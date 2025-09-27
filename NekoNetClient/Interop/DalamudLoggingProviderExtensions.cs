/*
    Neko-Net Client — Interop.DalamudLoggingProviderExtensions
    ---------------------------------------------------------
    Purpose
    - Adds a convenience extension to wire up the Dalamud logging provider into the
      Microsoft.Extensions.Logging pipeline. Clears existing providers to avoid duplicate output.
*/
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;

namespace NekoNetClient.Interop;

public static class DalamudLoggingProviderExtensions
{
    /// <summary>
    /// Replaces existing providers and adds the <see cref="DalamudLoggingProvider"/> using the supplied sink.
    /// </summary>
    /// <param name="builder">Logging builder to configure.</param>
    /// <param name="pluginLog">Dalamud plugin log sink.</param>
    /// <param name="hasModifiedGameFiles">Whether to annotate log lines with an unsupported marker.</param>
    /// <returns>The same builder for chaining.</returns>
    public static ILoggingBuilder AddDalamudLogging(this ILoggingBuilder builder, IPluginLog pluginLog, bool hasModifiedGameFiles)
    {
        builder.ClearProviders();

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, DalamudLoggingProvider>
            (b => new DalamudLoggingProvider(b.GetRequiredService<MareConfigService>(), pluginLog, hasModifiedGameFiles)));
        return builder;
    }
}