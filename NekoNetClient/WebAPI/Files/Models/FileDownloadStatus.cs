/*
   File: FileDownloadStatus.cs
   Role: Aggregated status for a multi-file download session, used to surface progress to UI and logs.

   Semantics:
   - DownloadStatus: coarse-grained state machine (initializing, waiting for slot/queue, downloading, decompressing).
   - TotalBytes/Files: full scope of the current batch as determined after server size checks.
   - TransferredBytes/Files: live progress across all files in the batch, updated as transfers advance.
*/
namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// Represents aggregate progress information for a batch of file downloads, including current phase,
/// total scope, and transferred counts.
/// </summary>
public class FileDownloadStatus
{
    /// <summary>
    /// Current coarse-grained download state for the batch.
    /// </summary>
    public DownloadStatus DownloadStatus { get; set; }
    /// <summary>
    /// Total number of bytes planned to be transferred in this batch.
    /// </summary>
    public long TotalBytes { get; set; }
    /// <summary>
    /// Total number of files planned to be transferred in this batch.
    /// </summary>
    public int TotalFiles { get; set; }
    /// <summary>
    /// Bytes already transferred across all files in the batch.
    /// </summary>
    public long TransferredBytes { get; set; }
    /// <summary>
    /// Number of files completed in this batch.
    /// </summary>
    public int TransferredFiles { get; set; }
}