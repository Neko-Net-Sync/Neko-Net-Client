using Microsoft.Extensions.Logging;
using NekoNet.API.Dto.User;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.PlayerData.Factories;

public class PairFactory
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public PairFactory(ILoggerFactory loggerFactory, PairHandlerFactory cachedPlayerFactory,
        MareMediator mareMediator, ServerConfigurationManager serverConfigurationManager)
    {
        _loggerFactory = loggerFactory;
        _cachedPlayerFactory = cachedPlayerFactory;
        _mareMediator = mareMediator;
        _serverConfigurationManager = serverConfigurationManager;
    }

    public Pair Create(UserFullPairDto userPairDto)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userPairDto, _cachedPlayerFactory, _mareMediator, _serverConfigurationManager);
    }

    public Pair Create(UserPairDto userPairDto)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), new(userPairDto.User, userPairDto.IndividualPairStatus, [], userPairDto.OwnPermissions, userPairDto.OtherPermissions),
            _cachedPlayerFactory, _mareMediator, _serverConfigurationManager);
    }

    // Service-scoped creation (uses API URL override for notes/tags)
    public Pair CreateForService(UserFullPairDto userPairDto, string apiUrlOverride)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userPairDto, _cachedPlayerFactory, _mareMediator, _serverConfigurationManager, apiUrlOverride);
    }

    public Pair CreateForService(UserPairDto userPairDto, string apiUrlOverride)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), new(userPairDto.User, userPairDto.IndividualPairStatus, [], userPairDto.OwnPermissions, userPairDto.OtherPermissions),
            _cachedPlayerFactory, _mareMediator, _serverConfigurationManager, apiUrlOverride);
    }
}
