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

    public DrawUserPair CreateDrawPair(string id, Pair user, List<GroupFullInfoDto> groups, GroupFullInfoDto? currentGroup, IApiActionRouter? router = null)
    {
        var api = router ?? _api;
        return new DrawUserPair(id + user.UserData.UID, user, groups, currentGroup, api, _uidDisplayHandler,
            _mediator, _selectTagForPairUi, _serverConfigurationManager, _uiSharedService, _playerPerformanceConfigService,
            _charaDataManager);
    }
}
