/*
    Neko-Net Client — Services.UiFactory
    -----------------------------------
    Purpose
    - Creates and returns UI windows/components used by the client, centralizing dependencies and setup.
*/
using Microsoft.Extensions.Logging;
using NekoNet.API.Dto.Group;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI;
using NekoNetClient.WebAPI.SignalR;

namespace NekoNetClient.Services;

/// <summary>
/// Creates UI windows used by the client and centralizes construction logic and service injection for
/// those windows. Keeps UI creation consistent and testable by avoiding scattered <c>new</c> calls.
/// </summary>
public class UiFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly MareProfileManager _mareProfileManager;
    private readonly PerformanceCollectorService _performanceCollectorService;

    /// <summary>
    /// Initializes a new instance of the <see cref="UiFactory"/>.
    /// </summary>
    /// <param name="loggerFactory">Factory for creating loggers for each window instance.</param>
    /// <param name="mareMediator">Mediator for UI messaging and event publication.</param>
    /// <param name="apiController">API controller used by administrative and permission windows.</param>
    /// <param name="uiSharedService">Shared UI helpers and drawing utilities.</param>
    /// <param name="pairManager">Provides access to pair and player state.</param>
    /// <param name="serverConfigManager">Server configuration manager for profile-related UI.</param>
    /// <param name="mareProfileManager">Profile manager to retrieve and cache user profiles.</param>
    /// <param name="performanceCollectorService">Performance probe for timing UI operations.</param>
    public UiFactory(ILoggerFactory loggerFactory, MareMediator mareMediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, ServerConfigurationManager serverConfigManager,
        MareProfileManager mareProfileManager, PerformanceCollectorService performanceCollectorService)
    {
        _loggerFactory = loggerFactory;
        _mareMediator = mareMediator;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _serverConfigManager = serverConfigManager;
        _mareProfileManager = mareProfileManager;
        _performanceCollectorService = performanceCollectorService;
    }

    /// <summary>
    /// Creates a new <see cref="SyncshellAdminUI"/> instance for administrating a specific syncshell group.
    /// </summary>
    /// <param name="dto">The full group information to be administrated.</param>
    /// <returns>A configured <see cref="SyncshellAdminUI"/> window.</returns>
    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto)
    {
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _mareMediator,
            _apiController, _uiSharedService, _pairManager, dto, _performanceCollectorService);
    }

    /// <summary>
    /// Creates a new <see cref="StandaloneProfileUi"/> instance for a given pair.
    /// </summary>
    /// <param name="pair">The pair whose profile will be displayed.</param>
    /// <returns>A configured <see cref="StandaloneProfileUi"/> window.</returns>
    public StandaloneProfileUi CreateStandaloneProfileUi(Pair pair)
    {
        return new StandaloneProfileUi(_loggerFactory.CreateLogger<StandaloneProfileUi>(), _mareMediator,
            _uiSharedService, _serverConfigManager, _mareProfileManager, _pairManager, pair, _performanceCollectorService);
    }

    /// <summary>
    /// Creates a new <see cref="PermissionWindowUI"/> instance to edit permissions for a given pair.
    /// </summary>
    /// <param name="pair">The pair for which the permissions should be managed.</param>
    /// <returns>A configured <see cref="PermissionWindowUI"/> window.</returns>
    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            _mareMediator, _uiSharedService, _apiController, _performanceCollectorService);
    }
}
