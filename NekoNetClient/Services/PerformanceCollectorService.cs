/*
    Neko-Net Client — Services.PerformanceCollectorService
    -----------------------------------------------------
    Purpose
    - Lightweight performance probe utility to time critical sections and optionally log them for diagnostics.
*/
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Utils;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NekoNetClient.Services;

/// <summary>
/// Lightweight performance probe that can wrap actions and functions, recording execution times into
/// rolling counters for optional diagnostics and logging. Enabled via configuration.
/// </summary>
public sealed class PerformanceCollectorService : IHostedService
{
    private const string _counterSplit = "=>";
    private readonly ILogger<PerformanceCollectorService> _logger;
    private readonly MareConfigService _mareConfigService;
    public ConcurrentDictionary<string, RollingList<(TimeOnly, long)>> PerformanceCounters { get; } = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _periodicLogPruneTaskCts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PerformanceCollectorService"/>.
    /// </summary>
    /// <param name="logger">Logger used for status and spike warnings (debug builds).</param>
    /// <param name="mareConfigService">Configuration to enable/disable performance collection.</param>
    public PerformanceCollectorService(ILogger<PerformanceCollectorService> logger, MareConfigService mareConfigService)
    {
        _logger = logger;
        _mareConfigService = mareConfigService;
    }

    /// <summary>
    /// Times a function call and returns its result, recording the elapsed ticks into a rolling list
    /// keyed by the sender type and supplied counter name.
    /// </summary>
    /// <typeparam name="T">Return type of the function.</typeparam>
    /// <param name="sender">The caller used for the counter prefix.</param>
    /// <param name="counterName">Interpolated counter name providing additional context.</param>
    /// <param name="func">The function to invoke and time.</param>
    /// <param name="maxEntries">Maximum entries to retain for this counter.</param>
    /// <returns>The function's result.</returns>
    public T LogPerformance<T>(object sender, MareInterpolatedStringHandler counterName, Func<T> func, int maxEntries = 10000)
    {
        if (!_mareConfigService.Current.LogPerformance) return func.Invoke();

        string cn = sender.GetType().Name + _counterSplit + counterName.BuildMessage();

        if (!PerformanceCounters.TryGetValue(cn, out var list))
        {
            list = PerformanceCounters[cn] = new(maxEntries);
        }

        var dt = DateTime.UtcNow.Ticks;
        try
        {
            return func.Invoke();
        }
        finally
        {
            var elapsed = DateTime.UtcNow.Ticks - dt;
#if DEBUG
            if (TimeSpan.FromTicks(elapsed) > TimeSpan.FromMilliseconds(10))
                _logger.LogWarning(">10ms spike on {counterName}: {time}", cn, TimeSpan.FromTicks(elapsed));
#endif
            list.Add((TimeOnly.FromDateTime(DateTime.Now), elapsed));
        }
    }

    /// <summary>
    /// Times an action and records the elapsed ticks into a rolling list keyed by the sender type
    /// and supplied counter name.
    /// </summary>
    /// <param name="sender">The caller used for the counter prefix.</param>
    /// <param name="counterName">Interpolated counter name providing additional context.</param>
    /// <param name="act">The action to invoke and time.</param>
    /// <param name="maxEntries">Maximum entries to retain for this counter.</param>
    public void LogPerformance(object sender, MareInterpolatedStringHandler counterName, Action act, int maxEntries = 10000)
    {
        if (!_mareConfigService.Current.LogPerformance) { act.Invoke(); return; }

        var cn = sender.GetType().Name + _counterSplit + counterName.BuildMessage();

        if (!PerformanceCounters.TryGetValue(cn, out var list))
        {
            list = PerformanceCounters[cn] = new(maxEntries);
        }

        var dt = DateTime.UtcNow.Ticks;
        try
        {
            act.Invoke();
        }
        finally
        {
            var elapsed = DateTime.UtcNow.Ticks - dt;
#if DEBUG
            if (TimeSpan.FromTicks(elapsed) > TimeSpan.FromMilliseconds(10))
                _logger.LogWarning(">10ms spike on {counterName}: {time}", cn, TimeSpan.FromTicks(elapsed));
#endif
            list.Add(new(TimeOnly.FromDateTime(DateTime.Now), elapsed));
        }
    }

    /// <summary>
    /// Starts background maintenance and pruning for stale counters.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting PerformanceCollectorService");
        _ = Task.Run(PeriodicLogPrune, _periodicLogPruneTaskCts.Token);
        _logger.LogInformation("Started PerformanceCollectorService");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops background maintenance and releases associated resources.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _periodicLogPruneTaskCts.Cancel();
        _periodicLogPruneTaskCts.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds and logs a table-like summary of performance counters, optionally limited to recent entries.
    /// </summary>
    /// <param name="limitBySeconds">If greater than 0, only entries within this window are considered.</param>
    internal void PrintPerformanceStats(int limitBySeconds = 0)
    {
        if (!_mareConfigService.Current.LogPerformance)
        {
            _logger.LogWarning("Performance counters are disabled");
        }

        StringBuilder sb = new();
        if (limitBySeconds > 0)
        {
            sb.AppendLine($"Performance Metrics over the past {limitBySeconds} seconds of each counter");
        }
        else
        {
            sb.AppendLine("Performance metrics over total lifetime of each counter");
        }
        var data = PerformanceCounters.ToList();
        var longestCounterName = data.OrderByDescending(d => d.Key.Length).First().Key.Length + 2;
        sb.Append("-Last".PadRight(15, '-'));
        sb.Append('|');
        sb.Append("-Max".PadRight(15, '-'));
        sb.Append('|');
        sb.Append("-Average".PadRight(15, '-'));
        sb.Append('|');
        sb.Append("-Last Update".PadRight(15, '-'));
        sb.Append('|');
        sb.Append("-Entries".PadRight(10, '-'));
        sb.Append('|');
        sb.Append("-Counter Name".PadRight(longestCounterName, '-'));
        sb.AppendLine();
        var orderedData = data.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var previousCaller = orderedData[0].Key.Split(_counterSplit, StringSplitOptions.RemoveEmptyEntries)[0];
        foreach (var entry in orderedData)
        {
            var newCaller = entry.Key.Split(_counterSplit, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.Equals(previousCaller, newCaller, StringComparison.Ordinal))
            {
                DrawSeparator(sb, longestCounterName);
            }

            var pastEntries = limitBySeconds > 0 ? entry.Value.Where(e => e.Item1.AddMinutes(limitBySeconds / 60.0d) >= TimeOnly.FromDateTime(DateTime.Now)).ToList() : [.. entry.Value];

            if (pastEntries.Any())
            {
                sb.Append((" " + TimeSpan.FromTicks(pastEntries.LastOrDefault() == default ? 0 : pastEntries.Last().Item2).TotalMilliseconds.ToString("0.00000", CultureInfo.InvariantCulture)).PadRight(15));
                sb.Append('|');
                sb.Append((" " + TimeSpan.FromTicks(pastEntries.Max(m => m.Item2)).TotalMilliseconds.ToString("0.00000", CultureInfo.InvariantCulture)).PadRight(15));
                sb.Append('|');
                sb.Append((" " + TimeSpan.FromTicks((long)pastEntries.Average(m => m.Item2)).TotalMilliseconds.ToString("0.00000", CultureInfo.InvariantCulture)).PadRight(15));
                sb.Append('|');
                sb.Append((" " + (pastEntries.LastOrDefault() == default ? "-" : pastEntries.Last().Item1.ToString("HH:mm:ss.ffff", CultureInfo.InvariantCulture))).PadRight(15, ' '));
                sb.Append('|');
                sb.Append((" " + pastEntries.Count).PadRight(10));
                sb.Append('|');
                sb.Append(' ').Append(entry.Key);
                sb.AppendLine();
            }

            previousCaller = newCaller;
        }

        DrawSeparator(sb, longestCounterName);

        _logger.LogInformation("{perf}", sb.ToString());
    }

    /// <summary>
    /// Appends a separator row to the performance table.
    /// </summary>
    private static void DrawSeparator(StringBuilder sb, int longestCounterName)
    {
        sb.Append("".PadRight(15, '-'));
        sb.Append('+');
        sb.Append("".PadRight(15, '-'));
        sb.Append('+');
        sb.Append("".PadRight(15, '-'));
        sb.Append('+');
        sb.Append("".PadRight(15, '-'));
        sb.Append('+');
        sb.Append("".PadRight(10, '-'));
        sb.Append('+');
        sb.Append("".PadRight(longestCounterName, '-'));
        sb.AppendLine();
    }

    /// <summary>
    /// Periodically prunes counters that haven't received updates for a fixed time window to keep memory usage bounded.
    /// </summary>
    private async Task PeriodicLogPrune()
    {
        while (!_periodicLogPruneTaskCts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), _periodicLogPruneTaskCts.Token).ConfigureAwait(false);

            foreach (var entries in PerformanceCounters.ToList())
            {
                try
                {
                    var last = entries.Value.ToList().Last();
                    if (last.Item1.AddMinutes(10) < TimeOnly.FromDateTime(DateTime.Now) && !PerformanceCounters.TryRemove(entries.Key, out _))
                    {
                        _logger.LogDebug("Could not remove performance counter {counter}", entries.Key);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Error removing performance counter {counter}", entries.Key);
                }
            }
        }
    }
}