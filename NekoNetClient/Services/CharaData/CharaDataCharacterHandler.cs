//
// Neko-Net Client — CharaDataCharacterHandler
// Purpose: Tracks and manages character-specific state while previewing/applying CharaData
//          during GPose or cutscenes. Keeps a set of "handled" characters (self and others),
//          applies and reverts appearance (Glamourer/Customize+) and triggers redraws via
//          Penumbra IPC. Provides helper methods to create GameObjectHandlers for actors.
//
// Key responsibilities:
// - Maintain a set of HandledCharaDataEntry for currently modified characters
// - Revert applied changes on GPose end, cutscene table changes, or disposal
// - Create GameObjectHandler for characters by name or object index (when available)
// - Update tracked metadata when server-side CharaData meta changes arrive
//
// Threading/usage notes:
// - Uses DalamudUtilService to query object table and to marshal work to the framework thread
// - Public revert calls schedule framework-thread work to ensure safe IPC/redraw
// - No functional changes compared to prior version; documentation only
//
using Microsoft.Extensions.Logging;
using NekoNet.API.Data.Enum;
using NekoNetClient.Interop.Ipc;
using NekoNetClient.PlayerData.Factories;
using NekoNetClient.PlayerData.Handlers;
using NekoNetClient.Services.CharaData.Models;
using NekoNetClient.Services.Mediator;

namespace NekoNetClient.Services.CharaData;

/// <summary>
/// Handles lifecycle and cleanup for characters whose appearance was modified by CharaData
/// operations. Ensures correct revert behavior across GPose/cutscene transitions and plugin
/// disposal, and exposes helper methods to obtain <see cref="GameObjectHandler"/> instances.
/// </summary>
public sealed class CharaDataCharacterHandler : DisposableMediatorSubscriberBase
{
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;
    private readonly HashSet<HandledCharaDataEntry> _handledCharaData = [];

    /// <summary>
    /// Gets the currently handled character entries that have been applied and are tracked
    /// for potential revert.
    /// </summary>
    public IEnumerable<HandledCharaDataEntry> HandledCharaData => _handledCharaData;

    /// <summary>
    /// Creates a new <see cref="CharaDataCharacterHandler"/> and wires GPose/cutscene subscriptions
    /// to guarantee reverts when appropriate.
    /// </summary>
    /// <param name="logger">Type-specific logger.</param>
    /// <param name="mediator">Mediator used to subscribe to GPose/cutscene events.</param>
    /// <param name="gameObjectHandlerFactory">Factory for obtaining object handlers.</param>
    /// <param name="dalamudUtilService">Utility service for framework access and lookups.</param>
    /// <param name="ipcManager">Aggregated IPC providers (Glamourer/Customize+/Penumbra).</param>
    public CharaDataCharacterHandler(ILogger<CharaDataCharacterHandler> logger, MareMediator mediator,
        GameObjectHandlerFactory gameObjectHandlerFactory, DalamudUtilService dalamudUtilService,
        IpcManager ipcManager)
        : base(logger, mediator)
    {
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        mediator.Subscribe<GposeEndMessage>(this, (_) =>
        {
            foreach (var chara in _handledCharaData)
            {
                RevertHandledChara(chara);
            }
        });

        mediator.Subscribe<CutsceneFrameworkUpdateMessage>(this, (_) => HandleCutsceneFrameworkUpdate());
    }

    private void HandleCutsceneFrameworkUpdate()
    {
        if (!_dalamudUtilService.IsInGpose) return;

        foreach (var entry in _handledCharaData.ToList())
        {
            var chara = _dalamudUtilService.GetGposeCharacterFromObjectTableByName(entry.Name, onlyGposeCharacters: true);
            if (chara is null)
            {
                RevertChara(entry.Name, entry.CustomizePlus).GetAwaiter().GetResult();
                _handledCharaData.Remove(entry);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var chara in _handledCharaData)
        {
            RevertHandledChara(chara);
        }
    }

    /// <summary>
    /// Reverts Glamourer/Customize+ changes for the provided character name and triggers a
    /// Penumbra redraw if the object can be resolved. Must run on the framework thread.
    /// </summary>
    /// <param name="name">Character name to revert.</param>
    /// <param name="cPlusId">Optional Customize+ application id used for a targeted revert.</param>
    public async Task RevertChara(string name, Guid? cPlusId)
    {
        Guid applicationId = Guid.NewGuid();
        await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
        if (cPlusId != null)
        {
            await _ipcManager.CustomizePlus.RevertByIdAsync(cPlusId).ConfigureAwait(false);
        }
        using var handler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
            () => _dalamudUtilService.GetGposeCharacterFromObjectTableByName(name, _dalamudUtilService.IsInGpose)?.Address ?? IntPtr.Zero, false)
            .ConfigureAwait(false);
        if (handler.Address != nint.Zero)
            await _ipcManager.Penumbra.RedrawAsync(Logger, handler, applicationId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to revert a tracked character by name. If present, removes it from the
    /// handled set and schedules a revert.
    /// </summary>
    /// <param name="name">Character name to revert.</param>
    /// <returns>True if the character was found and scheduled for revert.</returns>
    public async Task<bool> RevertHandledChara(string name)
    {
        var handled = _handledCharaData.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
        if (handled == null) return false;
        _handledCharaData.Remove(handled);
        await _dalamudUtilService.RunOnFrameworkThread(() => RevertChara(handled.Name, handled.CustomizePlus)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reverts the provided handled entry if not null and removes it from the handled set.
    /// </summary>
    /// <param name="handled">The handled chara entry to revert.</param>
    /// <returns>A task that completes once the operation has been scheduled.</returns>
    public Task RevertHandledChara(HandledCharaDataEntry? handled)
    {
        if (handled == null) return Task.CompletedTask;
        _handledCharaData.Remove(handled);
        return _dalamudUtilService.RunOnFrameworkThread(() => RevertChara(handled.Name, handled.CustomizePlus));
    }

    /// <summary>
    /// Adds a new handled character entry to the tracked set.
    /// </summary>
    /// <param name="handledCharaDataEntry">Entry describing the tracked character and meta.</param>
    internal void AddHandledChara(HandledCharaDataEntry handledCharaDataEntry)
    {
        _handledCharaData.Add(handledCharaDataEntry);
    }

    /// <summary>
    /// Updates tracked meta information for already handled characters. Only updates entries
    /// that are present in the provided lookup.
    /// </summary>
    /// <param name="newData">Mapping from FullId to new meta info.</param>
    public void UpdateHandledData(Dictionary<string, CharaDataMetaInfoExtendedDto?> newData)
    {
        foreach (var handledData in _handledCharaData)
        {
            if (newData.TryGetValue(handledData.MetaInfo.FullId, out var metaInfo) && metaInfo != null)
            {
                handledData.MetaInfo = metaInfo;
            }
        }
    }

    /// <summary>
    /// Attempts to create a handler for a character by name. Optionally restricts creation to
    /// GPose-only actors when in GPose.
    /// </summary>
    /// <param name="name">Character name to resolve.</param>
    /// <param name="gPoseOnly">When true and in GPose, only considers gpose characters.</param>
    /// <returns>A handler instance or null if the object couldn't be resolved.</returns>
    public async Task<GameObjectHandler?> TryCreateGameObjectHandler(string name, bool gPoseOnly = false)
    {
        var handler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
            () => _dalamudUtilService.GetGposeCharacterFromObjectTableByName(name, gPoseOnly && _dalamudUtilService.IsInGpose)?.Address ?? IntPtr.Zero, false)
            .ConfigureAwait(false);
        if (handler.Address == nint.Zero) return null;
        return handler;
    }

    /// <summary>
    /// Attempts to create a handler for a character by object table index.
    /// </summary>
    /// <param name="index">Object table index of the character.</param>
    /// <returns>A handler instance or null if the object couldn't be resolved.</returns>
    public async Task<GameObjectHandler?> TryCreateGameObjectHandler(int index)
    {
        var handler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
            () => _dalamudUtilService.GetCharacterFromObjectTableByIndex(index)?.Address ?? IntPtr.Zero, false)
            .ConfigureAwait(false);
        if (handler.Address == nint.Zero) return null;
        return handler;
    }
}
