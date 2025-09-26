using NekoNet.API.Dto.Files;

namespace NekoNetClient.WebAPI.Files.Models;

public class DownloadFileTransfer : FileTransfer
{
    public DownloadFileTransfer(DownloadFileDto dto) : base(dto)
    {
    }

    // Allow transfers if the file exists and is not forbidden. For standard CDN flow we require Size > 0.
    // For distribution-fallback entries (no size known), allow transfer even when Size == 0.
    public override bool CanBeTransferred => Dto.FileExists && !Dto.IsForbidden && (Dto.Size > 0 || IsDistributionDirect);
    // Always use the standard CDN/API Url for orchestration (enqueue/check/cache).
    // DirectDownloadUrl is reserved for the PlayerSync direct-download path only.
    public Uri DownloadUri => new(Dto.Url);
    public Uri? DirectDownloadUri => string.IsNullOrEmpty(Dto.DirectDownloadUrl) ? null : new Uri(Dto.DirectDownloadUrl);
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
    public override long Total
    {
        set
        {
            // nothing to set
        }
        get => Dto.Size;
    }

    public long TotalRaw => Dto.RawSize;
    public new DownloadFileDto Dto => (DownloadFileDto)TransferDto;
}