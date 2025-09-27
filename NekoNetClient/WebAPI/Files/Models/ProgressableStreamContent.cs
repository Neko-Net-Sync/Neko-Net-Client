/*
   File: ProgressableStreamContent.cs
   Role: Custom HttpContent that streams data while reporting granular upload progress via IProgress<UploadProgress>.

   Behavior:
   - Reports cumulative uploaded bytes against the source stream length after each write.
   - Supports resetting position for seekable streams when reused; throws for already-consumed non-seekable streams.
   - Default buffer size is 4096 bytes; caller may override.
*/
using System.Net;

namespace NekoNetClient.WebAPI.Files.Models;

/// <summary>
/// StreamContent derivative that reports upload progress during serialization to the outgoing HTTP stream.
/// </summary>
public class ProgressableStreamContent : StreamContent
{
    private const int _defaultBufferSize = 4096;
    private readonly int _bufferSize;
    private readonly IProgress<UploadProgress>? _progress;
    private readonly Stream _streamToWrite;
    private bool _contentConsumed;

    /// <summary>
    /// Initializes a new instance using a default buffer size.
    /// </summary>
    /// <param name="streamToWrite">The source stream to upload.</param>
    /// <param name="downloader">The progress reporter.</param>
    public ProgressableStreamContent(Stream streamToWrite, IProgress<UploadProgress>? downloader)
        : this(streamToWrite, _defaultBufferSize, downloader)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom buffer size and optional progress reporter.
    /// </summary>
    /// <param name="streamToWrite">The source stream to upload.</param>
    /// <param name="bufferSize">The buffer size in bytes to use for reads/writes.</param>
    /// <param name="progress">Optional progress reporter.</param>
    public ProgressableStreamContent(Stream streamToWrite, int bufferSize, IProgress<UploadProgress>? progress)
        : base(streamToWrite, bufferSize)
    {
        if (streamToWrite == null)
        {
            throw new ArgumentNullException(nameof(streamToWrite));
        }

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        _streamToWrite = streamToWrite;
        _bufferSize = bufferSize;
        _progress = progress;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamToWrite.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Serializes the source stream to the outgoing HTTP connection and reports progress incrementally.
    /// </summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        PrepareContent();

        var buffer = new byte[_bufferSize];
        var size = _streamToWrite.Length;
        var uploaded = 0;

        using (_streamToWrite)
        {
            while (true)
            {
                var length = await _streamToWrite.ReadAsync(buffer).ConfigureAwait(false);
                if (length <= 0)
                {
                    break;
                }

                uploaded += length;
                _progress?.Report(new UploadProgress(uploaded, size));
                await stream.WriteAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = _streamToWrite.Length;
        return true;
    }

    private void PrepareContent()
    {
        if (_contentConsumed)
        {
            if (_streamToWrite.CanSeek)
            {
                _streamToWrite.Position = 0;
            }
            else
            {
                throw new InvalidOperationException("The stream has already been read.");
            }
        }

        _contentConsumed = true;
    }
}