﻿using NekoNetClient.MareConfiguration.Configurations;

namespace NekoNetClient.MareConfiguration;

public static class ConfigurationExtensions
{
    public static bool HasValidSetup(this MareConfig configuration)
    {
        return configuration.AcceptedAgreement && configuration.InitialScanComplete
                    && !string.IsNullOrEmpty(configuration.CacheFolder)
                    && Directory.Exists(configuration.CacheFolder);
    }
}