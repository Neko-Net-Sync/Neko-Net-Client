/*
    Neko-Net Client — Interop.DalamudLogger
    --------------------------------------
    Purpose
    - Adapter to bridge Microsoft.Extensions.Logging to Dalamud's IPluginLog. Formats messages,
      respects user-configured log level, and annotates logs when unsupported game modifications
      are detected. Keeps categories compact to fit DTR.
*/
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using System.Text;

namespace NekoNetClient.Interop;

/// <summary>
/// ILogger implementation that writes to Dalamud's <see cref="IPluginLog"/>.
/// This keeps a compact category, applies the configured log level from <see cref="MareConfigService"/>,
/// and annotates messages when running with modified game files to clearly mark unsupported scenarios.
/// </summary>
internal sealed class DalamudLogger : ILogger
{
    private readonly MareConfigService _mareConfigService;
    private readonly string _name;
    private readonly IPluginLog _pluginLog;
    private readonly bool _hasModifiedGameFiles;

    /// <summary>
    /// Creates a new <see cref="DalamudLogger"/> for a specific category.
    /// </summary>
    /// <param name="name">Category name (already compacted by the provider).</param>
    /// <param name="mareConfigService">Config service providing the current <c>LogLevel</c>.</param>
    /// <param name="pluginLog">Dalamud plugin logger sink.</param>
    /// <param name="hasModifiedGameFiles">Whether the client detected modified game files.</param>
    public DalamudLogger(string name, MareConfigService mareConfigService, IPluginLog pluginLog, bool hasModifiedGameFiles)
    {
        _name = name;
        _mareConfigService = mareConfigService;
        _pluginLog = pluginLog;
        _hasModifiedGameFiles = hasModifiedGameFiles;
    }

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return (int)_mareConfigService.Current.LogLevel <= (int)logLevel;
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string unsupported = _hasModifiedGameFiles ? "[UNSUPPORTED]" : string.Empty;

        if ((int)logLevel <= (int)LogLevel.Information)
            _pluginLog.Information($"{unsupported}[{_name}]{{{(int)logLevel}}} {state}{(_hasModifiedGameFiles ? "." : string.Empty)}");
        else
        {
            StringBuilder sb = new();
            sb.Append($"{unsupported}[{_name}]{{{(int)logLevel}}} {state}{(_hasModifiedGameFiles ? "." : string.Empty)} {exception?.Message}");
            if (!string.IsNullOrWhiteSpace(exception?.StackTrace))
                sb.AppendLine(exception?.StackTrace);
            var innerException = exception?.InnerException;
            while (innerException != null)
            {
                sb.AppendLine($"InnerException {innerException}: {innerException.Message}");
                sb.AppendLine(innerException.StackTrace);
                innerException = innerException.InnerException;
            }
            if (logLevel == LogLevel.Warning)
                _pluginLog.Warning(sb.ToString());
            else if (logLevel == LogLevel.Error)
                _pluginLog.Error(sb.ToString());
            else
                _pluginLog.Fatal(sb.ToString());
        }
    }
}