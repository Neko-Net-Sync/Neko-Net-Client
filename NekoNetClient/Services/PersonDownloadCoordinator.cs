using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.Services
{
    /// <summary>
    /// Coordinates in-flight downloads keyed by an identifier across services/handlers to avoid duplicate concurrent downloads.
    /// Callers should provide a stable key (e.g., UID or file-set signature) to coalesce work; the first caller executes the action while others await completion.
    /// </summary>
    public sealed class PersonDownloadCoordinator
    {
        private readonly ConcurrentDictionary<string, Lazy<Task>> _inflight = new(StringComparer.Ordinal);

        /// <summary>
        /// Runs the provided action once for the given key, coalescing concurrent callers.
        /// If an action is already running for this key, waits for its completion.
        /// </summary>
        /// <param name="key">The coalescing key (e.g., UID or file-set signature).</param>
        /// <param name="action">The action to execute once.</param>
        public async Task RunCoalescedAsync(string key, Func<Task> action)
        {
            var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task>(() => Task.Run(action), LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                await lazy.Value.ConfigureAwait(false);
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }
    }
}
