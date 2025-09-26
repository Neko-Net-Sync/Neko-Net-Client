using Microsoft.Extensions.Logging;
using NekoNetClient.FileCache;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.WebAPI.Files;

namespace NekoNetClient.PlayerData.Factories;

public class FileDownloadManagerFactory
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileCompactor _fileCompactor;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;

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

    public FileDownloadManager Create(int? serverIndex = null, string? serviceApiBase = null)
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _mareMediator, _fileTransferOrchestrator, _fileCacheManager, _fileCompactor, _serverConfigurationManager, serverIndex, serviceApiBase);
    }
}
