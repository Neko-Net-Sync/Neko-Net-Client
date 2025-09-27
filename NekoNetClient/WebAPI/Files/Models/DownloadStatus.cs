/*
   File: DownloadStatus.cs
   Role: Coarse-grained phases for the multi-file download process. Used for UI and logging state transitions.
*/
namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// High-level phases used to represent the state of the current download batch.
/// </summary>
public enum DownloadStatus
{
    /// <summary>
    /// The orchestrator is initializing the batch, resolving sizes and preparing the queue.
    /// </summary>
    Initializing,
    /// <summary>
    /// Waiting to acquire a download slot (global throttling or per-host concurrency limits).
    /// </summary>
    WaitingForSlot,
    /// <summary>
    /// Waiting for the server-side queue to signal readiness for download.
    /// </summary>
    WaitingForQueue,
    /// <summary>
    /// Actively downloading data.
    /// </summary>
    Downloading,
    /// <summary>
    /// Post-download processing, e.g., decompression and validation.
    /// </summary>
    Decompressing
}