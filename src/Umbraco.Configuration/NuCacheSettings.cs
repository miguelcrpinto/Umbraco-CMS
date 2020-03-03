﻿using System.Configuration;
using Umbraco.Abstractions;

namespace Umbraco.Configuration
{
    public class NuCacheSettings : INuCacheSettings
    {
        public NuCacheSettings()
        {
            BTreeBlockSize = ConfigurationManager.AppSettings["Umbraco.Web.PublishedCache.NuCache.BTree.BlockSize"];
        }
        public string BTreeBlockSize { get; }
    }
}
