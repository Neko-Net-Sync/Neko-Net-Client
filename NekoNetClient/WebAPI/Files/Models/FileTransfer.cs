/*
   File: FileTransfer.cs
   Role: Abstract base for upload/download transfer models. Provides common properties such as hash, forbidden state,
         transfer totals, and progress helpers used by the orchestrator and UI.

   Semantics:
   - CanBeTransferred gates whether a transfer is eligible to run (for downloads requires FileExists; for uploads depends
     on subclass). Forbidden transfers are always excluded.
   - Total/Transferred are used by progress reporting and completion checks; subclasses define how Total is sourced.
*/
using NekoNet.API.Dto.Files;

namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// Base class for file transfer models (upload/download). Wraps the server-provided transfer DTO and exposes
/// shared properties that drive queue eligibility and progress reporting.
/// </summary>
public abstract class FileTransfer
{
    protected readonly ITransferFileDto TransferDto;

    /// <summary>
    /// Creates a new transfer wrapper around a server-provided <see cref="ITransferFileDto"/>.
    /// </summary>
    /// <param name="transferDto">The underlying transfer DTO.</param>
    protected FileTransfer(ITransferFileDto transferDto)
    {
        TransferDto = transferDto;
    }

    /// <summary>
    /// Gets a value indicating whether this transfer is eligible to run. For downloads, the file must exist on the
    /// server. Forbidden transfers are never eligible.
    /// </summary>
    public virtual bool CanBeTransferred => !TransferDto.IsForbidden && (TransferDto is not DownloadFileDto dto || dto.FileExists);
    /// <summary>
    /// Gets the identifier of the entity that forbids this transfer, if any.
    /// </summary>
    public string ForbiddenBy => TransferDto.ForbiddenBy;
    /// <summary>
    /// Gets the content hash of the file.
    /// </summary>
    public string Hash => TransferDto.Hash;
    /// <summary>
    /// Gets a value indicating whether the transfer is forbidden by policy or permissions.
    /// </summary>
    public bool IsForbidden => TransferDto.IsForbidden;
    /// <summary>
    /// Gets a value indicating whether the transfer is currently in progress.
    /// </summary>
    public bool IsInTransfer => Transferred != Total && Transferred > 0;
    /// <summary>
    /// Gets a value indicating whether the transfer has completed.
    /// </summary>
    public bool IsTransferred => Transferred == Total;
    /// <summary>
    /// Gets or sets the total number of bytes for this transfer. Subclasses control the semantics; for downloads this
    /// reflects the server-advertised size.
    /// </summary>
    public abstract long Total { get; set; }
    /// <summary>
    /// Gets or sets the number of bytes already transferred.
    /// </summary>
    public long Transferred { get; set; } = 0;

    public override string ToString()
    {
        return Hash;
    }
}