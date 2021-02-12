﻿using RuriLib.Attributes;
using RuriLib.Extensions;
using RuriLib.Functions.Files;
using RuriLib.Functions.Http;
using RuriLib.Functions.Http.Options;
using RuriLib.Helpers;
using RuriLib.Http;
using RuriLib.Http.Models;
using RuriLib.Logging;
using RuriLib.Models.Blocks.Custom.HttpRequest.Multipart;
using RuriLib.Models.Bots;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RuriLib.Blocks.Requests.Http
{
    [BlockCategory("Http", "Blocks for performing Http requests", "#32cd32")]
    public static class Methods
    {
        private static readonly string[] commaHeaders = new[] { "Accept", "Accept-Encoding" };

        // STANDARD REQUESTS
        // This method is accessed via a custom descriptor so it must not be an auto block
        public static async Task HttpRequestStandard(BotData data, StandardHttpRequestOptions options)
        {
            var clientOptions = GetClientOptions(data, options);
            using var client = HttpFactory.GetRLHttpClient(data.UseProxy ? data.Proxy : null, clientOptions);

            using var request = new HttpRequest
            {
                Method = new System.Net.Http.HttpMethod(options.Method.ToString()),
                Uri = new Uri(options.Url),
                Version = Version.Parse(options.HttpVersion),
                Headers = options.CustomHeaders
            };

            if (!string.IsNullOrEmpty(options.Content) || options.AlwaysSendContent)
            {
                request.Content = new StringContent(options.Content.Unescape());
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);
            }

            data.Logger.LogHeader();
            
            try
            {
                Activity.Current = null;
                var timeoutCts = new CancellationTokenSource(options.TimeoutMilliseconds);
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken, timeoutCts.Token);
                var response = await client.SendAsync(request, linkedCts.Token);
                LogHttpRequestData(data, client);
                await LogHttpResponseData(data, response, request);
            }
            catch
            {
                LogHttpRequestData(data, request);
                throw;
            }
            finally
            {
                request.Dispose();
                client.Dispose();
            }
        }

        // RAW REQUESTS
        // This method is accessed via a custom descriptor so it must not be an auto block
        public static async Task HttpRequestRaw(BotData data, RawHttpRequestOptions options)
        {
            var clientOptions = GetClientOptions(data, options);
            using var client = HttpFactory.GetRLHttpClient(data.UseProxy ? data.Proxy : null, clientOptions);

            using var request = new HttpRequest
            {
                Method = new System.Net.Http.HttpMethod(options.Method.ToString()),
                Uri = new Uri(options.Url),
                Version = Version.Parse(options.HttpVersion),
                Headers = options.CustomHeaders,
                Content = new ByteArrayContent(options.Content)
            };

            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);

            data.Logger.LogHeader();

            try
            {
                Activity.Current = null;
                var timeoutCts = new CancellationTokenSource(options.TimeoutMilliseconds);
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken, timeoutCts.Token);
                var response = await client.SendAsync(request, linkedCts.Token);
                LogHttpRequestData(data, client);
                await LogHttpResponseData(data, response, request);
            }
            catch
            {
                LogHttpRequestData(data, request);
                throw;
            }
            finally
            {
                request.Dispose();
                client.Dispose();
            }
        }

        // BASIC AUTH
        // This method is accessed via a custom descriptor so it must not be an auto block
        public static async Task HttpRequestBasicAuth(BotData data, BasicAuthHttpRequestOptions options)
        {
            var clientOptions = GetClientOptions(data, options);
            using var client = HttpFactory.GetRLHttpClient(data.UseProxy ? data.Proxy : null, clientOptions);

            using var request = new HttpRequest
            {
                Method = System.Net.Http.HttpMethod.Get,
                Uri = new Uri(options.Url),
                Version = Version.Parse(options.HttpVersion),
                Headers = options.CustomHeaders
            };

            // Add the basic auth header
            request.AddHeader("Authorization", "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));

            data.Logger.LogHeader();
            
            try
            {
                Activity.Current = null;
                var timeoutCts = new CancellationTokenSource(options.TimeoutMilliseconds);
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken, timeoutCts.Token);
                var response = await client.SendAsync(request, linkedCts.Token);
                LogHttpRequestData(data, client);
                await LogHttpResponseData(data, response, request);
            }
            catch
            {
                LogHttpRequestData(data, request);
                throw;
            }
            finally
            {
                request.Dispose();
                client.Dispose();
            }
        }

        // MULTIPART
        // This method is accessed via a custom descriptor so it must not be an auto block
        public static async Task HttpRequestMultipart(BotData data, MultipartHttpRequestOptions options)
        {
            var clientOptions = GetClientOptions(data, options);
            using var client = HttpFactory.GetRLHttpClient(data.UseProxy ? data.Proxy : null, clientOptions);

            if (string.IsNullOrWhiteSpace(options.Boundary))
                options.Boundary = GenerateMultipartBoundary();
            
            var multipartContent = new MultipartFormDataContent(options.Boundary);

            FileStream fileStream = null;

            foreach (var c in options.Contents)
            {
                switch (c)
                {
                    case StringHttpContent x:
                        multipartContent.Add(new StringContent(x.Data, Encoding.UTF8, x.ContentType), x.Name);
                        break;

                    case RawHttpContent x:
                        var byteContent = new ByteArrayContent(x.Data);
                        byteContent.Headers.ContentType = new MediaTypeHeaderValue(x.ContentType);
                        multipartContent.Add(byteContent, x.Name);
                        break;

                    case FileHttpContent x:
                        lock (FileLocker.GetHandle(x.FileName))
                        {
                            fileStream = new FileStream(x.FileName, FileMode.Open);
                            var fileContent = CreateFileContent(fileStream, x.Name, Path.GetFileName(x.FileName), x.ContentType);
                            multipartContent.Add(fileContent, x.Name);
                        }
                        break;
                }
            }

            using var request = new HttpRequest
            {
                Method = new System.Net.Http.HttpMethod(options.Method.ToString()),
                Uri = new Uri(options.Url),
                Version = Version.Parse(options.HttpVersion),
                Headers = options.CustomHeaders,
                Content = multipartContent
            };

            data.Logger.LogHeader();

            try
            {
                Activity.Current = null;
                var timeoutCts = new CancellationTokenSource(options.TimeoutMilliseconds);
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken, timeoutCts.Token);
                var response = await client.SendAsync(request, linkedCts.Token);
                LogHttpRequestData(data, client);
                await LogHttpResponseData(data, response, request);
            }
            catch
            {
                LogHttpRequestData(data, request, options.Boundary, options.Contents);
                throw;
            }
            finally
            {
                if (fileStream != null)
                    fileStream.Dispose();

                request.Dispose();
                client.Dispose();
            }
        }

        private static void LogHttpRequestData(BotData data, HttpRequest request,
            string boundary = null, List<MyHttpContent> multipartContents = null)
        {
            using var writer = new StringWriter();

            // Log the method, uri and http version
            writer.WriteLine($"{request.Method.Method} {request.Uri.PathAndQuery} HTTP/{request.Version.Major}.{request.Version.Minor}");

            // Log the headers
            if (!request.HeaderExists("Host", out _))
                writer.WriteLine($"Host: {request.Uri.Host}");

            foreach (var header in request.Headers)
            {
                var separator = commaHeaders.Contains(header.Key) ? ", " : " ";
                writer.WriteLine($"{header.Key}: {string.Join(separator, header.Value)}");
            }

            // Log the cookie header
            var cookies = RuriLib.Functions.Http.Http.GetAllCookies((CookieContainer)data.Objects["cookieContainer"])
                .Select(c => $"{c.Name}={c.Value}");
            
            if (cookies.Any())
                writer.WriteLine($"Cookie: {string.Join("; ", cookies)}");

            if (request.Content != null)
            {
                switch (request.Content)
                {
                    case StringContent x:
                        writer.WriteLine($"Content-Type: {x.Headers.ContentType}");
                        writer.WriteLine($"Content-Length: {x.Headers.ContentLength}");
                        writer.WriteLine();
                        writer.WriteLine(x.ReadAsStringAsync().Result);
                        break;

                    case ByteArrayContent x:
                        writer.WriteLine($"Content-Type: {x.Headers.ContentType}");
                        writer.WriteLine($"Content-Length: {x.Headers.ContentLength}");
                        writer.WriteLine();
                        writer.WriteLine(RuriLib.Functions.Conversion.HexConverter.ToHexString(x.ReadAsByteArrayAsync().Result));
                        break;

                    case MultipartFormDataContent x:
                        writer.WriteLine($"Content-Type: multipart/form-data; boundary=\"{boundary}\"");
                        var serializedMultipart = SerializeMultipart(boundary, multipartContents);
                        writer.WriteLine($"Content-Length: (not calculated)");
                        writer.WriteLine();
                        writer.WriteLine(serializedMultipart);
                        break;
                }
            }

            data.Logger.Log(writer.ToString(), LogColors.NonPhotoBlue);
        }

        private static void LogHttpRequestData(BotData data, RLHttpClient client)
        {
            for (var i = 0; i < client.RawRequests.Count; i++)
            {
                if (i > 0)
                {
                    data.Logger.Log($"Redirect {i}", LogColors.Beige);
                }

                data.Logger.Log(Encoding.ASCII.GetString(client.RawRequests[i]), LogColors.NonPhotoBlue);
            }
        }

        private static string SerializeMultipart(string boundary, List<MyHttpContent> contents)
        {
            using var writer = new StringWriter();

            foreach (var content in contents)
            {
                writer.WriteLine(boundary);

                switch (content)
                {
                    case StringHttpContent x:
                        writer.WriteLine($"Content-Disposition: form-data; name={x.Name}");
                        writer.WriteLine($"Content-Type: {GetMediaHeaderString(x.ContentType)}");
                        writer.WriteLine();
                        writer.WriteLine(x.Data);
                        break;

                    case RawHttpContent x:
                        writer.WriteLine($"Content-Disposition: form-data; name={x.Name}");
                        writer.WriteLine($"Content-Type: {GetMediaHeaderString(x.ContentType)}");
                        writer.WriteLine();
                        writer.WriteLine(Encoding.UTF8.GetString(x.Data));
                        break;

                    case FileHttpContent x:
                        writer.WriteLine($"Content-Disposition: form-data; name=\"{x.Name}\"; filename=\"{Path.GetFileName(x.FileName)}\"");
                        writer.WriteLine($"Content-Type: {GetMediaHeaderString(x.ContentType)}");
                        writer.WriteLine();
                        writer.WriteLine("[FILE CONTENTS NOT LOGGED]");
                        break;
                }
            }
            
            writer.WriteLine(boundary);

            return writer.ToString();
        }

        private static async Task LogHttpResponseData(BotData data, HttpResponseMessage response, HttpRequest request)
        {
            // Read the raw source for Content-Length calculation
            data.RAWSOURCE = await response.Content.ReadAsByteArrayAsync(data.CancellationToken);

            // Address
            var uri = response.RequestMessage.RequestUri;
            if (!uri.IsAbsoluteUri)
                uri = new Uri(request.Uri, uri);
            data.ADDRESS = response.RequestMessage.RequestUri.AbsoluteUri;
            data.Logger.Log($"Address: {data.ADDRESS}", LogColors.DodgerBlue);

            // Response code
            data.RESPONSECODE = (int)response.StatusCode;
            data.Logger.Log($"Response code: {data.RESPONSECODE}", LogColors.Citrine);

            // Headers
            data.HEADERS = response.Headers.ToDictionary(h => h.Key, h => h.Value.First());
            if (!data.HEADERS.ContainsKey("Content-Length"))
                data.HEADERS["Content-Length"] = data.RAWSOURCE.Length.ToString();
            data.Logger.Log("Received Headers:", LogColors.MediumPurple);
            data.Logger.Log(data.HEADERS.Select(h => $"{h.Key}: {h.Value}"), LogColors.Violet);

            // Cookies
            var allCookies = RuriLib.Functions.Http.Http.GetAllCookies((CookieContainer)data.Objects["cookieContainer"]);
            data.COOKIES.Clear();
            foreach (Cookie cookie in allCookies)
                data.COOKIES[cookie.Name] = cookie.Value;
            data.Logger.Log("Received Cookies:", LogColors.MikadoYellow);
            data.Logger.Log(data.COOKIES.Select(h => $"{h.Key}: {h.Value}"), LogColors.Khaki);

            // Unzip the GZipped content if still gzipped (after Content-Length calculation)
            if (data.RAWSOURCE.Length > 1 && data.RAWSOURCE[0] == 0x1F && data.RAWSOURCE[1] == 0x8B)
            {
                try
                {
                    data.RAWSOURCE = GZip.Unzip(data.RAWSOURCE);
                }
                catch
                {
                    data.Logger.Log("Tried to unzip but failed", LogColors.DarkOrange);
                }
            }

            // Source
            data.SOURCE = Encoding.UTF8.GetString(data.RAWSOURCE);
            data.Logger.Log("Received Payload:", LogColors.ForestGreen);
            data.Logger.Log(data.SOURCE, LogColors.GreenYellow, true);
        }

        /// <summary>
        /// Generates a random string to be used for boundary.
        /// </summary>
        private static string GenerateMultipartBoundary()
        {
            var builder = new StringBuilder();
            var random = new Random();
            char ch;
            for (var i = 0; i < 16; i++)
            {
                ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65)));
                builder.Append(ch);
            }
            return $"------WebKitFormBoundary{builder.ToString().ToLower()}";
        }

        private static StreamContent CreateFileContent(Stream stream, string fieldName, string fileName, string contentType)
        {
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = $"\"{fieldName}\"",  
                FileName = $"\"{fileName}\""
            }; // the extra quotes are key here
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return fileContent;
        }

        private static string GetMediaHeaderString(string contentType)
            => new MediaTypeHeaderValue(contentType).ToString();

        private static TlsCipherSuite[] ParseCipherSuites(List<string> cipherSuites)
        {
            if (cipherSuites == null)
            {
                return Array.Empty<TlsCipherSuite>();
            }

            var parsed = new List<TlsCipherSuite>();

            foreach (var suite in cipherSuites)
            {
                try
                {
                    parsed.Add(Enum.Parse<TlsCipherSuite>(suite));
                }
                catch
                {
                    throw new NotSupportedException($"Cipher suite not supported: {suite}");
                }
            }

            return parsed.ToArray();
        }

        private static HttpOptions GetClientOptions(BotData data,
            RuriLib.Functions.Http.Options.HttpRequestOptions options)
        {
            var cookies = new CookieContainer();
            data.Objects["cookieContainer"] = cookies;

            foreach (var cookie in data.COOKIES)
                cookies.Add(new Uri(options.Url), new Cookie(cookie.Key, cookie.Value));

            foreach (var cookie in options.CustomCookies)
                cookies.Add(new Uri(options.Url), new Cookie(cookie.Key, cookie.Value));

            return new()
            {
                Cookies = cookies,
                ConnectTimeout = data.Providers.ProxySettings.ConnectTimeout,
                ReadWriteTimeout = data.Providers.ProxySettings.ReadWriteTimeout,
                AutoRedirect = options.AutoRedirect,
                MaxNumberOfRedirects = options.MaxNumberOfRedirects,
                SecurityProtocol = options.SecurityProtocol,
                UseCustomCipherSuites = options.UseCustomCipherSuites,
                CustomCipherSuites = ParseCipherSuites(options.CustomCipherSuites),
                CertRevocationMode = data.Providers.Security.X509RevocationMode
            };
        }
    }
}
