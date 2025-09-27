/*
   File: UploadFileTransfer.cs
   Role: Model representing a single upload in the client-side transfer pipeline, wrapping UploadFileDto.

   Semantics:
   - Total is caller-defined (size of the local file) and participates in progress reporting.
   - LocalFile records the path to the on-disk content to be uploaded.
*/
using NekoNet.API.Dto.Files;

namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// Represents a concrete upload transfer based on <see cref="UploadFileDto"/>.
/// </summary>
public class UploadFileTransfer : FileTransfer
{
    /// <summary>
    /// Initializes a new instance from a server-provided <see cref="UploadFileDto"/>.
    /// </summary>
    /// <param name="dto">The transfer description provided by the server.</param>
    public UploadFileTransfer(UploadFileDto dto) : base(dto)
    {
    }

    /// <summary>
    /// Gets or sets the path to the local file to upload.
    /// </summary>
    public string LocalFile { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the total number of bytes for this upload (size of <see cref="LocalFile"/>).
    /// </summary>
    public override long Total { get; set; }
}