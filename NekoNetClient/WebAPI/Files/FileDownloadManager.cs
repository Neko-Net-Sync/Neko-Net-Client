/*
     File: FileDownloadManager.cs
     Role: High-level download manager for batching, queueing, and processing file downloads. Coordinates with the
                 FileTransferOrchestrator to request sizes, enqueue CDN blocks, wait for readiness, download and munge the
                 block, and then decompress and persist each file to cache with verification.

     Cross-service/CDN behavior:
     - Supports service-only and multi-service sessions by normalizing API bases and routing auth tokens based on host
         mappings registered with the orchestrator.
     - Verifies per-file size/RawSize from server to drive cache reuse decisions upstream in handlers.
*/
using Dalamud.Utility;
using System.IO;
using System.Text;
using System.Text.Json;
using NekoNetClient.Services.Events;
using NekoNetClient.Utils;
using K4os.Compression.LZ4.Legacy;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Dto.Files;
using NekoNet.API.Routes;
using NekoNetClient.FileCache;
using NekoNetClient.PlayerData.Handlers;
using NekoNetClient.Services.Mediator;
using NekoNetClient.WebAPI.Files.Models;
using System.Net;
using System.Net.Http.Json;
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.WebAPI.Files;

/// <summary>
/// Manages end-to-end lifecycle of a batch download: size retrieval, queueing, throttled download, decompression,
/// and persistence to cache. Emits mediator events for UI and diagnostics.
/// </summary>
public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly Dictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly int? _serverIndex;
    private readonly string? _serviceApiBase;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly List<ThrottledStream> _activeDownloadStreams;
    private readonly ServerConfigurationManager? _serverConfigurationManager;

    private string GetServerLabel() => (_serviceApiBase ?? _orchestrator.FilesCdnUri?.ToString() ?? string.Empty).ToServerLabel();

    private void PublishDownloadEvent(EventSeverity severity, string message, Guid? requestId = null, Uri? uri = null, string? hash = null, Exception? ex = null)
    {
        var builder = new StringBuilder(message);
        if (requestId.HasValue) builder.Append(" | RequestId: ").Append(requestId.Value);
        if (uri != null) builder.Append(" | Uri: ").Append(uri);
        if (!string.IsNullOrEmpty(hash)) builder.Append(" | Hash: ").Append(hash);
        if (ex != null) builder.Append(" | Exception: ").Append(ex.GetType().Name).Append(':').Append(ex.Message);
        var evt = new Event(nameof(FileDownloadManager), severity, builder.ToString())
        {
            Server = GetServerLabel()
        };
        Mediator.Publish(new EventMessage(evt));
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value.Substring(0, maxLength);

    /// <summary>
    /// Primary constructor for the download manager; callers may provide optional server context for routing.
    /// </summary>
    public FileDownloadManager(ILogger<FileDownloadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, int? serverIndex = null, string? serviceApiBase = null) : base(logger, mediator)
    {
        _downloadStatus = new Dictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _serverIndex = serverIndex;
        _serviceApiBase = serviceApiBase;
        _activeDownloadStreams = [];

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            if (!_activeDownloadStreams.Any()) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    /// <summary>
    /// Overload to maintain compatibility with factory passing <see cref="ServerConfigurationManager"/>.
    /// </summary>
    public FileDownloadManager(ILogger<FileDownloadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor,
        ServerConfigurationManager serverConfigurationManager, int? serverIndex = null, string? serviceApiBase = null)
        : this(logger, mediator, orchestrator, fileCacheManager, fileCompactor, serverIndex, serviceApiBase)
    {
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Gets the list of current downloads representing the active batch.
    /// </summary>
    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    /// <summary>
    /// Gets transfers that were forbidden by the server for the current session.
    /// </summary>
    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    /// <summary>
    /// Gets a value indicating whether a download is currently in progress.
    /// </summary>
    public bool IsDownloading => CurrentDownloads.Any();

    // Expose selected server context for callers that need to reason about routing/verification.
    /// <summary>
    /// Selected server index for this manager if tied to a specific configured server.
    /// </summary>
    public int? ServerIndex => _serverIndex;
    /// <summary>
    /// Base API URL when operating in a service-only flow; used to normalize REST calls for size checks.
    /// </summary>
    public string? ServiceApiBase => _serviceApiBase;

    public static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= 42;
        }
    }

    /// <summary>
    /// Clears the active download state and progress tracking.
    /// </summary>
    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }

    /// <summary>
    /// Executes a full download workflow for the specified game object and its required file replacements.
    /// Publishes start/finish mediator events and halts/resumes scanning as appropriate.
    /// </summary>
    public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(gameObject, fileReplacementDto, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            Mediator.Publish(new DownloadFinishedMessage(gameObject));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        foreach (var stream in _activeDownloadStreams.ToList())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
                //
            }
        }
        base.Dispose(disposing);
    }

    private static byte MungeByte(int byteOrEof)
    {
        if (byteOrEof == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)(byteOrEof ^ 42);
    }

    /// <summary>
    /// Reads the header of a munged block file and returns the next file's hash and compressed length.
    /// </summary>
    private static (string fileHash, long fileLengthBytes) ReadBlockFileHeader(FileStream fileBlockStream)
    {
        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = (char)MungeByte(fileBlockStream.ReadByte());
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            int readByte = fileBlockStream.ReadByte();
            if (readByte == -1)
                throw new EndOfStreamException();

            var readChar = (char)MungeByte(readByte);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash) hashName.Add(readChar);
            else fileLength.Add(readChar);
        }
        return (string.Join("", hashName), long.Parse(string.Join("", fileLength)));
    }

    /// <summary>
    /// Downloads the CDN block for the provided request, writing a munged temporary file that will be decompressed later.
    /// Applies bandwidth throttling and reports byte progress to the accumulator.
    /// </summary>
    private async Task DownloadAndMungeFileHttpClient(string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, string tempPath, IProgress<long> progress, CancellationToken ct)
    {
        var primaryUri = fileTransfer[0].DownloadUri;
        Logger.LogDebug("GUID {requestId} on server {uri} for files {files}", requestId, primaryUri, string.Join(", ", fileTransfer.Select(c => c.Hash).ToList()));

        await WaitForDownloadReady(fileTransfer, requestId, ct).ConfigureAwait(false);

        _downloadStatus[downloadGroup].DownloadStatus = DownloadStatus.Downloading;

        HttpResponseMessage response = null!;
        var requestUrl = MareFiles.CacheGetFullPath(primaryUri, requestId);

        Logger.LogDebug("Downloading {requestUrl} for request {id}", requestUrl, requestId);
        try
        {
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
            PublishDownloadEvent(EventSeverity.Error, $"HTTP error during download (cancelled: {ct.IsCancellationRequested})", requestId: requestId, uri: requestUrl, ex: ex);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }
            throw;
        }

        ThrottledStream? stream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                long totalWritten = 0;
                var contentLength = response.Content.Headers.ContentLength;
                PublishDownloadEvent(EventSeverity.Informational, $"Starting block download (Content-Length: {contentLength?.ToString() ?? "unknown"})", requestId: requestId, uri: requestUrl);
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download of {id} with a speed limit of {limit} to {tempPath}", requestId, limit, tempPath);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                _activeDownloadStreams.Add(stream);
                while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    MungeBuffer(buffer.AsSpan(0, bytesRead));

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalWritten += bytesRead;
                    progress.Report(bytesRead);
                }
                await fileStream.FlushAsync(ct).ConfigureAwait(false);
                PublishDownloadEvent(EventSeverity.Informational, $"Completed block download: {totalWritten} bytes to temp file", requestId: requestId, uri: requestUrl);
                if (totalWritten <= 0)
                {
                    PublishDownloadEvent(EventSeverity.Error, "No data received from CDN (zero-length block)", requestId: requestId, uri: requestUrl);
                    throw new EndOfStreamException("Zero bytes received during block download");
                }

                Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, tempPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (!tempPath.IsNullOrEmpty())
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore if file deletion fails
            }
            PublishDownloadEvent(EventSeverity.Error, "Exception during download", requestId: requestId, uri: requestUrl, ex: ex);
            throw;
        }
        finally
        {
            if (stream != null)
            {
                _activeDownloadStreams.Remove(stream);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retrieves sizes for the specified hashes and prepares the <see cref="CurrentDownloads"/> list while registering
    /// observed hosts with the orchestrator for correct token routing.
    /// </summary>
    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        // Note: some servers (e.g., PlayerSync) provide DirectDownloadUrl which bypasses the CDN queue.
        // The internal download pipeline will branch accordingly while still emitting progress events.
        try
        {
            if (CurrentDownloads.Any(d => d.DirectDownloadUri != null))
            {
                PublishDownloadEvent(EventSeverity.Informational,
                    $"Prepared {CurrentDownloads.Count(d => d.DirectDownloadUri != null)} direct-download file(s)",
                    uri: CurrentDownloads.First().DirectDownloadUri ?? CurrentDownloads.First().DownloadUri);
            }
        }
        catch { }

        // Register all hosts involved (both CDN and direct) so token routing can pick the correct server index even in service-only flows
        try
        {
            var hosts = new List<Uri>();
            // Always include standard DownloadUri hosts
            hosts.AddRange(CurrentDownloads
                .Select(d => d.DownloadUri)
                .Where(u => u != null)
                .DistinctBy(u => (u!.Host, u.Port))
                .Cast<Uri>());
            // Also include any DirectDownloadUri hosts (PlayerSync direct path)
            hosts.AddRange(CurrentDownloads
                .Where(d => d.DirectDownloadUri != null)
                .Select(d => d.DirectDownloadUri!)
                .DistinctBy(u => (u.Host, u.Port)));
            // De-dup combined list by host:port
            hosts = hosts.DistinctBy(u => (u.Host, u.Port)).ToList();
            if (hosts.Count > 0)
            {
                _orchestrator.RegisterServiceHosts(_serviceApiBase, hosts);
            }
        }
        catch { }

        // Report prepared file count and first target URI for visibility
        try
        {
            if (CurrentDownloads.Count > 0)
            {
                var first = CurrentDownloads[0];
                PublishDownloadEvent(EventSeverity.Informational, $"Prepared {CurrentDownloads.Count} files for download", uri: first.DownloadUri);

                // Summarize CDN distribution by host for visibility
                try
                {
                    var byHost = CurrentDownloads.GroupBy(d => d.DownloadUri.Host + ":" + d.DownloadUri.Port)
                        .Select(g => $"{g.Key}={g.Count()}").ToList();
                    if (byHost.Count > 0)
                    {
                        PublishDownloadEvent(EventSeverity.Informational, "CDN distribution: " + string.Join(", ", byHost), uri: first.DownloadUri);
                    }
                }
                catch { }
            }
            else
            {
                PublishDownloadEvent(EventSeverity.Informational, "No files to download");
            }
        }
        catch { }

        return CurrentDownloads;
    }

    /// <summary>
    /// Orchestrates the parallel download of prepared files, including enqueue, waiting for readiness, download,
    /// and block decompression into per-file cache entries. Cleans up temporary files and releases slots.
    /// </summary>
    private async Task DownloadFilesInternal(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        // Group by the actual target host/port we will download from. For direct-downloads this is DirectDownloadUri.
        var downloadGroups = CurrentDownloads
            .GroupBy(f => (f.DirectDownloadUri ?? f.DownloadUri).Host + ":" + (f.DirectDownloadUri ?? f.DownloadUri).Port, StringComparer.Ordinal);

        foreach (var downloadGroup in downloadGroups)
        {
            _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                // If Total (compressed) is zero for direct downloads, fall back to RawSize to keep UI stable
                TotalBytes = downloadGroup.Sum(c => c.Total > 0 ? c.Total : (c is DownloadFileTransfer dft ? dft.TotalRaw : 0)),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions()
        {
            MaxDegreeOfParallelism = downloadGroups.Count(),
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            // Direct-download path: if any entry in this group has a DirectDownloadUri, stream files directly
            if (fileGroup.Any(f => f.DirectDownloadUri != null))
            {
                try
                {
                    _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForSlot;
                    await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                    _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.Downloading;
                    var directTransfers = fileGroup.Where(f => f.DirectDownloadUri != null).ToList();
                    await DownloadDirectAndPersistAsync(gameObjectHandler, directTransfers, fileReplacement, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    PublishDownloadEvent(EventSeverity.Warning, "Direct-download cancelled by user", uri: (fileGroup.First().DirectDownloadUri ?? fileGroup.First().DownloadUri));
                }
                catch (Exception ex)
                {
                    PublishDownloadEvent(EventSeverity.Error, "Error during direct-download batch", uri: (fileGroup.First().DirectDownloadUri ?? fileGroup.First().DownloadUri), ex: ex);
                }
                finally
                {
                    _orchestrator.ReleaseDownloadSlot();
                }
                // If there are any remaining files without direct URLs in this group, fall through to the normal CDN flow for them
                if (!fileGroup.Any(f => f.DirectDownloadUri == null))
                {
                    return; // nothing left for this group
                }
            }

            // let server predownload files
            var remaining = fileGroup.Where(f => f.DirectDownloadUri == null).ToList();
            if (remaining.Count == 0) return; // safety
            var requestIdResponse = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.RequestEnqueueFullPath(remaining.First().DownloadUri),
                remaining.Select(c => c.Hash), token).ConfigureAwait(false);
            Logger.LogDebug("Sent request for {n} files on server {uri} with result {result}", remaining.Count(), remaining.First().DownloadUri,
                await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false));

            Guid requestId = Guid.Parse((await requestIdResponse.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim('"'));

            Logger.LogDebug("GUID {requestId} for {n} files on server {uri}", requestId, remaining.Count(), remaining.First().DownloadUri);
            PublishDownloadEvent(EventSeverity.Informational, $"Enqueued {remaining.Count()} files for CDN", requestId: requestId, uri: remaining.First().DownloadUri);

            var blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
            FileInfo fi = new(blockFile);
            try
            {
                _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForSlot;
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForQueue;
                Progress<long> progress = new((bytesDownloaded) =>
                {
                    try
                    {
                        if (!_downloadStatus.TryGetValue(fileGroup.Key, out FileDownloadStatus? value)) return;
                        value.TransferredBytes += bytesDownloaded;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Could not set download progress");
                    }
                });
                await DownloadAndMungeFileHttpClient(fileGroup.Key, requestId, [.. fileGroup], blockFile, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("{dlName}: Detected cancellation of download, partially extracting files for {id}", fi.Name, gameObjectHandler);
                PublishDownloadEvent(EventSeverity.Warning, "Download cancelled while waiting for slot/queue", requestId: requestId, uri: fileGroup.First().DownloadUri);
            }
            catch (Exception ex)
            {
                _orchestrator.ReleaseDownloadSlot();
                File.Delete(blockFile);
                Logger.LogError(ex, "{dlName}: Error during download of {id}", fi.Name, requestId);
                PublishDownloadEvent(EventSeverity.Error, "Error during block download", requestId: requestId, uri: fileGroup.First().DownloadUri, ex: ex);
                ClearDownload();
                return;
            }

            FileStream? fileBlockStream = null;
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var status))
                {
                    status.TransferredFiles = 1;
                    status.DownloadStatus = DownloadStatus.Decompressing;
                }
                if (!File.Exists(blockFile))
                {
                    PublishDownloadEvent(EventSeverity.Error, "Block file missing before decompression", requestId: requestId, uri: fileGroup.First().DownloadUri);
                    throw new FileNotFoundException("Block file missing", blockFile);
                }
                else
                {
                    try
                    {
                        var len = new FileInfo(blockFile).Length;
                        PublishDownloadEvent(EventSeverity.Informational, $"Starting decompression of block ({len} bytes)", requestId: requestId, uri: fileGroup.First().DownloadUri);
                    }
                    catch { }
                }
                fileBlockStream = File.OpenRead(blockFile);
                while (fileBlockStream.Position < fileBlockStream.Length)
                {
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);

                    try
                    {
                        var fileExtension = fileReplacement.First(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                        var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);
                        Logger.LogDebug("{dlName}: Decompressing {file}:{le} => {dest}", fi.Name, fileHash, fileLengthBytes, filePath);

                        byte[] compressedFileContent = new byte[fileLengthBytes];
                        var readBytes = await fileBlockStream.ReadAsync(compressedFileContent, CancellationToken.None).ConfigureAwait(false);
                        if (readBytes != fileLengthBytes)
                        {
                            throw new EndOfStreamException();
                        }
                        MungeBuffer(compressedFileContent);

                        var decompressedFile = LZ4Wrapper.Unwrap(compressedFileContent);
                        await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);

                        PersistFileToStorage(fileHash, filePath);
                    }
                    catch (EndOfStreamException)
                    {
                        Logger.LogWarning("{dlName}: Failure to extract file {fileHash}, stream ended prematurely", fi.Name, fileHash);
                        PublishDownloadEvent(EventSeverity.Warning, "Failure to extract file block (stream ended)", requestId: requestId, uri: fileGroup.First().DownloadUri, hash: fileHash);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e, "{dlName}: Error during decompression", fi.Name);
                        PublishDownloadEvent(EventSeverity.Warning, "Error during file decompression", requestId: requestId, uri: fileGroup.First().DownloadUri, hash: fileHash, ex: e);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Logger.LogDebug("{dlName}: Failure to extract file header data, stream ended", fi.Name);
                PublishDownloadEvent(EventSeverity.Warning, "Failure to extract block header (stream ended)", requestId: requestId, uri: fileGroup.First().DownloadUri);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{dlName}: Error during block file read", fi.Name);
                PublishDownloadEvent(EventSeverity.Error, "Error during block file read", requestId: requestId, uri: fileGroup.First().DownloadUri, ex: ex);
            }
            finally
            {
                _orchestrator.ReleaseDownloadSlot();
                if (fileBlockStream != null)
                    await fileBlockStream.DisposeAsync().ConfigureAwait(false);
                if (File.Exists(blockFile))
                    File.Delete(blockFile);
            }
        }).ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", gameObjectHandler);

        ClearDownload();
    }

    /// <summary>
    /// Streams files directly from their DirectDownloadUri (no queue), writing a temporary munged block per file
    /// and then extracting into the cache. Emits progress updates to the current download group's status.
    /// </summary>
    private async Task DownloadDirectAndPersistAsync(GameObjectHandler gameObjectHandler, List<DownloadFileTransfer> transfers, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        foreach (var t in transfers)
        {
            ct.ThrowIfCancellationRequested();
            var directUri = t.DirectDownloadUri ?? t.DownloadUri;
            var requestLabel = $"direct:{t.Hash}";
            // Use blk for legacy block format; use raw for direct object storage content
            var isPreSigned = FileTransferOrchestrator.IsPreSignedUrl(directUri);
            var tempPath = _fileDbManager.GetCacheFilePath(requestLabel, isPreSigned ? "raw" : "blk");

            PublishDownloadEvent(EventSeverity.Informational, "Starting direct download", uri: directUri, hash: t.Hash);

            ThrottledStream? stream = null;
            try
            {
                IProgress<long> progress = new Progress<long>((bytes) =>
                {
                    try
                    {
                        var key = (directUri.Host + ":" + directUri.Port);
                        if (_downloadStatus.TryGetValue(key, out var status))
                        {
                            status.TransferredBytes += bytes;
                        }
                    }
                    catch { }
                });

                var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, directUri, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                await using (var fileStream = File.Create(tempPath))
                {
                    var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                    var buffer = new byte[bufferSize];
                    long totalWritten = 0;
                    var limit = _orchestrator.DownloadLimitPerSlot();
                    stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                    _activeDownloadStreams.Add(stream);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Pre-signed object storage content is already the compressed payload; do not munge
                        if (!isPreSigned)
                            MungeBuffer(buffer.AsSpan(0, read));
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        totalWritten += read;
                        progress.Report(read);
                    }
                    await fileStream.FlushAsync(ct).ConfigureAwait(false);
                    // Ensure totals are non-zero for UI visibility
                    try
                    {
                        var key = (directUri.Host + ":" + directUri.Port);
                        if (_downloadStatus.TryGetValue(key, out var status) && status.TotalBytes == 0)
                        {
                            status.TotalBytes = totalWritten;
                        }
                    }
                    catch { }
                    PublishDownloadEvent(EventSeverity.Informational, $"Direct block downloaded: {totalWritten} bytes", uri: directUri, hash: t.Hash);
                    if (totalWritten <= 0) throw new EndOfStreamException("Zero bytes received in direct download");
                }

                // Extract content depending on format
                _downloadStatus[(directUri.Host + ":" + directUri.Port)].DownloadStatus = DownloadStatus.Decompressing;
                byte[] decompressedFile;
                if (isPreSigned)
                {
                    // Entire file is the compressed payload (no block header, no munge). Read all bytes and unwrap.
                    var raw = await File.ReadAllBytesAsync(tempPath, CancellationToken.None).ConfigureAwait(false);
                    decompressedFile = LZ4Wrapper.Unwrap(raw);
                }
                else
                {
                    await using var fileBlockStream = File.OpenRead(tempPath);
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);
                    byte[] compressedFileContent = new byte[fileLengthBytes];
                    var readBytes = await fileBlockStream.ReadAsync(compressedFileContent, CancellationToken.None).ConfigureAwait(false);
                    if (readBytes != fileLengthBytes)
                    {
                        throw new EndOfStreamException();
                    }
                    MungeBuffer(compressedFileContent);
                    decompressedFile = LZ4Wrapper.Unwrap(compressedFileContent);
                }
                string fileExtension = "dat";
                try
                {
                    // We may not have the fileHash from header for pre-signed; fall back to t.Hash
                    var fileHash = t.Hash;
                    fileExtension = fileReplacement.First(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                }
                catch { }
                if (fileExtension == "dat")
                {
                    try
                    {
                        // fallback: try to get extension from any matching replacement using this transfer's hash
                        fileExtension = fileReplacement.First(f => string.Equals(f.Hash, t.Hash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                    }
                    catch { }
                }
                var filePath = _fileDbManager.GetCacheFilePath(t.Hash, fileExtension);
                await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);
                PersistFileToStorage(t.Hash, filePath);

                var key2 = (directUri.Host + ":" + directUri.Port);
                if (_downloadStatus.TryGetValue(key2, out var status2))
                {
                    status2.TransferredFiles += 1;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PublishDownloadEvent(EventSeverity.Error, "Exception during direct download", uri: directUri, hash: t.Hash, ex: ex);
            }
            finally
            {
                try
                {
                    if (stream != null)
                    {
                        _activeDownloadStreams.Remove(stream);
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch { }
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Resolves file size information for the provided hashes via the appropriate service/CDN base, normalizing
    /// API bases and ensuring tokens are routed by host.
    /// </summary>
    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, CancellationToken ct)
    {
        Uri? baseCdn = null;
        string source = "unknown";

        // Prefer the configured server's CDN first (preserves non-default ports like :6200)
        if (_serverIndex.HasValue)
        {
            baseCdn = _orchestrator.GetFilesCdnUriForServerIndex(_serverIndex.Value);
            if (baseCdn != null) source = "server-index CDN";
        }

        // If CDN is unknown or not provided, try the service API base next, preserving any explicit port
        if (baseCdn == null && !string.IsNullOrEmpty(_serviceApiBase))
        {
            if (Uri.TryCreate(_serviceApiBase, UriKind.Absolute, out var apiBase))
            {
                var builder = new UriBuilder(apiBase);
                if (string.Equals(builder.Scheme, "wss", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "https";
                else if (string.Equals(builder.Scheme, "ws", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "http";
                // keep builder.Port as-is to preserve non-default ports
                builder.Path = builder.Path.TrimEnd('/');
                builder.Query = null;
                builder.Fragment = null;
                baseCdn = builder.Uri;
                source = "service API base";
            }
        }
        // If still unknown, try to use the configured server API base (normalized) as the request base
        if (baseCdn == null && _serverIndex.HasValue)
        {
            try
            {
                var srv = _serverConfigurationManager?.GetServerByIndex(_serverIndex.Value);
                if (srv != null)
                {
                    baseCdn = _orchestrator.GetFilesCdnUriForApiBase(srv.ServerUri);
                    if (baseCdn != null) source = "server API base";
                }
            }
            catch { }
        }
        // Finally, fall back to the currently active orchestrator CDN (main service)
        baseCdn ??= _orchestrator.FilesCdnUri;
        if (baseCdn != null && source == "unknown") source = "default CDN";
        if (baseCdn == null)
        {
            PublishDownloadEvent(EventSeverity.Error, "FileTransferManager is not initialized for this server (no CDN/API base)");
            throw new InvalidOperationException("FileTransferManager is not initialized for this server");
        }

        var requestUri = MareFiles.ServerFilesGetSizesFullPath(baseCdn);
        try
        {
            PublishDownloadEvent(EventSeverity.Informational, $"Requesting file sizes for {hashes?.Count ?? 0} files via {source}", uri: requestUri);
        }
        catch { }
        HttpResponseMessage response;
        try
        {
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUri, hashes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PublishDownloadEvent(EventSeverity.Error, "Exception while requesting file sizes", uri: requestUri, ex: ex);
            throw;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Special-case: Some services (e.g., PlayerSync) do not implement /files/getFileSizes and return 404.
            // When direct-download is preferred/allowed, fall back to synthesizing per-file DirectDownloadUrl entries
            // so we can stream files immediately without size metadata.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                bool preferDirect = false;
                try
                {
                    if (_serverConfigurationManager != null && _serverIndex.HasValue)
                    {
                        var cfg = _serverConfigurationManager.GetServerByIndex(_serverIndex.Value);
                        if (cfg?.UseDirectDownload == true) preferDirect = true;
                    }
                }
                catch { }
                try
                {
                    if (!preferDirect && baseCdn != null && baseCdn.Host.Contains("playersync", StringComparison.OrdinalIgnoreCase))
                        preferDirect = true;
                }
                catch { }

                if (preferDirect && hashes != null && hashes.Count > 0 && baseCdn != null)
                {
                    PublishDownloadEvent(EventSeverity.Warning, "getFileSizes unavailable (404); using direct-download fallback for this server", uri: requestUri);
                    Logger.LogInformation("Falling back to direct-download for {count} files due to 404 at {uri}", hashes.Count, requestUri);

                    var fallback = new List<DownloadFileDto>(hashes.Count);
                    foreach (var h in hashes)
                    {
                        Uri direct;
                        try
                        {
                            direct = MareFiles.RequestRequestFileFullPath(baseCdn, h);
                        }
                        catch
                        {
                            direct = new Uri(baseCdn, $"/request/file?file={h}");
                        }
                        fallback.Add(new DownloadFileDto
                        {
                            Hash = h,
                            // Set standard Url to base for host registration; direct path will be used for transfer
                            Url = baseCdn.ToString(),
                            DirectDownloadUrl = direct.ToString(),
                            FileExists = true,
                            Size = 0,
                            RawSize = 0,
                            IsForbidden = false,
                            ForbiddenBy = string.Empty,
                        });
                    }
                    return fallback;
                }
            }

            PublishDownloadEvent(EventSeverity.Error, $"File size request failed with {response.StatusCode}: {Truncate(body, 256)}", uri: requestUri);
            Logger.LogWarning("File size request failed {status}: {body}", response.StatusCode, Truncate(body, 512));
            response.EnsureSuccessStatusCode();
        }

        try
        {
            var payload = JsonSerializer.Deserialize<List<DownloadFileDto>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
            if (payload == null)
            {
                PublishDownloadEvent(EventSeverity.Warning, "File size response was empty", uri: requestUri);
                return [];
            }
            return payload;
        }
        catch (JsonException ex)
        {
            PublishDownloadEvent(EventSeverity.Error, $"Failed to parse file size response: {Truncate(body, 256)}", uri: requestUri, ex: ex);
            throw;
        }
    }

    // Expose server file info lookup so callers can verify local cache before deciding what to download.
    /// <summary>
    /// Returns server metadata for the given hashes to allow cache verification upstream without performing a download.
    /// </summary>
    public async Task<Dictionary<string, DownloadFileDto>> GetServerFileInfoAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        var list = hashes?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (list.Count == 0) return new Dictionary<string, DownloadFileDto>(StringComparer.OrdinalIgnoreCase);

        var infos = await FilesGetSizes(list, ct).ConfigureAwait(false);
        return infos.ToDictionary(d => d.Hash, d => d, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes cache metadata and timestamps for a freshly extracted file, verifying hash integrity and cleaning
    /// up any mismatches.
    /// </summary>
    private void PersistFileToStorage(string fileHash, string filePath)
    {
        var fi = new FileInfo(filePath);
        Func<DateTime> RandomDayInThePast()
        {
            DateTime start = new(1995, 1, 1, 1, 1, 1, DateTimeKind.Local);
            Random gen = new();
            int range = (DateTime.Today - start).Days;
            return () => start.AddDays(gen.Next(range));
        }

        fi.CreationTime = RandomDayInThePast().Invoke();
        fi.LastAccessTime = DateTime.Today;
        fi.LastWriteTime = RandomDayInThePast().Invoke();
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath);
            if (entry != null && !string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Hash mismatch after extracting, got {hash}, expected {expectedHash}, deleting file", entry.Hash, fileHash);
                File.Delete(filePath);
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }

    /// <summary>
    /// Waits until the CDN queue marks the request as ready, issuing periodic heartbeats and a fall-back queue check
    /// if no readiness signal is received within a short time window. Cancels the queued request on timeout.
    /// </summary>
    private async Task WaitForDownloadReady(List<DownloadFileTransfer> downloadFileTransfer, Guid requestId, CancellationToken downloadCt)
    {
        bool alreadyCancelled = false;
        try
        {
            CancellationTokenSource localTimeoutCts = new();
            localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationTokenSource composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    // Heartbeat for queue wait visibility
                    PublishDownloadEvent(NekoNetClient.Services.Events.EventSeverity.Informational,
                        "Waiting for CDN queue to be ready",
                        requestId: requestId,
                        uri: downloadFileTransfer[0].DownloadUri);
                    await Task.Delay(250, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    var checkUri = MareFiles.RequestCheckQueueFullPath(downloadFileTransfer[0].DownloadUri, requestId);
                    var req = await _orchestrator.SendRequestAsync(HttpMethod.Get, checkUri,
                        downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                    req.EnsureSuccessStatusCode();
                    try
                    {
                        var body = await req.Content.ReadAsStringAsync(downloadCt).ConfigureAwait(false);
                        PublishDownloadEvent(NekoNetClient.Services.Events.EventSeverity.Informational,
                            $"Queue check responded: {Truncate(body, 128)}",
                            requestId: requestId,
                            uri: checkUri);
                    }
                    catch { }
                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            localTimeoutCts.Dispose();
            composite.Dispose();

            PublishDownloadEvent(NekoNetClient.Services.Events.EventSeverity.Informational,
                "CDN marked request ready",
                requestId: requestId,
                uri: downloadFileTransfer[0].DownloadUri);
            Logger.LogDebug("Download {requestId} ready", requestId);
        }
        catch (TaskCanceledException)
        {
            try
            {
                var cancelUri = MareFiles.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId);
                PublishDownloadEvent(NekoNetClient.Services.Events.EventSeverity.Warning,
                    "Cancelling queued request due to timeout",
                    requestId: requestId,
                    uri: cancelUri);
                await _orchestrator.SendRequestAsync(HttpMethod.Get, cancelUri).ConfigureAwait(false);
                alreadyCancelled = true;
            }
            catch
            {
                // ignore whatever happens here
            }

            throw;
        }
        finally
        {
            if (downloadCt.IsCancellationRequested && !alreadyCancelled)
            {
                try
                {
                    var cancelUri = MareFiles.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId);
                    await _orchestrator.SendRequestAsync(HttpMethod.Get, cancelUri).ConfigureAwait(false);
                }
                catch
                {
                    // ignore whatever happens here
                }
            }
            _orchestrator.ClearDownloadRequest(requestId);
        }
    }
}
