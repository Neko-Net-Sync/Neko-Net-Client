//
// NekoNetClient — Services.Events.EventAggregator
// ------------------------------------------------------------
// Purpose:
//   Collects Event messages, maintains a rolling in-memory list, and persists them to disk
//   with daily log rotation. Exposes a Lazy list for UI consumption with cheap refresh checks.
//
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Utils;

namespace NekoNetClient.Services.Events;

/// <summary>
/// Aggregates <see cref="Event"/> messages published on the mediator, keeps a rolling in-memory
/// buffer, and writes events to daily-rotated log files. Intended for UI and diagnostics.
/// </summary>
public class EventAggregator : MediatorSubscriberBase, IHostedService
{
    private readonly RollingList<Event> _events = new(500);
    private readonly SemaphoreSlim _lock = new(1);
    private readonly string _configDirectory;
    private readonly ILogger<EventAggregator> _logger;

    /// <summary>Lazy snapshot of current events; re-created on new event to signal UI refresh.</summary>
    public Lazy<List<Event>> EventList { get; private set; }
    /// <summary>True when a new lazy snapshot should be requested by the UI.</summary>
    public bool NewEventsAvailable => !EventList.IsValueCreated;
    /// <summary>Directory where event logs are stored.</summary>
    public string EventLogFolder => Path.Combine(_configDirectory, "eventlog");
    private string CurrentLogName => $"{DateTime.Now:yyyy-MM-dd}-events.log";
    private DateTime _currentTime;

    /// <summary>
    /// Subscribes to <see cref="EventMessage"/> and sets up rolling storage and log folder paths.
    /// </summary>
    public EventAggregator(string configDirectory, ILogger<EventAggregator> logger, MareMediator mareMediator) : base(logger, mareMediator)
    {
        Mediator.Subscribe<EventMessage>(this, (msg) =>
        {
            _lock.Wait();
            try
            {
                Logger.LogTrace("Received Event: {evt}", msg.Event.ToString());
                _events.Add(msg.Event);
                WriteToFile(msg.Event);
            }
            finally
            {
                _lock.Release();
            }

            RecreateLazy();
        });

        EventList = CreateEventLazy();
        _configDirectory = configDirectory;
        _logger = logger;
        _currentTime = DateTime.Now - TimeSpan.FromDays(1);
    }

    private void RecreateLazy()
    {
        if (!EventList.IsValueCreated) return;

        EventList = CreateEventLazy();
    }

    private Lazy<List<Event>> CreateEventLazy()
    {
        return new Lazy<List<Event>>(() =>
        {
            _lock.Wait();
            try
            {
                return [.. _events];
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    private void WriteToFile(Event receivedEvent)
    {
        if (DateTime.Now.Day != _currentTime.Day)
        {
            try
            {
                _currentTime = DateTime.Now;
                var filesInDirectory = Directory.EnumerateFiles(EventLogFolder, "*.log");
                if (filesInDirectory.Skip(10).Any())
                {
                    File.Delete(filesInDirectory.OrderBy(f => new FileInfo(f).LastWriteTimeUtc).First());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete last events");
            }
        }

        var eventLogFile = Path.Combine(EventLogFolder, CurrentLogName);
        try
        {
            if (!Directory.Exists(EventLogFolder)) Directory.CreateDirectory(EventLogFolder);
            File.AppendAllLines(eventLogFile, [receivedEvent.ToString()]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Could not write to event file {eventLogFile}");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting EventAggregatorService");
        Logger.LogInformation("Started EventAggregatorService");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
