﻿using Blazored.Modal;
using Blazored.Modal.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Newtonsoft.Json;
using OpenBullet2.Entities;
using OpenBullet2.Helpers;
using OpenBullet2.Logging;
using OpenBullet2.Models.Data;
using OpenBullet2.Models.Jobs;
using OpenBullet2.Repositories;
using OpenBullet2.Services;
using OpenBullet2.Shared.Forms;
using RuriLib.Logging;
using RuriLib.Models.Hits;
using RuriLib.Models.Jobs;
using RuriLib.Parallelization.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBullet2.Shared
{
    public partial class MultiRunJobViewer : IDisposable
    {
        [Parameter] public MultiRunJob Job { get; set; }

        [Inject] private IModalService Modal { get; set; }
        [Inject] private VolatileSettingsService VolatileSettings { get; set; }
        [Inject] private PersistentSettingsService PersistentSettings { get; set; }
        [Inject] private MemoryJobLogger Logger { get; set; }
        [Inject] private IJobRepository JobRepo { get; set; }
        [Inject] private JobManagerService JobManager { get; set; }
        [Inject] private NavigationManager Nav { get; set; }

        private bool changingBots = false;
        private string hitsFilter = "SUCCESS";
        private List<Hit> selectedHits = new();
        private Hit lastSelectedHit;
        private Timer uiRefreshTimer;

        protected override void OnInitialized() => AddEventHandlers();

        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                var interval = Math.Max(50, PersistentSettings.OpenBulletSettings.GeneralSettings.JobUpdateInterval);
                uiRefreshTimer = new Timer(new TimerCallback(async _ => await InvokeAsync(StateHasChanged)),
                    null, interval, interval);
            }
        }

        private async Task ChangeBots()
        {
            var parameters = new ModalParameters();
            parameters.Add(nameof(BotsSelector.Bots), Job.Bots);

            var modal = Modal.Show<BotsSelector>(Loc["EditBotsAmount"], parameters);
            var result = await modal.Result;

            if (!result.Cancelled)
            {
                var newAmount = (int)result.Data;
                changingBots = true;

                await Job.ChangeBots(newAmount);

                Job.Bots = newAmount;
                changingBots = false;
            }
        }

        private void ChangeOptions()
        {
            Nav.NavigateTo($"jobs/edit/{Job.Id}");
        }

        private void LogResult(object sender, ResultDetails<MultiRunInput, CheckResult> details)
        {
            var botData = details.Result.BotData;
            var data = botData.Line.Data;
            var proxy = botData.Proxy != null
                ? $"{botData.Proxy.Host}:{botData.Proxy.Port}"
                : string.Empty;

            var message = string.Format(Loc["LineCheckedMessage"], data, proxy, botData.STATUS);
            var color = botData.STATUS switch
            {
                "SUCCESS" => "yellowgreen",
                "FAIL" => "tomato",
                "BAN" => "plum",
                "RETRY" => "yellow",
                "ERROR" => "red",
                "NONE" => "skyblue",
                _ => "orange"
            };
            Logger.Log(Job.Id, message, LogKind.Custom, color);
        }

        private void PlaySoundOnHit(object sender, ResultDetails<MultiRunInput, CheckResult> details)
        {
            if (details.Result.BotData.STATUS == "SUCCESS" && PersistentSettings.OpenBulletSettings.CustomizationSettings.PlaySoundOnHit)
            {
                _ = js.InvokeVoidAsync("playHitSound");
            }
        }

        private void LogError(object sender, Exception ex)
        {
            Logger.LogError(Job.Id, $"{Loc["TaskManagerError"]} {ex.Message}");
        }

        private void LogTaskError(object sender, ErrorDetails<MultiRunInput> details)
        {
            var data = details.Item.BotData.Line.Data;
            var proxy = details.Item.BotData.Proxy != null
                ? $"{details.Item.BotData.Proxy.Host}:{details.Item.BotData.Proxy.Port}"
                : string.Empty;
            Logger.LogError(Job.Id, $"{Loc["TaskError"]} ({data})({proxy})! {details.Exception.Message}");
        }

        private void LogCompleted(object sender, EventArgs e)
        {
            Logger.LogInfo(Job.Id, Loc["TaskManagerCompleted"]);
        }

        private void SaveRecord(object sender, EventArgs e)
        {
            // Fire and forget
            JobManager.SaveRecord(Job).ConfigureAwait(false);
        }

        // TODO: Move this to a separate service! It has no point of being here!
        private void SaveJobOptions(object sender, EventArgs e)
        {
            JobEntity job = null;

            // Get the job
            lock (JobRepo)
            {
                job = JobRepo.Get(Job.Id).Result;
            }

            if (job == null || job.JobOptions == null)
            {
                Console.WriteLine("Skipped job options save because Job (or JobOptions) was null");
                return;
            }
            
            // Deserialize and unwrap the job options
            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
            var wrapper = JsonConvert.DeserializeObject<JobOptionsWrapper>(job.JobOptions, settings);
            var options = ((MultiRunJobOptions)wrapper.Options);
            
            // Check if it's valid
            if (string.IsNullOrEmpty(options.ConfigId))
            {
                Console.WriteLine("Skipped job options save because ConfigId was null");
                return;
            }

            if (options.DataPool is WordlistDataPoolOptions x && x.WordlistId == -1)
            {
                Console.WriteLine("Skipped job options save because WordlistId was -1");
                return;
            }

            // Update the skip (if not idle, also add the currently tested ones)
            options.Skip = Job.Status == JobStatus.Idle
                ? Job.Skip
                : Job.Skip + Job.DataTested;
            
            // Wrap and serialize again
            var newWrapper = new JobOptionsWrapper { Options = options };
            job.JobOptions = JsonConvert.SerializeObject(newWrapper, settings);

            // Update the job
            lock (JobRepo)
            {
                JobRepo.Update(job).Wait();
            }
        }

        private async Task Start()
        {
            try
            {
                await AskCustomInputs();

                Logger.LogInfo(Job.Id, Loc["StartedWaiting"]);
                await Job.Start();
                Logger.LogInfo(Job.Id, Loc["StartedChecking"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Stop()
        {
            try
            {
                Logger.LogInfo(Job.Id, Loc["SoftStopMessage"]);
                await Job.Stop();
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Abort()
        {
            try
            {
                Logger.LogInfo(Job.Id, Loc["HardStopMessage"]);
                await Job.Abort();
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Pause()
        {
            try
            {
                Logger.LogInfo(Job.Id, Loc["PauseMessage"]);
                await Job.Pause();
                Logger.LogInfo(Job.Id, Loc["TaskManagerPaused"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task Resume()
        {
            try
            {
                await Job.Resume();
                Logger.LogInfo(Job.Id, Loc["ResumeMessage"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task SkipWait()
        {
            try
            {
                Job.SkipWait();
                Logger.LogInfo(Job.Id, Loc["SkippedWait"]);
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task AskCustomInputs()
        {
            Job.CustomInputsAnswers.Clear();
            foreach (var input in Job.Config.Settings.InputSettings.CustomInputs)
            {
                var parameters = new ModalParameters();
                parameters.Add(nameof(CustomInputQuestion.Question), input.Description);
                parameters.Add(nameof(CustomInputQuestion.Answer), input.DefaultAnswer);

                var modal = Modal.Show<CustomInputQuestion>(Loc["CustomInput"], parameters);
                var result = await modal.Result;
                var answer = result.Cancelled ? input.DefaultAnswer : (string)result.Data;
                Job.CustomInputsAnswers[input.VariableName] = answer;
            }
        }

        private static string GetHitColor(Hit hit) => hit.Type switch
        {
            "SUCCESS" => "var(--fg-hit)",
            "NONE" => "var(--fg-tocheck)",
            _ => "var(--fg-custom)"
        };

        private void HitClicked(Hit hit, MouseEventArgs e)
        {
            // If we held down CTRL
            if (e.CtrlKey)
            {
                // If already selected, deselect
                if (selectedHits.Contains(hit))
                {
                    selectedHits.Remove(hit);
                }
                // Otherwise add to selected list
                else
                {
                    selectedHits.Add(hit);
                    lastSelectedHit = hit;
                }
            }
            // If we held down SHIFT
            else if (e.ShiftKey)
            {
                // If we never clicked anything, treat as normal click
                if (lastSelectedHit == null)
                {
                    lastSelectedHit = hit;
                    selectedHits.Clear();
                    selectedHits.Add(hit);
                }
                // Otherwise select the range from last selected hit
                else
                {
                    selectedHits.Clear();
                    var filteredHits = GetFilteredHits();
                    var lastIndex = filteredHits.IndexOf(lastSelectedHit);
                    var currentIndex = filteredHits.IndexOf(hit);
                    var rangeStartIndex = Math.Min(lastIndex, currentIndex);
                    var rangeEndIndex = Math.Max(lastIndex, currentIndex);

                    selectedHits.AddRange(filteredHits.Skip(rangeStartIndex).Take(rangeEndIndex - rangeStartIndex + 1));
                    lastSelectedHit = hit;
                }
            }
            else
            {
                lastSelectedHit = hit;
                selectedHits.Clear();
                selectedHits.Add(hit);
            }
        }

        private async Task CopyHitDataCapture()
        {
            if (selectedHits.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            var sb = new StringBuilder();
            selectedHits.ForEach(i => sb.AppendLine($"{i.Data.Data} | {i.CapturedDataString}"));

            try
            {
                await js.CopyToClipboard(sb.ToString());
            }
            catch
            {
                await js.AlertError(Loc["CopyToClipboardFailed"], Loc["CopyToClipboardFailedMessage"]);
            }
        }

        private async Task CopyHitData()
        {
            if (selectedHits.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            var sb = new StringBuilder();
            selectedHits.ForEach(i => sb.AppendLine(i.Data.Data));

            try
            {
                await js.CopyToClipboard(sb.ToString());
            }
            catch
            {
                await js.AlertError(Loc["CopyToClipboardFailed"], Loc["CopyToClipboardFailedMessage"]);
            }
        }

        private async Task SendToDebugger()
        {
            if (lastSelectedHit == null)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            VolatileSettings.DebuggerOptions.TestData = lastSelectedHit.Data.Data;
            VolatileSettings.DebuggerOptions.WordlistType = lastSelectedHit.Data.Type.Name;
            VolatileSettings.DebuggerOptions.UseProxy = lastSelectedHit.Proxy != null;

            if (lastSelectedHit.Proxy != null)
            {
                VolatileSettings.DebuggerOptions.ProxyType = lastSelectedHit.Proxy.Type;
                VolatileSettings.DebuggerOptions.TestProxy = lastSelectedHit.Proxy.ToString();
            }
        }

        private async Task ShowFullLog()
        {
            if (lastSelectedHit == null)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            if (lastSelectedHit.BotLogger == null)
            {
                await js.AlertError(Loc["Disabled"], Loc["BotLogDisabledError"]);
                return;
            }

            var parameters = new ModalParameters();
            parameters.Add(nameof(BotLoggerViewerModal.BotLogger), lastSelectedHit.BotLogger);

            Modal.Show<BotLoggerViewerModal>(Loc["BotLog"], parameters);
        }

        private void OnHitsFilterChanged(string value)
        {
            hitsFilter = value;
            StateHasChanged();
        }

        private List<Hit> GetFilteredHits() => hitsFilter switch
        {
            "SUCCESS" => Job.Hits.Where(h => h.Type == "SUCCESS").ToList(),
            "NONE" => Job.Hits.Where(h => h.Type == "NONE").ToList(),
            "CUSTOM" => Job.Hits.Where(h => h.Type != "SUCCESS" && h.Type != "NONE").ToList(),
            _ => throw new NotImplementedException()
        };

        private async Task ShowNoHitSelectedWarning()
            => await js.AlertError(Loc["Uh-Oh"], Loc["NoHitSelectedWarning"]);

        private void AddEventHandlers()
        {
            if (PersistentSettings.OpenBulletSettings.GeneralSettings.EnableJobLogging)
            {
                Job.OnResult += LogResult;
                Job.OnResult += PlaySoundOnHit;
                Job.OnTaskError += LogTaskError;
                Job.OnError += LogError;
                Job.OnCompleted += LogCompleted;
            }
            
            Job.OnCompleted += SaveRecord;
            Job.OnTimerTick += SaveRecord;
            Job.OnTimerTick += SaveJobOptions;
        }

        private void RemoveEventHandlers()
        {
            try
            {
                Job.OnResult -= LogResult;
                Job.OnResult -= PlaySoundOnHit;
                Job.OnTaskError -= LogTaskError;
                Job.OnError -= LogError;
                Job.OnCompleted -= LogCompleted;
            }
            catch
            {

            }

            try
            {
                Job.OnCompleted -= SaveRecord;
                Job.OnTimerTick -= SaveRecord;
                Job.OnTimerTick -= SaveJobOptions;
            }
            catch 
            {

            }
        }

        public void Dispose()
        {
            uiRefreshTimer?.Dispose();
            RemoveEventHandlers();
        }
    }
}
