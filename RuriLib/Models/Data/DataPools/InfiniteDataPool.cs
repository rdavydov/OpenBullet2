﻿using System.Collections.Generic;

namespace RuriLib.Models.Data.DataPools
{
    public class InfiniteDataPool : DataPool
    {
        /// <summary>
        /// Creates a DataPool of empty strings that never ends.
        /// </summary>
        public InfiniteDataPool(string wordlistType = "Default")
        {
            DataList = InfiniteCounter();
            Size = int.MaxValue;
            WordlistType = wordlistType;
        }

        private IEnumerable<string> InfiniteCounter()
        {
            while (true) yield return string.Empty;
        }
    }
}
