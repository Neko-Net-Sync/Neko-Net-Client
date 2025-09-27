/*
     Neko-Net Client — Services.Mediator.Messages
     -------------------------------------------
     Purpose
     - Defines all mediator messages used across the client for UI navigation, lifecycle, downloads,
         IPC plugin events, and sync orchestration. These are lightweight records with no behavior that
         carry event payloads across subsystems.
*/
using Dalamud.Game.ClientState.Objects.Types;
using NekoNet.API.Data;
using NekoNet.API.Dto;
using NekoNet.API.Dto.CharaData;
using NekoNet.API.Dto.Group;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.PlayerData.Handlers;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Events;
using NekoNetClient.WebAPI.Files.Models;
using System.Numerics;

namespace NekoNetClient.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
/// <summary>Navigate to the intro UI.</summary>
public record SwitchToIntroUiMessage : MessageBase;
/// <summary>Navigate to the main UI.</summary>
public record SwitchToMainUiMessage : MessageBase;
/// <summary>Open the settings UI.</summary>
public record OpenSettingsUiMessage : MessageBase;
/// <summary>Dalamud login completed.</summary>
public record DalamudLoginMessage : MessageBase;
/// <summary>Dalamud logout event.</summary>
public record DalamudLogoutMessage : MessageBase;
/// <summary>High-priority per-frame message (same-thread).</summary>
public record PriorityFrameworkUpdateMessage : SameThreadMessage;
/// <summary>Per-frame update message (same-thread).</summary>
public record FrameworkUpdateMessage : SameThreadMessage;
/// <summary>Class/job changed on the provided game object handler.</summary>
public record ClassJobChangedMessage(GameObjectHandler GameObjectHandler) : MessageBase;
/// <summary>Delayed per-frame update (same-thread).</summary>
public record DelayedFrameworkUpdateMessage : SameThreadMessage;
/// <summary>Started changing zones.</summary>
public record ZoneSwitchStartMessage : MessageBase;
/// <summary>Finished changing zones.</summary>
public record ZoneSwitchEndMessage : MessageBase;
/// <summary>Cutscene started.</summary>
public record CutsceneStartMessage : MessageBase;
/// <summary>GPose started (same-thread).</summary>
public record GposeStartMessage : SameThreadMessage;
/// <summary>GPose ended.</summary>
public record GposeEndMessage : MessageBase;
/// <summary>Cutscene ended.</summary>
public record CutsceneEndMessage : MessageBase;
/// <summary>Per-frame update restricted to cutscenes (same-thread).</summary>
public record CutsceneFrameworkUpdateMessage : SameThreadMessage;
/// <summary>Generic connection established.</summary>
public record ConnectedMessage(ConnectionDto Connection) : MessageBase;
/// <summary>Configured (server-indexed) connection established.</summary>
public record ConfiguredConnectedMessage(int ServerIndex, ConnectionDto Connection) : MessageBase;
/// <summary>Service connection established; includes service API base for scoping.</summary>
public record ServiceConnectedMessage(NekoNetClient.WebAPI.SignalR.SyncService Service, ConnectionDto Connection, string ServiceApiBase) : MessageBase;
/// <summary>Disconnected (same-thread).</summary>
public record DisconnectedMessage : SameThreadMessage;
/// <summary>Penumbra mod setting changed.</summary>
public record PenumbraModSettingChangedMessage : MessageBase;
/// <summary>Penumbra has initialized.</summary>
public record PenumbraInitializedMessage : MessageBase;
/// <summary>Penumbra has been disposed.</summary>
public record PenumbraDisposedMessage : MessageBase;
/// <summary>Penumbra signaled a redraw for a character (same-thread).</summary>
public record PenumbraRedrawMessage(IntPtr Address, int ObjTblIdx, bool WasRequested) : SameThreadMessage;
/// <summary>Glamourer changed something on the address.</summary>
public record GlamourerChangedMessage(IntPtr Address) : MessageBase;
/// <summary>Heels offset changed.</summary>
public record HeelsOffsetMessage : MessageBase;
/// <summary>Penumbra resource load reported (same-thread).</summary>
public record PenumbraResourceLoadMessage(IntPtr GameObject, string GamePath, string FilePath) : SameThreadMessage;
/// <summary>Customize+ changed.</summary>
public record CustomizePlusMessage(nint? Address) : MessageBase;
/// <summary>Honorific title changed.</summary>
public record HonorificMessage(string NewHonorificTitle) : MessageBase;
/// <summary>Moodles update for a given address.</summary>
public record MoodlesMessage(IntPtr Address) : MessageBase;
/// <summary>Pet names subsystem is ready.</summary>
public record PetNamesReadyMessage : MessageBase;
/// <summary>Pet nicknames changed.</summary>
public record PetNamesMessage(string PetNicknamesData) : MessageBase;
/// <summary>Honorific subsystem is ready.</summary>
public record HonorificReadyMessage : MessageBase;
/// <summary>Transient resource was changed for a given address.</summary>
public record TransientResourceChangedMessage(IntPtr Address) : MessageBase;
/// <summary>Request to halt scanning (by source string).</summary>
public record HaltScanMessage(string Source) : MessageBase;
/// <summary>Request to resume scanning (by source string).</summary>
public record ResumeScanMessage(string Source) : MessageBase;
/// <summary>A user-facing notification with title, message, type, and optional screen duration.</summary>
public record NotificationMessage
    (string Title, string Message, NotificationType Type, TimeSpan? TimeShownOnScreen = null) : MessageBase;
/// <summary>Request to create cache entries for the given object (same-thread).</summary>
public record CreateCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : SameThreadMessage;
/// <summary>Request to clear cache entries for the given object (same-thread).</summary>
public record ClearCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : SameThreadMessage;
/// <summary>Character data was created (same-thread).</summary>
public record CharacterDataCreatedMessage(CharacterData CharacterData) : SameThreadMessage;
/// <summary>Character data has been analyzed.</summary>
public record CharacterDataAnalyzedMessage : MessageBase;
/// <summary>Penumbra started a redraw.</summary>
public record PenumbraStartRedrawMessage(IntPtr Address) : MessageBase;
/// <summary>Penumbra ended a redraw.</summary>
public record PenumbraEndRedrawMessage(IntPtr Address) : MessageBase;
/// <summary>A hub is reconnecting (same-thread).</summary>
public record HubReconnectingMessage(Exception? Exception) : SameThreadMessage;
/// <summary>A hub has reconnected (same-thread).</summary>
public record HubReconnectedMessage(string? Arg) : SameThreadMessage;
/// <summary>A hub has closed (same-thread).</summary>
public record HubClosedMessage(Exception? Exception) : SameThreadMessage;
/// <summary>Download pipeline has received a "ready" signal for a request.</summary>
public record DownloadReadyMessage(Guid RequestId) : MessageBase;
/// <summary>Download pipeline started for a handler.</summary>
public record DownloadStartedMessage(GameObjectHandler DownloadId, Dictionary<string, FileDownloadStatus> DownloadStatus) : MessageBase;
/// <summary>Download pipeline finished for a handler.</summary>
public record DownloadFinishedMessage(GameObjectHandler DownloadId) : MessageBase;
/// <summary>Toggles a UI by its type.</summary>
public record UiToggleMessage(Type UiType) : MessageBase;
/// <summary>Publishes whether a player is currently uploading.</summary>
public record PlayerUploadingMessage(GameObjectHandler Handler, bool IsUploading) : MessageBase;
/// <summary>Clears profile data for the given user or globally when null.</summary>
public record ClearProfileDataMessage(UserData? UserData = null) : MessageBase;
/// <summary>Cycles pause state for a UID.</summary>
public record CyclePauseMessage(UserData UserData) : MessageBase;
/// <summary>Pauses a UID.</summary>
public record PauseMessage(UserData UserData) : MessageBase;
/// <summary>Publishes that a UID's player name is known.</summary>
public record PlayerNameKnownMessage(string UID, string PlayerName) : MessageBase;
/// <summary>Opens or toggles a profile popout window for a pair.</summary>
public record ProfilePopoutToggle(Pair? Pair) : MessageBase;
/// <summary>Compact UI changed size and position.</summary>
public record CompactUiChange(Vector2 Size, Vector2 Position) : MessageBase;
/// <summary>Open a profile window for the given pair in standalone mode.</summary>
public record ProfileOpenStandaloneMessage(Pair Pair) : MessageBase;
/// <summary>Request to remove a window from the mediator-managed UI list.</summary>
public record RemoveWindowMessage(WindowMediatorSubscriberBase Window) : MessageBase;
/// <summary>Refresh UI.</summary>
public record RefreshUiMessage : MessageBase;
/// <summary>Open ban-user popup for a pair within a group.</summary>
public record OpenBanUserPopupMessage(Pair PairToBan, GroupFullInfoDto GroupFullInfoDto) : MessageBase;
/// <summary>Open census popup.</summary>
public record OpenCensusPopupMessage() : MessageBase;
/// <summary>Open Syncshell admin panel for a group.</summary>
public record OpenSyncshellAdminPanel(GroupFullInfoDto GroupInfo) : MessageBase;
/// <summary>Open permission window for a pair.</summary>
public record OpenPermissionWindow(Pair Pair) : MessageBase;
/// <summary>Download limit changed (same-thread).</summary>
public record DownloadLimitChangedMessage() : SameThreadMessage;
/// <summary>Census values (gender/race/tribe) changed.</summary>
public record CensusUpdateMessage(byte Gender, byte RaceId, byte TribeId) : MessageBase;
/// <summary>Target the given pair in-game.</summary>
public record TargetPairMessage(Pair Pair) : MessageBase;
/// <summary>Combat or performance started.</summary>
public record CombatOrPerformanceStartMessage : MessageBase;
/// <summary>Combat or performance ended.</summary>
public record CombatOrPerformanceEndMessage : MessageBase;
/// <summary>Generic event message intended for the UI log.</summary>
public record EventMessage(Event Event) : MessageBase;
/// <summary>Penumbra mod directory changed.</summary>
public record PenumbraDirectoryChangedMessage(string? ModDirectory) : MessageBase;
/// <summary>Penumbra requests a redraw of a specific character (same-thread).</summary>
public record PenumbraRedrawCharacterMessage(ICharacter Character) : SameThreadMessage;
/// <summary>GameObject handler created (same-thread).</summary>
public record GameObjectHandlerCreatedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : SameThreadMessage;
/// <summary>GameObject handler destroyed (same-thread).</summary>
public record GameObjectHandlerDestroyedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : SameThreadMessage;
/// <summary>Halt character-data creation (resume toggles when true). (same-thread)</summary>
public record HaltCharaDataCreation(bool Resume = false) : SameThreadMessage;
/// <summary>GPose lobby: user joined.</summary>
public record GposeLobbyUserJoin(UserData UserData) : MessageBase;
/// <summary>GPose lobby: user left.</summary>
public record GPoseLobbyUserLeave(UserData UserData) : MessageBase;
/// <summary>GPose lobby: received chara data download.</summary>
public record GPoseLobbyReceiveCharaData(CharaDataDownloadDto CharaDataDownloadDto) : MessageBase;
/// <summary>GPose lobby: received pose data.</summary>
public record GPoseLobbyReceivePoseData(UserData UserData, PoseData PoseData) : MessageBase;
/// <summary>GPose lobby: received world data.</summary>
public record GPoseLobbyReceiveWorldData(UserData UserData, WorldData WorldData) : MessageBase;
/// <summary>Open CharaDataHub with a filter for a specific UID.</summary>
public record OpenCharaDataHubWithFilterMessage(UserData UserData) : MessageBase;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
