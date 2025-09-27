﻿/*
     Neko-Net Client — Services.Mediator.MareMediator
     ------------------------------------------------
     Purpose
     - Central message bus for the client, providing pub/sub semantics with optional same-thread execution and
         background queue processing.

     Behavior
     - Subscribers are registered per message type; messages can request same-thread execution via KeepThreadContext
         or be enqueued and processed asynchronously.
     - Execution is ordered to prioritize IHighPriorityMediatorSubscriber instances and supports performance logging.
     - Errors are rate-limited per-subscriber to avoid log spam.
*/
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace NekoNetClient.Services.Mediator;

/// <summary>
/// Lightweight mediator/event aggregator for delivering <see cref="MessageBase"/> payloads to subscribed
/// components. Supports background queue processing, optional same-thread dispatch, and priority ordering.
/// </summary>
public sealed class MareMediator : IHostedService
{
    private readonly object _addRemoveLock = new();
    private readonly ConcurrentDictionary<object, DateTime> _lastErrorTime = [];
    private readonly ILogger<MareMediator> _logger;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly ConcurrentQueue<MessageBase> _messageQueue = new();
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly MareConfigService _mareConfigService;
    private readonly ConcurrentDictionary<Type, HashSet<SubscriberAction>> _subscriberDict = [];
    private bool _processQueue = false;
    private readonly ConcurrentDictionary<Type, MethodInfo?> _genericExecuteMethods = new();
    public MareMediator(ILogger<MareMediator> logger, PerformanceCollectorService performanceCollector, MareConfigService mareConfigService)
    {
        _logger = logger;
        _performanceCollector = performanceCollector;
        _mareConfigService = mareConfigService;
    }

    public void PrintSubscriberInfo()
    {
        foreach (var subscriber in _subscriberDict.SelectMany(c => c.Value.Select(v => v.Subscriber))
            .DistinctBy(p => p).OrderBy(p => p.GetType().FullName, StringComparer.Ordinal).ToList())
        {
            _logger.LogInformation("Subscriber {type}: {sub}", subscriber.GetType().Name, subscriber.ToString());
            StringBuilder sb = new();
            sb.Append("=> ");
            foreach (var item in _subscriberDict.Where(item => item.Value.Any(v => v.Subscriber == subscriber)).ToList())
            {
                sb.Append(item.Key.Name).Append(", ");
            }

            if (!string.Equals(sb.ToString(), "=> ", StringComparison.Ordinal))
                _logger.LogInformation("{sb}", sb.ToString());
            _logger.LogInformation("---");
        }
    }

    /// <summary>
    /// Publishes a message. If <see cref="MessageBase.KeepThreadContext"/> is true, executes immediately on the
    /// current thread; otherwise, enqueues for background processing.
    /// </summary>
    public void Publish<T>(T message) where T : MessageBase
    {
        if (message.KeepThreadContext)
        {
            ExecuteMessage(message);
        }
        else
        {
            _messageQueue.Enqueue(message);
        }
    }

    /// <summary>
    /// Starts the background processing loop and initializes the mediator.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MareMediator");

        _ = Task.Run(async () =>
        {
            while (!_loopCts.Token.IsCancellationRequested)
            {
                while (!_processQueue)
                {
                    await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);
                }

                await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);

                HashSet<MessageBase> processedMessages = [];
                while (_messageQueue.TryDequeue(out var message))
                {
                    if (processedMessages.Contains(message)) { continue; }
                    processedMessages.Add(message);

                    ExecuteMessage(message);
                }
            }
        });

        _logger.LogInformation("Started MareMediator");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops background processing and clears the message queue.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _messageQueue.Clear();
        _loopCts.Cancel();
        _loopCts.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes a handler for the specified message type.
    /// </summary>
    public void Subscribe<T>(IMediatorSubscriber subscriber, Action<T> action) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            _subscriberDict.TryAdd(typeof(T), []);

            if (!_subscriberDict[typeof(T)].Add(new(subscriber, action)))
            {
                throw new InvalidOperationException("Already subscribed");
            }
        }
    }

    /// <summary>
    /// Unsubscribes a previously registered handler for the specified message type.
    /// </summary>
    public void Unsubscribe<T>(IMediatorSubscriber subscriber) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            if (_subscriberDict.ContainsKey(typeof(T)))
            {
                _subscriberDict[typeof(T)].RemoveWhere(p => p.Subscriber == subscriber);
            }
        }
    }

    internal void UnsubscribeAll(IMediatorSubscriber subscriber)
    {
        lock (_addRemoveLock)
        {
            foreach (Type kvp in _subscriberDict.Select(k => k.Key))
            {
                int unSubbed = _subscriberDict[kvp]?.RemoveWhere(p => p.Subscriber == subscriber) ?? 0;
                if (unSubbed > 0)
                {
                    _logger.LogDebug("{sub} unsubscribed from {msg}", subscriber.GetType().Name, kvp.Name);
                }
            }
        }
    }

    private void ExecuteMessage(MessageBase message)
    {
        if (!_subscriberDict.TryGetValue(message.GetType(), out HashSet<SubscriberAction>? subscribers) || subscribers == null || !subscribers.Any()) return;

        List<SubscriberAction> subscribersCopy = [];
        lock (_addRemoveLock)
        {
            subscribersCopy = subscribers?.Where(s => s.Subscriber != null).OrderBy(k => k.Subscriber is IHighPriorityMediatorSubscriber ? 0 : 1).ToList() ?? [];
        }

#pragma warning disable S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
        var msgType = message.GetType();
        if (!_genericExecuteMethods.TryGetValue(msgType, out var methodInfo))
        {
            _genericExecuteMethods[msgType] = methodInfo = GetType()
                 .GetMethod(nameof(ExecuteReflected), BindingFlags.NonPublic | BindingFlags.Instance)?
                 .MakeGenericMethod(msgType);
        }

        methodInfo!.Invoke(this, [subscribersCopy, message]);
#pragma warning restore S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
    }

    private void ExecuteReflected<T>(List<SubscriberAction> subscribers, T message) where T : MessageBase
    {
        foreach (SubscriberAction subscriber in subscribers)
        {
            try
            {
                if (_mareConfigService.Current.LogPerformance)
                {
                    var isSameThread = message.KeepThreadContext ? "$" : string.Empty;
                    _performanceCollector.LogPerformance(this, $"{isSameThread}Execute>{message.GetType().Name}+{subscriber.Subscriber.GetType().Name}>{subscriber.Subscriber}",
                        () => ((Action<T>)subscriber.Action).Invoke(message));
                }
                else
                {
                    ((Action<T>)subscriber.Action).Invoke(message);
                }
            }
            catch (Exception ex)
            {
                if (_lastErrorTime.TryGetValue(subscriber, out var lastErrorTime) && lastErrorTime.Add(TimeSpan.FromSeconds(10)) > DateTime.UtcNow)
                    continue;

                _logger.LogError(ex.InnerException ?? ex, "Error executing {type} for subscriber {subscriber}",
                    message.GetType().Name, subscriber.Subscriber.GetType().Name);
                _lastErrorTime[subscriber] = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Enables processing of the message queue, typically after initialization steps complete.
    /// </summary>
    public void StartQueueProcessing()
    {
        _logger.LogInformation("Starting Message Queue Processing");
        _processQueue = true;
    }

    private sealed class SubscriberAction
    {
        public SubscriberAction(IMediatorSubscriber subscriber, object action)
        {
            Subscriber = subscriber;
            Action = action;
        }

        public object Action { get; }
        public IMediatorSubscriber Subscriber { get; }
    }
}