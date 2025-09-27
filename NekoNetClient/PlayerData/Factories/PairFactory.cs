/*
     Neko-Net Client — PlayerData.Factories.PairFactory
     ---------------------------------------------------
     Purpose
     - Creates Pair domain objects from DTOs and wires factories and services used by downstream handlers.

     Notes
     - Provides overloads for partial DTOs and a service-scoped creation that pins an API URL override
         for per-service notes/tags and routing.
*/
using Microsoft.Extensions.Logging;
using NekoNet.API.Dto.User;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.PlayerData.Factories;

/// <summary>
/// Factory for <see cref="Pair"/> instances created from server DTOs.
/// Supports service-scoped creation for multi-service environments.
/// </summary>
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

    /// <summary>
    /// Creates a <see cref="Pair"/> from a full DTO.
    /// </summary>
    public Pair Create(UserFullPairDto userPairDto)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userPairDto, _cachedPlayerFactory, _mareMediator, _serverConfigurationManager);
    }

    /// <summary>
    /// Creates a <see cref="Pair"/> from a partial DTO by expanding into a full equivalent.
    /// </summary>
    public Pair Create(UserPairDto userPairDto)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), new(userPairDto.User, userPairDto.IndividualPairStatus, [], userPairDto.OwnPermissions, userPairDto.OtherPermissions),
            _cachedPlayerFactory, _mareMediator, _serverConfigurationManager);
    }

    // Service-scoped creation (uses API URL override for notes/tags)
    /// <summary>
    /// Creates a service-scoped <see cref="Pair"/> from a full DTO that uses the given API URL override.
    /// </summary>
    public Pair CreateForService(UserFullPairDto userPairDto, string apiUrlOverride)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userPairDto, _cachedPlayerFactory, _mareMediator, _serverConfigurationManager, apiUrlOverride);
    }

    /// <summary>
    /// Creates a service-scoped <see cref="Pair"/> from a partial DTO that uses the given API URL override.
    /// </summary>
    public Pair CreateForService(UserPairDto userPairDto, string apiUrlOverride)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), new(userPairDto.User, userPairDto.IndividualPairStatus, [], userPairDto.OwnPermissions, userPairDto.OtherPermissions),
            _cachedPlayerFactory, _mareMediator, _serverConfigurationManager, apiUrlOverride);
    }
}
