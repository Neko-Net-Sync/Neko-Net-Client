/*
   File: DownloadFileTransfer.cs
   Role: Model representing a single downloadable file in the client-side transfer pipeline. It wraps a DownloadFileDto and
         provides convenience properties for orchestration (URIs, transfer eligibility, and totals).

   Cross-service/CDN semantics:
   - CanBeTransferred returns true only when the server marks the file as existing and not forbidden. For standard CDN flows we
     also require a positive Size. Fallback distribution entries (e.g., pre-resolved direct distribution URLs) are permitted
     even with Size == 0 via IsDistributionDirect.
   - DownloadUri is used by the normal CDN/API orchestrator. DirectDownloadUri is reserved for direct PlayerSync paths and MUST
     NOT be used by the generic queue/orchestrator unless explicitly intended.

   Invariants and error behavior:
   - Dto.Url must be absolute for IsDistributionDirect checks; malformed URLs are ignored safely (treated as not direct).
   - Total reflects the server-declared compressed size (Size). RawSize is provided for cache validations.
*/
using NekoNet.API.Dto.Files;

namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// Represents a concrete download transfer based on <see cref="DownloadFileDto"/>. Exposes URLs used by the download
/// orchestrator and encapsulates the rules that determine when a file is allowed to be transferred across services/CDNs.
/// </summary>
public class DownloadFileTransfer : FileTransfer
{
    /// <summary>
    /// Initializes a new instance from a server-provided <see cref="DownloadFileDto"/>.
    /// </summary>
    /// <param name="dto">The transfer description provided by the server.</param>
    public DownloadFileTransfer(DownloadFileDto dto) : base(dto)
    {
    }

    /// <summary>
    /// Indicates if this file is eligible for transfer.
    /// - Direct download available: allowed when not forbidden, independent of FileExists/Size flags from CDN.
    /// - Standard/CDN path: requires the file to exist and either a positive Size or a recognizable distribution URL.
    /// </summary>
    public override bool CanBeTransferred
        => !Dto.IsForbidden
           && (
               DirectDownloadUri != null
               || (Dto.FileExists && (Dto.Size > 0 || IsDistributionDirect))
           );
    /// <summary>
    /// Gets the standard CDN/API URL used by the orchestrator for queued downloads.
    /// </summary>
    public Uri DownloadUri => new(Dto.Url);
    /// <summary>
    /// Gets the optional direct download URL. Reserved for PlayerSync direct-download paths and not used by the generic queue.
    /// </summary>
    public Uri? DirectDownloadUri => string.IsNullOrEmpty(Dto.DirectDownloadUrl) ? null : new Uri(Dto.DirectDownloadUrl);
    /// <summary>
    /// Indicates whether the URL looks like a distribution/"direct" endpoint (used as a fallback when no size is provided).
    /// This accepts paths that typically include /dist/, /files/, /download/, or /api/files/.
    /// </summary>
    public bool IsDistributionDirect
    {
        get
        {
            if (string.IsNullOrEmpty(Dto.Url)) return false;
            try
            {
                var uri = new Uri(Dto.Url, UriKind.Absolute);
                var path = (uri.AbsolutePath ?? string.Empty).ToLowerInvariant();
                // Typical patterns in our generated fallback URLs
                if (path.Contains("/dist/") || path.Contains("/files/") || path.Contains("/download/") || path.Contains("/api/files/"))
                    return true;
            }
            catch { }
            return false;
        }
    }
    /// <summary>
    /// Gets the total compressed size as reported by the server. Setter is ignored for download transfers.
    /// </summary>
    public override long Total
    {
        set
        {
            // nothing to set
        }
        get => Dto.Size;
    }

    /// <summary>
    /// Gets the uncompressed/raw size from the server. Used for cache verification and integrity checks.
    /// </summary>
    public long TotalRaw => Dto.RawSize;
    /// <summary>
    /// Strongly-typed access to the underlying <see cref="DownloadFileDto"/>.
    /// </summary>
    public new DownloadFileDto Dto => (DownloadFileDto)TransferDto;
}