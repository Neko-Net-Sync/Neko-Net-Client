//
// Neko-Net Client — DrawEntityFactory
// Purpose: Central factory for creating UI draw-models that back the main/compact UI, including
//          folders for groups and tags, and per-user pair nodes. Keeps routing, handlers, and
//          services wired consistently across all produced components.
//
using Microsoft.Extensions.Logging;
using NekoNet.API.Dto.Group;
using NekoNetClient.MareConfiguration;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.CharaData;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI.Components;
using NekoNetClient.UI.Handlers;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Immutable;

namespace NekoNetClient.UI;

/// <summary>
/// Produces UI drawer entities for folders and pairs. Abstracts away the wiring of API routing,
/// tagging handlers, and shared services so UI code can focus on layout and interactions.
/// </summary>
public class DrawEntityFactory
{
    private readonly ILogger<DrawEntityFactory> _logger;
    private readonly ApiController _apiController;
    private readonly IApiActionRouter _api;
    private readonly MareMediator _mediator;
    private readonly SelectPairForTagUi _selectPairForTagUi;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiSharedService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly CharaDataManager _charaDataManager;
    private readonly SelectTagForPairUi _selectTagForPairUi;
    private readonly TagHandler _tagHandler;
    private readonly IdDisplayHandler _uidDisplayHandler;

    /// <summary>
    /// Creates a new factory with the main API controller and supporting services.
    /// </summary>
    public DrawEntityFactory(ILogger<DrawEntityFactory> logger, ApiController apiController, IdDisplayHandler uidDisplayHandler,
        SelectTagForPairUi selectTagForPairUi, MareMediator mediator,
        TagHandler tagHandler, SelectPairForTagUi selectPairForTagUi,
        ServerConfigurationManager serverConfigurationManager, UiSharedService uiSharedService,
        PlayerPerformanceConfigService playerPerformanceConfigService, CharaDataManager charaDataManager)
    {
        _logger = logger;
        _apiController = apiController;
        _api = new MainApiActionRouter(apiController);
        _uidDisplayHandler = uidDisplayHandler;
        _selectTagForPairUi = selectTagForPairUi;
        _mediator = mediator;
        _tagHandler = tagHandler;
        _selectPairForTagUi = selectPairForTagUi;
        _serverConfigurationManager = serverConfigurationManager;
        _uiSharedService = uiSharedService;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _charaDataManager = charaDataManager;
    }

    /// <summary>
    /// Creates a draw folder for a group and all associated pairs, optionally with a custom router
    /// and tag handler override.
    /// </summary>
    /// <param name="groupFullInfoDto">Full info for the group to render.</param>
    /// <param name="filteredPairs">Pairs filtered for this group.</param>
    /// <param name="allPairs">All visible pairs for reference.</param>
    /// <param name="router">Optional API action router override.</param>
    /// <param name="tagHandlerOverride">Optional tag handler override.</param>
    /// <returns>The draw folder entity representing the group.</returns>
    public DrawFolderGroup CreateDrawGroupFolder(GroupFullInfoDto groupFullInfoDto,
        Dictionary<Pair, List<GroupFullInfoDto>> filteredPairs,
        IImmutableList<Pair> allPairs,
        IApiActionRouter? router = null,
        TagHandler? tagHandlerOverride = null)
    {
        var api = router ?? _api;
        var th = tagHandlerOverride ?? _tagHandler;
        return new DrawFolderGroup(groupFullInfoDto.Group.GID, groupFullInfoDto, api,
            filteredPairs.Select(p => CreateDrawPair(groupFullInfoDto.Group.GID + p.Key.UserData.UID, p.Key, p.Value, groupFullInfoDto, api)).ToImmutableList(),
            allPairs, th, _uidDisplayHandler, _mediator, _uiSharedService);
    }

    /// <summary>
    /// Creates a draw folder for a tag and all pairs associated with the tag, optionally with a
    /// custom router and tag handler override.
    /// </summary>
    public DrawFolderTag CreateDrawTagFolder(string tag,
        Dictionary<Pair, List<GroupFullInfoDto>> filteredPairs,
        IImmutableList<Pair> allPairs,
        IApiActionRouter? router = null,
        TagHandler? tagHandlerOverride = null)
    {
        var api = router ?? _api;
        var th = tagHandlerOverride ?? _tagHandler;
        return new(tag, filteredPairs.Select(u => CreateDrawPair(tag, u.Key, u.Value, null, api)).ToImmutableList(),
            allPairs, th, api, _selectPairForTagUi, _uiSharedService);
    }

    /// <summary>
    /// Creates a per-user pair node used within folders.
    /// </summary>
    public DrawUserPair CreateDrawPair(string id, Pair user, List<GroupFullInfoDto> groups, GroupFullInfoDto? currentGroup, IApiActionRouter? router = null)
    {
        var api = router ?? _api;
        return new DrawUserPair(id + user.UserData.UID, user, groups, currentGroup, api, _uidDisplayHandler,
            _mediator, _selectTagForPairUi, _serverConfigurationManager, _uiSharedService, _playerPerformanceConfigService,
            _charaDataManager);
    }
}
