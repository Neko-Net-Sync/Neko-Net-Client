/*
     Neko-Net Client — PlayerData.Factories.PairHandlerFactory
     ---------------------------------------------------------
     Purpose
     - Creates fully wired PairHandler instances and maps API URL overrides back to server indices for
         correct auth/routing across multiple services.

     Notes
     - Normalizes ws/wss to http/https, strips ports and trailing slashes to compare hosts with configured servers.
*/
using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.FileCache;
using NekoNetClient.Interop.Ipc;
using NekoNetClient.PlayerData.Handlers;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.PlayerData.Factories;

/// <summary>
/// Factory for fully configured <see cref="PairHandler"/> instances. Resolves the appropriate server index
/// for service-scoped pairs by normalizing and matching API base URLs, ensuring REST calls authenticate correctly.
/// </summary>
public class PairHandlerFactory
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IpcManager _ipcManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly PersonDownloadCoordinator _personDownloadCoordinator;
    private readonly PersonApplyCoordinator _personApplyCoordinator;

    public PairHandlerFactory(ILoggerFactory loggerFactory, GameObjectHandlerFactory gameObjectHandlerFactory, IpcManager ipcManager,
        FileDownloadManagerFactory fileDownloadManagerFactory, DalamudUtilService dalamudUtilService,
        PluginWarningNotificationService pluginWarningNotificationManager, IHostApplicationLifetime hostApplicationLifetime,
        FileCacheManager fileCacheManager, MareMediator mareMediator, PlayerPerformanceService playerPerformanceService,
        ServerConfigurationManager serverConfigManager, PersonDownloadCoordinator personDownloadCoordinator, PersonApplyCoordinator personApplyCoordinator)
    {
        _loggerFactory = loggerFactory;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _dalamudUtilService = dalamudUtilService;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _hostApplicationLifetime = hostApplicationLifetime;
        _fileCacheManager = fileCacheManager;
        _mareMediator = mareMediator;
        _playerPerformanceService = playerPerformanceService;
        _serverConfigManager = serverConfigManager;
        _personDownloadCoordinator = personDownloadCoordinator;
        _personApplyCoordinator = personApplyCoordinator;
    }

    /// <summary>
    /// Creates a <see cref="PairHandler"/> for the given <paramref name="pair"/> and wires all collaborators.
    /// If the pair is service-scoped, attempts to map its API URL override back to a configured server index.
    /// </summary>
    public PairHandler Create(Pair pair)
    {
        int? serverIndex = null;
        try
        {
            if (!string.IsNullOrEmpty(pair.ApiUrlOverride))
            {
                string NormalizeToHost(string value)
                {
                    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                    try
                    {
                        var uri = new Uri(value, UriKind.Absolute);
                        var builder = new UriBuilder(uri)
                        {
                            Port = -1
                        };
                        // Normalize ws(s) to http(s) to ensure TryCreate works with both styles, but we only compare hosts
                        if (string.Equals(builder.Scheme, "wss", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "https";
                        else if (string.Equals(builder.Scheme, "ws", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "http";
                        return builder.Uri.Host;
                    }
                    catch
                    {
                        // Best effort: if it's not a valid absolute URI, strip protocol-like prefixes and trailing slashes
                        var trimmed = value.Trim().TrimEnd('/');
                        // If there's a path segment, take only the part before the first '/'
                        var slashIdx = trimmed.IndexOf('/');
                        return slashIdx >= 0 ? trimmed.Substring(0, slashIdx) : trimmed;
                    }
                }

                var normalizedTarget = NormalizeToHost(pair.ApiUrlOverride);
                var urls = _serverConfigManager.GetServerApiUrls();
                var idx = Array.FindIndex(urls, u => string.Equals(NormalizeToHost(u), normalizedTarget, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) serverIndex = idx;
            }
        }
        catch { }

        return new PairHandler(_loggerFactory.CreateLogger<PairHandler>(), pair, _gameObjectHandlerFactory,
            _ipcManager, _fileDownloadManagerFactory.Create(serverIndex, pair.ApiUrlOverride), _pluginWarningNotificationManager, _dalamudUtilService, _hostApplicationLifetime,
            _fileCacheManager, _mareMediator, _playerPerformanceService, _personDownloadCoordinator, _personApplyCoordinator, _serverConfigManager);
    }
}
