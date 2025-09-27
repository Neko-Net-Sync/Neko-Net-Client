/*
    Neko-Net Client — Interop.DalamudLoggingProvider
    -----------------------------------------------
    Purpose
    - Logging provider bridge that produces <see cref="DalamudLogger"/> instances and routes
      Microsoft.Extensions.Logging to Dalamud's logging sink. Keeps logger category names compact
      and caches instances for reuse.
*/
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using System.Collections.Concurrent;

namespace NekoNetClient.Interop;

[ProviderAlias("Dalamud")]
public sealed class DalamudLoggingProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DalamudLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly MareConfigService _mareConfigService;
    private readonly IPluginLog _pluginLog;
    private readonly bool _hasModifiedGameFiles;

    /// <summary>
    /// Constructs the provider bound to the application's configuration and Dalamud log sink.
    /// </summary>
    /// <param name="mareConfigService">Provides the current log level configuration.</param>
    /// <param name="pluginLog">Dalamud logging sink.</param>
    /// <param name="hasModifiedGameFiles">Flag that, when true, annotates log lines as unsupported.</param>
    public DalamudLoggingProvider(MareConfigService mareConfigService, IPluginLog pluginLog, bool hasModifiedGameFiles)
    {
        _mareConfigService = mareConfigService;
        _pluginLog = pluginLog;
        _hasModifiedGameFiles = hasModifiedGameFiles;
    }

    /// <summary>
    /// Creates or reuses a compact-category logger instance.
    /// </summary>
    /// <param name="categoryName">Original category name (will be compacted to fit alignment).</param>
    /// <returns>Logger for the category.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        string catName = categoryName.Split(".", StringSplitOptions.RemoveEmptyEntries).Last();
        if (catName.Length > 15)
        {
            catName = string.Join("", catName.Take(6)) + "..." + string.Join("", catName.TakeLast(6));
        }
        else
        {
            catName = string.Join("", Enumerable.Range(0, 15 - catName.Length).Select(_ => " ")) + catName;
        }

        return _loggers.GetOrAdd(catName, name => new DalamudLogger(name, _mareConfigService, _pluginLog, _hasModifiedGameFiles));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loggers.Clear();
        GC.SuppressFinalize(this);
    }
}