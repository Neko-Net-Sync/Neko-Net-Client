using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.Services
{
    /// <summary>
    /// Serializes character apply operations per UID across all services/handlers to avoid
    /// duplicate, overlapping downloads and re-application thrash. Also tracks the last
    /// successfully applied data hash to skip redundant applies for identical content.
    /// </summary>
    public sealed class PersonApplyCoordinator
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _lastAppliedHash = new(StringComparer.Ordinal);

        /// <summary>
        /// Attempts to enter the apply section for the given UID. This will wait for any in-flight
        /// apply to complete. If the last applied hash matches <paramref name="incomingHash"/>,
        /// this method releases immediately and returns false to indicate the caller should skip.
        /// When this method returns true, the caller MUST invoke <see cref="Complete"/> followed by
        /// <see cref="Release"/> in a finally block.
        /// </summary>
        public async Task<bool> TryEnterAsync(string uid, string incomingHash, CancellationToken ct)
        {
            var gate = _locks.GetOrAdd(uid, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);

            if (_lastAppliedHash.TryGetValue(uid, out var last)
                && string.Equals(last, incomingHash, StringComparison.Ordinal))
            {
                // Nothing to do, already at this state
                gate.Release();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Records the last applied hash for the UID. Call this when an apply finished (successfully)
        /// to prevent duplicate re-applies of the same data from parallel services.
        /// </summary>
        public void Complete(string uid, string appliedHash)
        {
            _lastAppliedHash[uid] = appliedHash;
        }

        /// <summary>
        /// Releases the per-UID gate acquired via <see cref="TryEnterAsync"/>.
        /// </summary>
        public void Release(string uid)
        {
            if (_locks.TryGetValue(uid, out var gate))
            {
                try { gate.Release(); } catch { }
            }
        }
    }
}
