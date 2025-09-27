/*
     Neko-Net Client — PlayerData.Factories.FileDownloadManagerFactory
     -----------------------------------------------------------------
     Purpose
     - Centralized factory to create configured instances of FileDownloadManager.

     Notes
     - Wires logging, mediator, orchestrator, cache, compactor and server configuration.
     - Optionally scopes the manager to a specific server index or API base override for
         multi-service routing/auth scenarios.
*/
using Microsoft.Extensions.Logging;
using NekoNetClient.FileCache;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.WebAPI.Files;

namespace NekoNetClient.PlayerData.Factories;

/// <summary>
/// Factory for <see cref="FileDownloadManager"/> instances used to orchestrate download lifecycles
/// across multiple services/CDNs with cache and integrity verification.
/// </summary>
public class FileDownloadManagerFactory
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileCompactor _fileCompactor;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Creates a new factory with all required collaborators.
    /// </summary>
    /// <param name="loggerFactory">Logger factory to create typed loggers for managers.</param>
    /// <param name="mareMediator">Mediator for publishing download progress events.</param>
    /// <param name="fileTransferOrchestrator">Coordinator for HTTP/CDN transfers.</param>
    /// <param name="fileCacheManager">Cache manager for file metadata and local paths.</param>
    /// <param name="fileCompactor">NTFS compaction service for on-disk size reduction.</param>
    /// <param name="serverConfigurationManager">Server configuration for auth/routing.</param>
    public FileDownloadManagerFactory(ILoggerFactory loggerFactory, MareMediator mareMediator, FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, ServerConfigurationManager serverConfigurationManager)
    {
        _loggerFactory = loggerFactory;
        _mareMediator = mareMediator;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Creates a new <see cref="FileDownloadManager"/> optionally scoped to a specific server or API base.
    /// </summary>
    /// <param name="serverIndex">Optional server index to select credentials and server-scoped options.</param>
    /// <param name="serviceApiBase">Optional API base override for service-scoped managers.</param>
    /// <returns>A ready-to-use download manager.</returns>
    public FileDownloadManager Create(int? serverIndex = null, string? serviceApiBase = null)
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _mareMediator, _fileTransferOrchestrator, _fileCacheManager, _fileCompactor, _serverConfigurationManager, serverIndex, serviceApiBase);
    }
}
