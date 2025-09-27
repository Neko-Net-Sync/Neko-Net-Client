/*
     Neko-Net Client — Services.UiService
     -----------------------------------
     Purpose
     - Central coordinator for rendering and toggling UIs, wiring mediator messages to window visibility,
         and managing per-frame UI updates.
*/
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Services.Mediator;
using NekoNetClient.UI;

namespace NekoNetClient.Services;

/// <summary>
/// Central UI coordinator that wires mediator messages to window visibility, owns the WindowSystem,
/// and integrates the global UI builder callbacks. Responsible for toggling primary windows based on
/// configuration state and drawing both the window system and file dialogs each frame.
/// </summary>
/// <remarks>
/// This service does not perform business logic; it focuses solely on UI orchestration, window lifecycle
/// management, and mediator event hookup. Creation of individual windows is delegated to <see cref="UiFactory"/>.
/// </remarks>
public sealed class UiService : DisposableMediatorSubscriberBase
{
    private readonly List<WindowMediatorSubscriberBase> _createdWindows = [];
    private readonly IUiBuilder _uiBuilder;
    private readonly FileDialogManager _fileDialogManager;
    private readonly ILogger<UiService> _logger;
    private readonly MareConfigService _mareConfigService;
    private readonly WindowSystem _windowSystem;
    private readonly UiFactory _uiFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="UiService"/> and registers window instances with the
    /// provided <see cref="WindowSystem"/>, hooking into the global UI draw and open events.
    /// </summary>
    /// <param name="logger">Logger used for tracing lifecycle events.</param>
    /// <param name="uiBuilder">Dalamud UI builder to attach draw and open handlers to.</param>
    /// <param name="mareConfigService">Configuration provider to decide which primary window to open.</param>
    /// <param name="windowSystem">Root window system which contains all windows.</param>
    /// <param name="windows">Pre-created windows to register with the window system.</param>
    /// <param name="uiFactory">Factory used to create on-demand windows (e.g., profile, permission, admin).</param>
    /// <param name="fileDialogManager">Shared file dialog manager, drawn alongside the window system.</param>
    /// <param name="mareMediator">Mediator used to subscribe/publish UI messages.</param>
    public UiService(ILogger<UiService> logger, IUiBuilder uiBuilder,
        MareConfigService mareConfigService, WindowSystem windowSystem,
        IEnumerable<WindowMediatorSubscriberBase> windows,
        UiFactory uiFactory, FileDialogManager fileDialogManager,
        MareMediator mareMediator) : base(logger, mareMediator)
    {
        _logger = logger;
        _logger.LogTrace("Creating {type}", GetType().Name);
        _uiBuilder = uiBuilder;
        _mareConfigService = mareConfigService;
        _windowSystem = windowSystem;
        _uiFactory = uiFactory;
        _fileDialogManager = fileDialogManager;

        _uiBuilder.DisableGposeUiHide = true;
        _uiBuilder.Draw += Draw;
        _uiBuilder.OpenConfigUi += ToggleUi;
        _uiBuilder.OpenMainUi += ToggleMainUi;

        foreach (var window in windows)
        {
            _windowSystem.AddWindow(window);
        }

        Mediator.Subscribe<ProfileOpenStandaloneMessage>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is StandaloneProfileUi ui
                && string.Equals(ui.Pair.UserData.AliasOrUID, msg.Pair.UserData.AliasOrUID, StringComparison.Ordinal)))
            {
                var window = _uiFactory.CreateStandaloneProfileUi(msg.Pair);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<OpenSyncshellAdminPanel>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is SyncshellAdminUI ui
                && string.Equals(ui.GroupFullInfo.GID, msg.GroupInfo.GID, StringComparison.Ordinal)))
            {
                var window = _uiFactory.CreateSyncshellAdminUi(msg.GroupInfo);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<OpenPermissionWindow>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is PermissionWindowUI ui
                && msg.Pair == ui.Pair))
            {
                var window = _uiFactory.CreatePermissionPopupUi(msg.Pair);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<RemoveWindowMessage>(this, (msg) =>
        {
            _windowSystem.RemoveWindow(msg.Window);
            _createdWindows.Remove(msg.Window);
            msg.Window.Dispose();
        });
    }

    /// <summary>
    /// Toggles the main UI window. If the configuration is valid, shows the compact UI;
    /// otherwise shows the introductory setup UI.
    /// </summary>
    public void ToggleMainUi()
    {
        if (_mareConfigService.Current.HasValidSetup())
            Mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
        else
            Mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    /// <summary>
    /// Toggles the settings UI. If the configuration is valid, shows settings;
    /// otherwise opens the introductory setup UI.
    /// </summary>
    public void ToggleUi()
    {
        if (_mareConfigService.Current.HasValidSetup())
            Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
        else
            Mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    /// <summary>
    /// Disposes the service, removing all windows from the <see cref="WindowSystem"/> and unhooking
    /// builder callbacks. Also disposes any on-demand windows that were created during runtime.
    /// </summary>
    /// <param name="disposing">True when called from <c>Dispose()</c>; false when from a finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _logger.LogTrace("Disposing {type}", GetType().Name);

        _windowSystem.RemoveAllWindows();

        foreach (var window in _createdWindows)
        {
            window.Dispose();
        }

        _uiBuilder.Draw -= Draw;
        _uiBuilder.OpenConfigUi -= ToggleUi;
        _uiBuilder.OpenMainUi -= ToggleMainUi;
    }

    /// <summary>
    /// Per-frame draw callback that renders the window system and any open file dialogs.
    /// </summary>
    private void Draw()
    {
        _windowSystem.Draw();
        _fileDialogManager.Draw();
    }
}