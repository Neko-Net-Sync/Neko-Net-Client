/*
    Neko-Net Client — Services.CommandManagerService
    ----------------------------------------------
    Purpose
    - Registers and handles in-game chat/console commands that control client features and UI.
*/
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using NekoNetClient.FileCache;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI;
using NekoNetClient.WebAPI.SignalR;
using NekoNetClient.Services.Sync;
using System.Globalization;

namespace NekoNetClient.Services;

public sealed class CommandManagerService : IDisposable
{
    private const string _commandName = "/nsync";

    private readonly ApiController _apiController;
    private readonly ISyncFacade _syncFacade;
    private readonly ICommandManager _commandManager;
    private readonly MareMediator _mediator;
    private readonly MareConfigService _mareConfigService;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public CommandManagerService(ICommandManager commandManager, PerformanceCollectorService performanceCollectorService,
        ServerConfigurationManager serverConfigurationManager, CacheMonitor periodicFileScanner,
        ApiController apiController, MareMediator mediator, MareConfigService mareConfigService, ISyncFacade syncFacade)
    {
        _commandManager = commandManager;
        _performanceCollectorService = performanceCollectorService;
        _serverConfigurationManager = serverConfigurationManager;
        _cacheMonitor = periodicFileScanner;
        _apiController = apiController;
        _mediator = mediator;
        _mareConfigService = mareConfigService;
        _syncFacade = syncFacade;
        _commandManager.AddHandler(_commandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Neko-Net UI" + Environment.NewLine + Environment.NewLine +
                "Additionally possible commands:" + Environment.NewLine +
                "\t /nsync toggle - Disconnects from Neko-Net session, if connected. Connects to last active Neko-Net session, if disconnected" + Environment.NewLine +
                "\t /nsync toggle on|off - Connects or disconnects to Neko-Net respectively" + Environment.NewLine +
                "\t /nsync gpose - Opens the Neko-Net Character Data Hub window" + Environment.NewLine +
                "\t /nsync analyze - Opens the Neko-Net Character Data Analysis window" + Environment.NewLine +
                "\t /nsync settings - Opens the Neko-Net Settings window" + Environment.NewLine +
        "\t /nsync services - Opens the Cross Sync window (per-service sessions)"
        });
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(_commandName);
    }

    private void OnCommand(string command, string args)
    {
        var splitArgs = args.ToLowerInvariant().Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);

        if (splitArgs.Length == 0)
        {
            // Interpret this as toggling the UI
            if (_mareConfigService.Current.HasValidSetup())
                _mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
            else
                _mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
            return;
        }

        if (!_mareConfigService.Current.HasValidSetup())
            return;

        if (string.Equals(splitArgs[0], "toggle", StringComparison.OrdinalIgnoreCase))
        {
            if (_apiController.ServerState == WebAPI.SignalR.Utils.ServerState.Disconnecting)
            {
                _mediator.Publish(new NotificationMessage("Mare disconnecting", "Cannot use /toggle while Neko-Net is still disconnecting",
                    NotificationType.Error));
            }

            if (_serverConfigurationManager.CurrentServer == null) return;
            var fullPause = splitArgs.Length > 1 ? splitArgs[1] switch
            {
                "on" => false,
                "off" => true,
                _ => !_serverConfigurationManager.CurrentServer.FullPause,
            } : !_serverConfigurationManager.CurrentServer.FullPause;

            if (fullPause != _serverConfigurationManager.CurrentServer.FullPause)
            {
                _serverConfigurationManager.CurrentServer.FullPause = fullPause;
                _serverConfigurationManager.Save();
                _ = _syncFacade.StartAsync(CancellationToken.None);
            }
        }
        else if (string.Equals(splitArgs[0], "gpose", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }
        else if (string.Equals(splitArgs[0], "rescan", StringComparison.OrdinalIgnoreCase))
        {
            _cacheMonitor.InvokeScan();
        }
        else if (string.Equals(splitArgs[0], "perf", StringComparison.OrdinalIgnoreCase))
        {
            if (splitArgs.Length > 1 && int.TryParse(splitArgs[1], CultureInfo.InvariantCulture, out var limitBySeconds))
            {
                _performanceCollectorService.PrintPerformanceStats(limitBySeconds);
            }
            else
            {
                _performanceCollectorService.PrintPerformanceStats();
            }
        }
        else if (string.Equals(splitArgs[0], "medi", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.PrintSubscriberInfo();
        }
        else if (string.Equals(splitArgs[0], "analyze", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
        }
        else if (string.Equals(splitArgs[0], "settings", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
        }
        else if (string.Equals(splitArgs[0], "services", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(ServicesUi)));
        }
    }
}
