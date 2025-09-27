/*
    Neko-Net Client — PlayerData.Factories.GameObjectHandlerFactory
    ----------------------------------------------------------------
    Purpose
    - Creates GameObjectHandler instances on the Dalamud framework thread to ensure pointer safety.

    Notes
    - Wraps object-kind specific address accessors and wires performance collectors and mediator.
*/
using Microsoft.Extensions.Logging;
using NekoNet.API.Data.Enum;
using NekoNetClient.PlayerData.Handlers;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;

namespace NekoNetClient.PlayerData.Factories;

/// <summary>
/// Factory for <see cref="GameObjectHandler"/> that guarantees construction occurs on the
/// framework thread and wires required collaborators.
/// </summary>
public class GameObjectHandlerFactory
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly PerformanceCollectorService _performanceCollectorService;

    public GameObjectHandlerFactory(ILoggerFactory loggerFactory, PerformanceCollectorService performanceCollectorService, MareMediator mareMediator,
        DalamudUtilService dalamudUtilService)
    {
        _loggerFactory = loggerFactory;
        _performanceCollectorService = performanceCollectorService;
        _mareMediator = mareMediator;
        _dalamudUtilService = dalamudUtilService;
    }

    /// <summary>
    /// Creates a new <see cref="GameObjectHandler"/> for the given object kind.
    /// </summary>
    /// <param name="objectKind">Type of game object to observe.</param>
    /// <param name="getAddressFunc">Delegate to retrieve the object's address. Will be invoked on the framework thread.</param>
    /// <param name="isWatched">Whether this is the self-owned handler (publishes CreateCache messages).</param>
    /// <returns>The created handler.</returns>
    public async Task<GameObjectHandler> Create(ObjectKind objectKind, Func<nint> getAddressFunc, bool isWatched = false)
    {
        return await _dalamudUtilService.RunOnFrameworkThread(() => new GameObjectHandler(_loggerFactory.CreateLogger<GameObjectHandler>(),
            _performanceCollectorService, _mareMediator, _dalamudUtilService, objectKind, getAddressFunc, isWatched)).ConfigureAwait(false);
    }
}