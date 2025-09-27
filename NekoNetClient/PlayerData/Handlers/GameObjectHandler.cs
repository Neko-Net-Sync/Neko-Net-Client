/*
     Neko-Net Client — PlayerData.Handlers.GameObjectHandler
     -------------------------------------------------------
     Purpose
     - Lightweight wrapper around a live game object pointer that tracks draw/visibility state, equipment and
         customization diffs, and publishes messages to drive caching and application flows.

     Behavior
     - Periodically checks object state on framework ticks, detecting changes in draw conditions, equipment, and
         customization. When self-owned, emits CreateCacheForObjectMessage to refresh cache entries.
     - Handles zoning, cutscenes, and plugin redraw windows to defer processing and avoid race conditions while
         the game is drawing or objects are invalid.

     Notes
     - Resolves addresses under the framework thread to ensure pointer safety.
     - Exposes helper methods for safe operations such as CompareNameAndThrow and ActOnFrameworkAfterEnsureNoDrawAsync.
*/
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.Logging;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;
using System.Runtime.CompilerServices;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer;
using ObjectKind = NekoNet.API.Data.Enum.ObjectKind;

namespace NekoNetClient.PlayerData.Handlers;

/// <summary>
/// Tracks and exposes the state of a specific game object (player, pet, minion/mount, companion),
/// detects draw/visibility conditions, and emits mediator messages to maintain cache coherence.
/// </summary>
public sealed class GameObjectHandler : DisposableMediatorSubscriberBase, IHighPriorityMediatorSubscriber
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly Func<IntPtr> _getAddress;
    private readonly bool _isOwnedObject;
    private readonly PerformanceCollectorService _performanceCollector;
    private byte _classJob = 0;
    private Task? _delayedZoningTask;
    private bool _haltProcessing = false;
    private CancellationTokenSource _zoningCts = new();

    public GameObjectHandler(ILogger<GameObjectHandler> logger, PerformanceCollectorService performanceCollector,
        MareMediator mediator, DalamudUtilService dalamudUtil, ObjectKind objectKind, Func<IntPtr> getAddress, bool ownedObject = true) : base(logger, mediator)
    {
        _performanceCollector = performanceCollector;
        ObjectKind = objectKind;
        _dalamudUtil = dalamudUtil;
        _getAddress = () =>
        {
            _dalamudUtil.EnsureIsOnFramework();
            return getAddress.Invoke();
        };
        _isOwnedObject = ownedObject;
        Name = string.Empty;

        if (ownedObject)
        {
            Mediator.Subscribe<TransientResourceChangedMessage>(this, (msg) =>
            {
                if (_delayedZoningTask?.IsCompleted ?? true)
                {
                    if (msg.Address != Address) return;
                    Mediator.Publish(new CreateCacheForObjectMessage(this));
                }
            });
        }

        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) => FrameworkUpdate());

        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (_) => ZoneSwitchEnd());
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) => ZoneSwitchStart());

        Mediator.Subscribe<CutsceneStartMessage>(this, (_) =>
        {
            _haltProcessing = true;
        });
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) =>
        {
            _haltProcessing = false;
            ZoneSwitchEnd();
        });
        Mediator.Subscribe<PenumbraStartRedrawMessage>(this, (msg) =>
        {
            if (msg.Address == Address)
            {
                _haltProcessing = true;
            }
        });
        Mediator.Subscribe<PenumbraEndRedrawMessage>(this, (msg) =>
        {
            if (msg.Address == Address)
            {
                _haltProcessing = false;
            }
        });

        Mediator.Publish(new GameObjectHandlerCreatedMessage(this, _isOwnedObject));

        _dalamudUtil.RunOnFrameworkThread(CheckAndUpdateObject).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Represents the current draw condition of the object, used to determine whether application is safe.
    /// </summary>
    public enum DrawCondition
    {
        None,
        ObjectZero,
        DrawObjectZero,
        RenderFlags,
        ModelInSlotLoaded,
        ModelFilesInSlotLoaded
    }

    /// <summary>Raw address of the target game object.</summary>
    public IntPtr Address { get; private set; }
    /// <summary>Computed draw condition used by IsBeingDrawn logic.</summary>
    public DrawCondition CurrentDrawCondition { get; set; } = DrawCondition.None;
    /// <summary>Detected gender for self-owned player objects.</summary>
    public byte Gender { get; private set; }
    /// <summary>Resolved name of the game object.</summary>
    public string Name { get; private set; }
    /// <summary>Kind of object that is tracked by this handler.</summary>
    public ObjectKind ObjectKind { get; }
    /// <summary>Detected race id for self-owned player objects.</summary>
    public byte RaceId { get; private set; }
    /// <summary>Detected tribe id for self-owned player objects.</summary>
    public byte TribeId { get; private set; }
    private byte[] CustomizeData { get; set; } = new byte[26];
    private IntPtr DrawObjectAddress { get; set; }
    private byte[] EquipSlotData { get; set; } = new byte[40];
    private ushort[] MainHandData { get; set; } = new ushort[3];
    private ushort[] OffHandData { get; set; } = new ushort[3];

    /// <summary>
    /// Runs the provided action once a safe "no draw" window is detected on the framework thread for this object.
    /// This avoids applying changes while the game is actively rendering the model.
    /// </summary>
    public async Task ActOnFrameworkAfterEnsureNoDrawAsync(Action<Dalamud.Game.ClientState.Objects.Types.ICharacter> act, CancellationToken token)
    {
        while (await _dalamudUtil.RunOnFrameworkThread(() =>
               {
                   if (_haltProcessing) CheckAndUpdateObject();
                   if (CurrentDrawCondition != DrawCondition.None) return true;
                   var gameObj = _dalamudUtil.CreateGameObject(Address);
                   if (gameObj is Dalamud.Game.ClientState.Objects.Types.ICharacter chara)
                   {
                       act.Invoke(chara);
                   }
                   return false;
               }).ConfigureAwait(false))
        {
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures that the current object still matches the provided name and pointer is valid, else throws.
    /// </summary>
    public void CompareNameAndThrow(string name)
    {
        if (!string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Player name not equal to requested name, pointer invalid");
        }
        if (Address == IntPtr.Zero)
        {
            throw new InvalidOperationException("Player pointer is zero, pointer invalid");
        }
    }

    /// <summary>Creates a Dalamud IGameObject from the current address.</summary>
    public Dalamud.Game.ClientState.Objects.Types.IGameObject? GetGameObject()
    {
        return _dalamudUtil.CreateGameObject(Address);
    }

    /// <summary>Marks the handler as invalid by clearing pointers and halting draw gating.</summary>
    public void Invalidate()
    {
        Address = IntPtr.Zero;
        DrawObjectAddress = IntPtr.Zero;
        _haltProcessing = false;
    }

    /// <summary>
    /// Computes the draw condition on the framework thread for this object.
    /// </summary>
    public async Task<bool> IsBeingDrawnRunOnFrameworkAsync()
    {
        return await _dalamudUtil.RunOnFrameworkThread(IsBeingDrawn).ConfigureAwait(false);
    }

    public override string ToString()
    {
        var owned = _isOwnedObject ? "Self" : "Other";
        return $"{owned}/{ObjectKind}:{Name} ({Address:X},{DrawObjectAddress:X})";
    }

    /// <summary>
    /// Publishes a destruction message so caches can be released for this object.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        Mediator.Publish(new GameObjectHandlerDestroyedMessage(this, _isOwnedObject));
    }

    private unsafe void CheckAndUpdateObject()
    {
        var prevAddr = Address;
        var prevDrawObj = DrawObjectAddress;

        Address = _getAddress();
        if (Address != IntPtr.Zero)
        {
            var drawObjAddr = (IntPtr)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)Address)->DrawObject;
            DrawObjectAddress = drawObjAddr;
            CurrentDrawCondition = DrawCondition.None;
        }
        else
        {
            DrawObjectAddress = IntPtr.Zero;
            CurrentDrawCondition = DrawCondition.DrawObjectZero;
        }

        CurrentDrawCondition = IsBeingDrawnUnsafe();

        if (_haltProcessing) return;

        bool drawObjDiff = DrawObjectAddress != prevDrawObj;
        bool addrDiff = Address != prevAddr;

        if (Address != IntPtr.Zero && DrawObjectAddress != IntPtr.Zero)
        {
            var chara = (Character*)Address;
            var name = chara->GameObject.NameString;
            bool nameChange = !string.Equals(name, Name, StringComparison.Ordinal);
            if (nameChange)
            {
                Name = name;
            }
            bool equipDiff = false;

            if (((DrawObject*)DrawObjectAddress)->Object.GetObjectType() == ObjectType.CharacterBase
                && ((CharacterBase*)DrawObjectAddress)->GetModelType() == CharacterBase.ModelType.Human)
            {
                var classJob = chara->CharacterData.ClassJob;
                if (classJob != _classJob)
                {
                    Logger.LogTrace("[{this}] classjob changed from {old} to {new}", this, _classJob, classJob);
                    _classJob = classJob;
                    Mediator.Publish(new ClassJobChangedMessage(this));
                }

                equipDiff = CompareAndUpdateEquipByteData((byte*)&((Human*)DrawObjectAddress)->Head);

                ref var mh = ref chara->DrawData.Weapon(WeaponSlot.MainHand);
                ref var oh = ref chara->DrawData.Weapon(WeaponSlot.OffHand);
                equipDiff |= CompareAndUpdateMainHand((Weapon*)mh.DrawObject);
                equipDiff |= CompareAndUpdateOffHand((Weapon*)oh.DrawObject);

                if (equipDiff)
                    Logger.LogTrace("Checking [{this}] equip data as human from draw obj, result: {diff}", this, equipDiff);
            }
            else
            {
                equipDiff = CompareAndUpdateEquipByteData((byte*)Unsafe.AsPointer(ref chara->DrawData.EquipmentModelIds[0]));
                if (equipDiff)
                    Logger.LogTrace("Checking [{this}] equip data from game obj, result: {diff}", this, equipDiff);
            }

            if (equipDiff && !_isOwnedObject) // send the message out immediately and cancel out, no reason to continue if not self
            {
                Logger.LogTrace("[{this}] Changed", this);
                return;
            }

            bool customizeDiff = false;

            if (((DrawObject*)DrawObjectAddress)->Object.GetObjectType() == ObjectType.CharacterBase
                && ((CharacterBase*)DrawObjectAddress)->GetModelType() == CharacterBase.ModelType.Human)
            {
                var gender = ((Human*)DrawObjectAddress)->Customize.Sex;
                var raceId = ((Human*)DrawObjectAddress)->Customize.Race;
                var tribeId = ((Human*)DrawObjectAddress)->Customize.Tribe;

                if (_isOwnedObject && ObjectKind == ObjectKind.Player
                    && (gender != Gender || raceId != RaceId || tribeId != TribeId))
                {
                    Mediator.Publish(new CensusUpdateMessage(gender, raceId, tribeId));
                    Gender = gender;
                    RaceId = raceId;
                    TribeId = tribeId;
                }

                customizeDiff = CompareAndUpdateCustomizeData(((Human*)DrawObjectAddress)->Customize.Data);
                if (customizeDiff)
                    Logger.LogTrace("Checking [{this}] customize data as human from draw obj, result: {diff}", this, customizeDiff);
            }
            else
            {
                customizeDiff = CompareAndUpdateCustomizeData(chara->DrawData.CustomizeData.Data);
                if (customizeDiff)
                    Logger.LogTrace("Checking [{this}] customize data from game obj, result: {diff}", this, equipDiff);
            }

            if ((addrDiff || drawObjDiff || equipDiff || customizeDiff || nameChange) && _isOwnedObject)
            {
                Logger.LogDebug("[{this}] Changed, Sending CreateCacheObjectMessage", this);
                Mediator.Publish(new CreateCacheForObjectMessage(this));
            }
        }
        else if (addrDiff || drawObjDiff)
        {
            CurrentDrawCondition = DrawCondition.DrawObjectZero;
            Logger.LogTrace("[{this}] Changed", this);
            if (_isOwnedObject && ObjectKind != ObjectKind.Player)
            {
                Mediator.Publish(new ClearCacheForObjectMessage(this));
            }
        }
    }

    private unsafe bool CompareAndUpdateCustomizeData(Span<byte> customizeData)
    {
        bool hasChanges = false;

        for (int i = 0; i < customizeData.Length; i++)
        {
            var data = customizeData[i];
            if (CustomizeData[i] != data)
            {
                CustomizeData[i] = data;
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    private unsafe bool CompareAndUpdateEquipByteData(byte* equipSlotData)
    {
        bool hasChanges = false;
        for (int i = 0; i < EquipSlotData.Length; i++)
        {
            var data = equipSlotData[i];
            if (EquipSlotData[i] != data)
            {
                EquipSlotData[i] = data;
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    private unsafe bool CompareAndUpdateMainHand(Weapon* weapon)
    {
        if ((nint)weapon == nint.Zero) return false;
        bool hasChanges = false;
        hasChanges |= weapon->ModelSetId != MainHandData[0];
        MainHandData[0] = weapon->ModelSetId;
        hasChanges |= weapon->Variant != MainHandData[1];
        MainHandData[1] = weapon->Variant;
        hasChanges |= weapon->SecondaryId != MainHandData[2];
        MainHandData[2] = weapon->SecondaryId;
        return hasChanges;
    }

    private unsafe bool CompareAndUpdateOffHand(Weapon* weapon)
    {
        if ((nint)weapon == nint.Zero) return false;
        bool hasChanges = false;
        hasChanges |= weapon->ModelSetId != OffHandData[0];
        OffHandData[0] = weapon->ModelSetId;
        hasChanges |= weapon->Variant != OffHandData[1];
        OffHandData[1] = weapon->Variant;
        hasChanges |= weapon->SecondaryId != OffHandData[2];
        OffHandData[2] = weapon->SecondaryId;
        return hasChanges;
    }

    private void FrameworkUpdate()
    {
        if (!_delayedZoningTask?.IsCompleted ?? false) return;

        try
        {
            _performanceCollector.LogPerformance(this, $"CheckAndUpdateObject>{(_isOwnedObject ? "Self" : "Other")}+{ObjectKind}/{(string.IsNullOrEmpty(Name) ? "Unk" : Name)}"
                + $"+{Address.ToString("X")}", CheckAndUpdateObject);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during FrameworkUpdate of {this}", this);
        }
    }

    /// <summary>
    /// Determines if the game is currently drawing this object, or if a global draw block is active,
    /// to defer expensive operations safely.
    /// </summary>
    private bool IsBeingDrawn()
    {
        if (_haltProcessing) CheckAndUpdateObject();

        if (_dalamudUtil.IsAnythingDrawing)
        {
            Logger.LogTrace("[{this}] IsBeingDrawn, Global draw block", this);
            return true;
        }

        Logger.LogTrace("[{this}] IsBeingDrawn, Condition: {cond}", this, CurrentDrawCondition);
        return CurrentDrawCondition != DrawCondition.None;
    }

    /// <summary>
    /// Low-level draw condition evaluation using FFXIV structs.
    /// </summary>
    private unsafe DrawCondition IsBeingDrawnUnsafe()
    {
        if (Address == IntPtr.Zero) return DrawCondition.ObjectZero;
        if (DrawObjectAddress == IntPtr.Zero) return DrawCondition.DrawObjectZero;
        var renderFlags = (((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)Address)->RenderFlags) != 0x0;
        if (renderFlags) return DrawCondition.RenderFlags;

        if (ObjectKind == ObjectKind.Player)
        {
            var modelInSlotLoaded = (((CharacterBase*)DrawObjectAddress)->HasModelInSlotLoaded != 0);
            if (modelInSlotLoaded) return DrawCondition.ModelInSlotLoaded;
            var modelFilesInSlotLoaded = (((CharacterBase*)DrawObjectAddress)->HasModelFilesInSlotLoaded != 0);
            if (modelFilesInSlotLoaded) return DrawCondition.ModelFilesInSlotLoaded;
        }

        return DrawCondition.None;
    }

    /// <summary>
    /// Signals that zone switching has finished and shortens the zoning timeout to resume work.
    /// </summary>
    private void ZoneSwitchEnd()
    {
        if (!_isOwnedObject) return;

        try
        {
            _zoningCts?.CancelAfter(2500);
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Zoning CTS cancel issue");
        }
    }

    /// <summary>
    /// Signals the start of zone switching and defers work for a grace period to avoid flapping state.
    /// </summary>
    private void ZoneSwitchStart()
    {
        if (!_isOwnedObject) return;

        _zoningCts = new();
        Logger.LogDebug("[{obj}] Starting Delay After Zoning", this);
        _delayedZoningTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(120), _zoningCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignore cancelled
            }
            finally
            {
                Logger.LogDebug("[{this}] Delay after zoning complete", this);
                _zoningCts.Dispose();
            }
        });
    }
}