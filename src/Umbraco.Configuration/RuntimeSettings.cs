﻿using System.Configuration;
using Umbraco.Abstractions;

namespace Umbraco.Configuration
{
    public class RuntimeSettings : IRuntimeSettings
    {
        public RuntimeSettings()
        {
            if (ConfigurationManager.GetSection("system.web/httpRuntime") is ConfigurationSection section)
            {
                var maxRequestLengthProperty = section.ElementInformation.Properties["maxRequestLength"];
                if (maxRequestLengthProperty != null && maxRequestLengthProperty.Value is int requestLength)
                {
                    MaxRequestLength = requestLength;
                }

                var maxQueryStringProperty = section.ElementInformation.Properties["maxQueryStringLength"];
                if (maxQueryStringProperty != null && maxQueryStringProperty.Value is int maxQueryStringLength)
                {
                    MaxQueryStringLength = maxQueryStringLength;
                }
            }
        }
        public int? MaxQueryStringLength { get; }
        public int? MaxRequestLength { get; }
        
    }
}
