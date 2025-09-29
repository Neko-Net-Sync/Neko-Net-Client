/*
   File: FileUploadManager.cs
   Role: High-level uploader coordinating verification, compression, and upload of files to the server/CDN. Handles
       batching, progress reporting, and fallback to munged upload when necessary.

   Cross-service behavior:
   - Uses orchestrator to resolve correct CDN base for a given server index and routes requests with proper auth.
*/
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Dto.Files;
using NekoNet.API.Routes;
using NekoNetClient.FileCache;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI;
using NekoNetClient.WebAPI.Files.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NekoNetClient.WebAPI.Files;

/// <summary>
/// Manages verification and upload of local file content to the service/CDN. Performs compression, optional munging,
/// and incremental progress reporting.
/// </summary>
public sealed class FileUploadManager : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileDbManager;
    private readonly MareConfigService _mareConfigService;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<string, DateTime> _verifiedUploadedHashes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _uploadCancellationTokenSource = new();
    private Uri? _currentBaseCdn;
    private int? _currentServerIndex;

    public FileUploadManager(ILogger<FileUploadManager> logger, MareMediator mediator,
        MareConfigService mareConfigService,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileDbManager,
        ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _mareConfigService = mareConfigService;
        _orchestrator = orchestrator;
        _fileDbManager = fileDbManager;
        _serverManager = serverManager;

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            Reset();
        });
    }

    /// <summary>
    /// Gets the set of currently pending/active uploads.
    /// </summary>
    public List<FileTransfer> CurrentUploads { get; } = [];
    /// <summary>
    /// Gets a value indicating whether any uploads are active.
    /// </summary>
    public bool IsUploading => CurrentUploads.Count > 0;

    /// <summary>
    /// Cancels the current upload batch if any is running and clears bookkeeping.
    /// </summary>
    public bool CancelUpload()
    {
        if (CurrentUploads.Any())
        {
            Logger.LogDebug("Cancelling current upload");
            _uploadCancellationTokenSource?.Cancel();
            _uploadCancellationTokenSource?.Dispose();
            _uploadCancellationTokenSource = null;
            CurrentUploads.Clear();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Requests deletion of all files on the remote service for the specified server (or current).
    /// </summary>
    public async Task DeleteAllFiles(int? serverIndex = null)
    {
        var baseCdn = serverIndex.HasValue ? _orchestrator.GetFilesCdnUriForServerIndex(serverIndex.Value) : _orchestrator.FilesCdnUri;
        if (baseCdn == null) throw new InvalidOperationException("FileTransferManager is not initialized");
        await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.ServerFilesDeleteAllFullPath(baseCdn)).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a set of file hashes, compressing content and reporting progress. Returns hashes that are missing locally
    /// or forbidden by the server.
    /// </summary>
    public async Task<List<string>> UploadFiles(List<string> hashesToUpload, IProgress<string> progress, CancellationToken? ct = null, int? serverIndex = null)
    {
        Logger.LogDebug("Trying to upload files");
        _currentServerIndex = serverIndex;
        _currentBaseCdn = serverIndex.HasValue ? _orchestrator.GetFilesCdnUriForServerIndex(serverIndex.Value) : _orchestrator.FilesCdnUri;
        if (_currentBaseCdn == null) throw new InvalidOperationException("FileTransferManager is not initialized");
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Any())
        {
            return locallyMissingFiles;
        }

        progress.Report($"Starting upload for {filesPresentLocally.Count} files");

    var filesToUpload = await FilesSend(ResolveUploadBase(_currentBaseCdn), [.. filesPresentLocally], [], ct ?? CancellationToken.None).ConfigureAwait(false);

        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        Task uploadTask = Task.CompletedTask;
        int i = 1;
        foreach (var file in filesToUpload)
        {
            progress.Report($"Uploading file {i++}/{filesToUpload.Count}. Please wait until the upload is completed.");
            Logger.LogDebug("[{hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, ct ?? CancellationToken.None).ConfigureAwait(false);
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
            await uploadTask.ConfigureAwait(false);
            uploadTask = UploadFile(data.Item2, file.Hash, false, ct ?? CancellationToken.None);
            (ct ?? CancellationToken.None).ThrowIfCancellationRequested();
        }

        await uploadTask.ConfigureAwait(false);

        return [];
    }

    /// <summary>
    /// Uploads all file content referenced by the provided character data, limited to the current visibility set.
    /// </summary>
    public async Task<CharacterData> UploadFiles(CharacterData data, List<UserData> visiblePlayers, int? serverIndex = null)
    {
        CancelUpload();

        _uploadCancellationTokenSource = new CancellationTokenSource();
        var uploadToken = _uploadCancellationTokenSource.Token;
        _currentServerIndex = serverIndex;
        _currentBaseCdn = serverIndex.HasValue ? _orchestrator.GetFilesCdnUriForServerIndex(serverIndex.Value) : _orchestrator.FilesCdnUri;
        Logger.LogDebug("Sending Character data {hash} to service {url}", data.DataHash.Value, serverIndex.HasValue ? _serverManager.GetServerByIndex(serverIndex.Value).ServerUri : _serverManager.CurrentApiUrl);

        HashSet<string> unverifiedUploads = GetUnverifiedFiles(data);
        if (unverifiedUploads.Any())
        {
            await UploadUnverifiedFiles(unverifiedUploads, visiblePlayers, uploadToken).ConfigureAwait(false);
            Logger.LogInformation("Upload complete for {hash}", data.DataHash.Value);
        }

        foreach (var kvp in data.FileReplacements)
        {
            data.FileReplacements[kvp.Key].RemoveAll(i => _orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, i.Hash, StringComparison.OrdinalIgnoreCase)));
        }

        return data;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Reset();
    }

    /// <summary>
    /// Queries the service for which files must be uploaded given a set of hashes and audience UIDs.
    /// </summary>
    private async Task<List<UploadFileDto>> FilesSend(Uri baseCdn, List<string> hashes, List<string> uids, CancellationToken ct)
    {
        if (baseCdn == null) throw new InvalidOperationException("FileTransferManager is not initialized");
        FilesSendDto filesSendDto = new()
        {
            FileHashes = hashes,
            UIDs = uids
        };
        var response = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.ServerFilesFilesSendFullPath(baseCdn), filesSendDto, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<UploadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private HashSet<string> GetUnverifiedFiles(CharacterData data)
    {
        HashSet<string> unverifiedUploadHashes = new(StringComparer.Ordinal);
        foreach (var item in data.FileReplacements.SelectMany(c => c.Value.Where(f => string.IsNullOrEmpty(f.FileSwapPath)).Select(v => v.Hash).Distinct(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList())
        {
            if (!_verifiedUploadedHashes.TryGetValue(item, out var verifiedTime))
            {
                verifiedTime = DateTime.MinValue;
            }

            if (verifiedTime < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10)))
            {
                Logger.LogTrace("Verifying {item}, last verified: {date}", item, verifiedTime);
                unverifiedUploadHashes.Add(item);
            }
        }

        return unverifiedUploadHashes;
    }

    /// <summary>
    /// Cancels and clears current upload state.
    /// </summary>
    private void Reset()
    {
        _uploadCancellationTokenSource?.Cancel();
        _uploadCancellationTokenSource?.Dispose();
        _uploadCancellationTokenSource = null;
        CurrentUploads.Clear();
        _verifiedUploadedHashes.Clear();
    }

    /// <summary>
    /// Uploads a single compressed file with optional munging fallback on error, updating verification timestamps.
    /// </summary>
    private async Task UploadFile(byte[] compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        Logger.LogInformation("[{hash}] Uploading {size}", fileHash, UiSharedService.ByteToString(compressedFile.Length));

        if (uploadToken.IsCancellationRequested) return;

        try
        {
            bool useMunge = _serverManager.CurrentServer?.UseMungeUpload ?? false;
            await UploadFileStream(compressedFile, fileHash, useMunge, postProgress, uploadToken).ConfigureAwait(false);
            _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            bool useMunge = _serverManager.CurrentServer?.UseMungeUpload ?? false;
            if (!useMunge && ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "[{hash}] Error during file upload, trying munged upload as fallback", fileHash);
                await UploadFileStream(compressedFile, fileHash, munged: true, postProgress, uploadToken).ConfigureAwait(false);
                _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
            }
            else
            {
                Logger.LogWarning(ex, "[{hash}] File upload failed", fileHash);
                throw;
            }
        }
    }

    /// <summary>
    /// Streams a compressed file to the server using ProgressableStreamContent, optionally applying munging.
    /// </summary>
    private async Task UploadFileStream(byte[] compressedFile, string fileHash, bool munged, bool postProgress, CancellationToken uploadToken)
    {
        if (munged)
        {
            FileDownloadManager.MungeBuffer(compressedFile.AsSpan());
        }

        using var ms = new MemoryStream(compressedFile);

        Progress<UploadProgress>? prog = !postProgress ? null : new((prog) =>
        {
            try
            {
                CurrentUploads.Single(f => string.Equals(f.Hash, fileHash, StringComparison.Ordinal)).Transferred = prog.Uploaded;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{hash}] Could not set upload progress", fileHash);
            }
        });

        var streamContent = new ProgressableStreamContent(ms, prog);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        var baseUpload = ResolveUploadBase(_currentBaseCdn ?? _orchestrator.FilesCdnUri!);
        if (!munged)
            response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, MareFiles.ServerFilesUploadFullPath(baseUpload, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        else
            response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, MareFiles.ServerFilesUploadMunged(baseUpload, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);
    }

    /// <summary>
    /// For all file hashes that need verification, compress and upload content as permitted by the server.
    /// </summary>
    private async Task UploadUnverifiedFiles(HashSet<string> unverifiedUploadHashes, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        // Deduplicate upfront to avoid any downstream Single() surprises
        unverifiedUploadHashes = unverifiedUploadHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        unverifiedUploadHashes = unverifiedUploadHashes.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);

        Logger.LogDebug("Verifying {count} files", unverifiedUploadHashes.Count);
    var baseForSend = ResolveUploadBase(_currentBaseCdn ?? _orchestrator.FilesCdnUri!);
    var filesToUpload = await FilesSend(baseForSend, [.. unverifiedUploadHashes], visiblePlayers.Select(p => p.UID).ToList(), uploadToken).ConfigureAwait(false);

        foreach (var file in filesToUpload.Where(f => !f.IsForbidden).DistinctBy(f => f.Hash))
        {
            try
            {
                // Guard against adding duplicate hashes into CurrentUploads
                if (CurrentUploads.Exists(u => string.Equals(u.Hash, file.Hash, StringComparison.Ordinal)))
                    continue;

                CurrentUploads.Add(new UploadFileTransfer(file)
                {
                    Total = new FileInfo(_fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath).Length,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {hash} but file was not present", file.Hash);
            }
        }

        foreach (var file in filesToUpload.Where(c => c.IsForbidden))
        {
            if (_orchestrator.ForbiddenTransfers.TrueForAll(f => !string.Equals(f.Hash, file.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new UploadFileTransfer(file)
                {
                    LocalFile = _fileDbManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath ?? string.Empty,
                });
            }

            _verifiedUploadedHashes[file.Hash] = DateTime.UtcNow;
        }

        var totalSize = CurrentUploads.Sum(c => c.Total);
        Logger.LogDebug("Compressing and uploading files");
        Task uploadTask = Task.CompletedTask;
        foreach (var file in CurrentUploads.Where(f => f.CanBeTransferred && !f.IsTransferred).ToList())
        {
            Logger.LogDebug("[{hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, uploadToken).ConfigureAwait(false);
            // Be robust against unexpected duplicates or race conditions in CurrentUploads
            var cu = CurrentUploads.FirstOrDefault(e => string.Equals(e.Hash, data.Item1, StringComparison.Ordinal));
            if (cu != null) cu.Total = data.Item2.Length;
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
            await uploadTask.ConfigureAwait(false);
            try
            {
                uploadTask = UploadFile(data.Item2, file.Hash, true, uploadToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("more than one matching element", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(ex, "Duplicate hash encountered during upload bookkeeping for {hash}", file.Hash);
                // Continue with best effort
                uploadTask = UploadFile(data.Item2, file.Hash, true, uploadToken);
            }
            uploadToken.ThrowIfCancellationRequested();
        }

        if (CurrentUploads.Any())
        {
            await uploadTask.ConfigureAwait(false);

            var compressedSize = CurrentUploads.Sum(c => c.Total);
            Logger.LogDebug("Upload complete, compressed {size} to {compressed}", UiSharedService.ByteToString(totalSize), UiSharedService.ByteToString(compressedSize));
        }

        foreach (var file in unverifiedUploadHashes.Where(c => !CurrentUploads.Exists(u => string.Equals(u.Hash, c, StringComparison.Ordinal))))
        {
            _verifiedUploadedHashes[file] = DateTime.UtcNow;
        }

        CurrentUploads.Clear();
    }

    private Uri ResolveUploadBase(Uri fallback)
    {
        try
        {
            if (_currentServerIndex.HasValue)
            {
                var raw = _serverManager.GetServerByIndex(_currentServerIndex.Value).ServerUri;
                var norm = NormalizeApiBase(raw);
                if (!string.IsNullOrEmpty(norm) && Uri.TryCreate(norm, UriKind.Absolute, out var api))
                {
                    // Orchestrator maps configured API base host to server index for proper token routing
                    Logger.LogDebug("Using API base for upload: {api}", api);
                    return api;
                }
            }
        }
        catch { }
        return fallback;
    }

    private static string? NormalizeApiBase(string? apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase)) return null;
        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri))
        {
            var trimmed = apiBase.Trim().TrimEnd('/');
            if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return "https://" + trimmed.Substring(6);
            if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "http://" + trimmed.Substring(5);
            return trimmed;
        }
        var builder = new UriBuilder(uri);
        if (string.Equals(builder.Scheme, "wss", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "https";
        else if (string.Equals(builder.Scheme, "ws", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "http";
        builder.Port = -1;
        return builder.Uri.ToString().TrimEnd('/');
    }
}
