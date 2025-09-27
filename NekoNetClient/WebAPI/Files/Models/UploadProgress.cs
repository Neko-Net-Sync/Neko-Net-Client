/*
	File: UploadProgress.cs
	Role: Lightweight value type used for progress callbacks during uploads.
*/
namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// Represents a snapshot of upload progress.
/// </summary>
public record UploadProgress(long Uploaded, long Size);