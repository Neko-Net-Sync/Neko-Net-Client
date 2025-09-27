/*
    Neko-Net Client — Interop.BlockedCharacterHandler
    -----------------------------------------------
    Purpose
    - Lightweight helper that queries the game's blacklist (InfoProxyBlacklist) to determine
      whether a character should be considered blocked. Caches lookups by account/content id pair.
*/
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Microsoft.Extensions.Logging;

namespace NekoNetClient.Interop;

public unsafe class BlockedCharacterHandler
{
    private sealed record CharaData(ulong AccId, ulong ContentId);
    private readonly Dictionary<CharaData, bool> _blockedCharacterCache = new();

    private readonly ILogger<BlockedCharacterHandler> _logger;

    /// <summary>
    /// Initializes hooks from attributes and prepares the logger.
    /// </summary>
    public BlockedCharacterHandler(ILogger<BlockedCharacterHandler> logger, IGameInteropProvider gameInteropProvider)
    {
        gameInteropProvider.InitializeFromAttributes(this);
        _logger = logger;
    }

    /// <summary>
    /// Extracts the account and content id from a player pointer.
    /// </summary>
    private static CharaData GetIdsFromPlayerPointer(nint ptr)
    {
        if (ptr == nint.Zero) return new(0, 0);
        var castChar = ((BattleChara*)ptr);
        return new(castChar->Character.AccountId, castChar->Character.ContentId);
    }

    /// <summary>
    /// Determines whether the given character pointer corresponds to a blocked character.
    /// </summary>
    /// <param name="ptr">Pointer to the in-game character.</param>
    /// <param name="firstTime">True when this is the first cached lookup for the character.</param>
    /// <returns>True when blocked, otherwise false.</returns>
    public bool IsCharacterBlocked(nint ptr, out bool firstTime)
    {
        firstTime = false;
        var combined = GetIdsFromPlayerPointer(ptr);
        if (_blockedCharacterCache.TryGetValue(combined, out var isBlocked))
            return isBlocked;

        firstTime = true;
        var blockStatus = InfoProxyBlacklist.Instance()->GetBlockResultType(combined.AccId, combined.ContentId);
        _logger.LogTrace("CharaPtr {ptr} is BlockStatus: {status}", ptr, blockStatus);
        if ((int)blockStatus == 0)
            return false;
        return _blockedCharacterCache[combined] = blockStatus != InfoProxyBlacklist.BlockResultType.NotBlocked;
    }
}
