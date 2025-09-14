using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.FileCache;
using NekoNetClient.MareConfiguration;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.PlayerData.Services;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using System.Reflection;

namespace NekoNetClient;

#pragma warning disable S125 // Sections of code should not be commented out
/*
                                                                    (..,,...,,,,,+/,                ,,.....,,+
                                                              ..,,+++/((###%%%&&%%#(+,,.,,,+++,,,,//,,#&@@@@%+.
                                                          ...+//////////(/,,,,++,.,(###((//////////,..  .,#@@%/./
                                                       ,..+/////////+///,.,. ,&@@@@,,/////////////+,..    ,(##+,.
                                                    ,,.+//////////++++++..     ./#%#,+/////////////+,....,/((,..,
                                                  +..////////////+++++++...  .../##(,,////////////////++,,,+/(((+,
                                                +,.+//////////////+++++++,.,,,/(((+.,////////////////////////((((#/,,
                                              /+.+//////////++++/++++++++++,,...,++///////////////////////////((((##,
                                             /,.////////+++++++++++++++++++++////////+++//////++/+++++//////////((((#(+,
                                           /+.+////////+++++++++++++++++++++++++++++++++++++++++++++++++++++/////((((##+
                                          +,.///////////////+++++++++++++++++++++++++++++++++++++++++++++++++++///((((%/
                                         /.,/////////////////+++++++++++++++++++++++++++++++++++++++++++++++++++///+/(#+
                                        +,./////////////////+++++++++++++++++++++++++++++++++++++++++++++++,,+++++///((,
                                       ...////////++/++++++++++++++++++++++++,,++++++++++++++++++++++++++++++++++++//(,,
                                       ..//+,+///++++++++++++++++++,,,,+++,,,,,,,,,,,,++++++++,,+++++++++++++++++++//,,+
                                      ..,++,.++++++++++++++++++++++,,,,,,,,,,,,,,,,,,,++++++++,,,,,,,,,,++++++++++...
                                      ..+++,.+++++++++++++++++++,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,++,..,.
                                     ..,++++,,+++++++++++,+,,,,,,,,,,..,+++++++++,,,,,,.....................,//+,+
                                 ....,+++++,.,+++++++++++,,,,,,,,.+///(((((((((((((///////////////////////(((+,,,
                          .....,++++++++++..,+++++++++++,,.,,,.////////(((((((((((((((////////////////////+,,/
                      .....,++++++++++++,..,,+++++++++,,.,../////////////////((((((((((//////////////////,,+
                   ...,,+++++++++++++,.,,.,,,+++++++++,.,/////////////////(((//++++++++++++++//+++++++++/,,
                ....,++++++++++++++,.,++.,++++++++++++.,+////////////////////+++++++++++++++++++++++++///,,..
              ...,++++++++++++++++..+++..+++++++++++++.,//////////////////////////++++++++++++///////++++......
            ...++++++++++++++++++..++++.,++,++++++++++.+///////////////////////////////////////////++++++..,,,..
          ...+++++++++++++++++++..+++++..,+,,+++++++++.+//////////////////////////////////////////+++++++...,,,,..
         ..++++++++++++++++++++..++++++..,+,,+++++++++.+//////////////////////////////////////++++++++++,....,,,,..
       ...+++//(//////+++++++++..++++++,.,+++++++++++++,..,....,,,+++///////////////////////++++++++++++..,,,,,,,,...
      ..,++/(((((//////+++++++,.,++++++,,.,,,+++++++++++++++++++++++,.++////////////////////+++++++++++.....,,,,,,,...
     ..,//#(((((///////+++++++..++++++++++,...,++,++++++++++++++++,...+++/////////////////////+,,,+++...  ....,,,,,,...
   ...+//(((((//////////++++++..+++++++++++++++,......,,,,++++++,,,..+++////////////////////////+,....     ...,,,,,,,...
   ..,//((((////////////++++++..++++++/+++++++++++++,,...,,........,+/+//////////////////////((((/+,..     ....,.,,,,..
  ...+/////////////////////+++..++++++/+///+++++++++++++++++++++///+/+////////////////////////(((((/+...   .......,,...
  ..++////+++//////////////++++.+++++++++///////++++++++////////////////////////////////////+++/(((((/+..    .....,,...
  .,++++++++///////////////++++..++++//////////////////////////////////////////////////////++++++/((((++..    ........
  .+++++++++////////////////++++,.+++/////////////////////////////////////////////////////+++++++++/((/++..
 .,++++++++//////////////////++++,.+++//////////////////////////////////////////////////+++++++++++++//+++..
 .++++++++//////////////////////+/,.,+++////((((////////////////////////////////////////++++++++++++++++++...
 .++++++++///////////////////////+++..++++//((((((((///////////////////////////////////++++++++++++++++++++ .
 .++++++///////////////////////////++,.,+++++/(((((((((/////////////////////////////+++++++++++++++++++++++,..
 .++++++////////////////////////////+++,.,+++++++/((((((((//////////////////////////++++++++++++++++++++++++..
 .+++++++///////////////////++////////++++,.,+++++++++///////////+////////////////+++++++++++++++++++++++++,..
 ..++++++++++//////////////////////+++++++..+...,+++++++++++++++/++++++++++++++++++++++++++++++++++++++++++,...
  ..++++++++++++///////////////+++++++,...,,,,,.,....,,,,+++++++++++++++++++++++++++++++++++++++++++++++,,,,...
  ...++++++++++++++++++++++++++,,,,...,,,,,,,,,..,,++,,,.,,,,,,,,,,,,,,,,,,+++++++++++++++++++++++++,,,,,,,,..
   ...+++++++++++++++,,,,,,,,....,,,,,,,,,,,,,,,..,,++++++,,,,,,,,,,,,,,,,+++++++++++++++++++++++++,,,,,,,,,..
     ...++++++++++++,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,...,++++++++++++++++++++++++++++++++++++++++++++,,,,,,,,,,...
       ,....,++++++++++++++,,,+++++++,,,,,,,,,,,,,,,,,.,++++++++++++++++++++++++++++++++++++++++++++,,,,,,,,..

*/
#pragma warning restore S125 // Sections of code should not be commented out

public class MarePlugin : MediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _mareConfigService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private IServiceScope? _runtimeServiceScope;
    private Task? _launchTask = null;

    public MarePlugin(ILogger<MarePlugin> logger, MareConfigService mareConfigService,
        ServerConfigurationManager serverConfigurationManager,
        DalamudUtilService dalamudUtil,
        IServiceScopeFactory serviceScopeFactory, MareMediator mediator) : base(logger, mediator)
    {
        _mareConfigService = mareConfigService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtil = dalamudUtil;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!;
        Logger.LogInformation("Launching {name} {major}.{minor}.{build}", "Neko-Net", version.Major, version.Minor, version.Build);
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(MarePlugin), Services.Events.EventSeverity.Informational,
            $"Starting Neko-Net {version.Major}.{version.Minor}.{version.Build}")));

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (msg) => { if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager); });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());

        Mediator.StartQueueProcessing();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();

        DalamudUtilOnLogOut();

        Logger.LogDebug("Halting Neko-Net");

        return Task.CompletedTask;
    }

    private void DalamudUtilOnLogIn()
    {
        Logger?.LogDebug("Client login");
        if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager);
    }

    private void DalamudUtilOnLogOut()
    {
        Logger?.LogDebug("Client logout");

        _runtimeServiceScope?.Dispose();
    }

    private async Task WaitForPlayerAndLaunchCharacterManager()
    {
        while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        try
        {
            Logger?.LogDebug("Launching Managers");

            _runtimeServiceScope?.Dispose();
            _runtimeServiceScope = _serviceScopeFactory.CreateScope();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<UiService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CommandManagerService>();
            if (!_mareConfigService.Current.HasValidSetup() || !_serverConfigurationManager.HasValidConfig())
            {
                Mediator.Publish(new SwitchToIntroUiMessage());
                return;
            }
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CacheCreationService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<TransientResourceManager>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<VisibleUserDataDistributor>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<NotificationService>();

            // Kick off parallel service sessions for Neko-Net and Lightless
            try
            {
                var sessions = _runtimeServiceScope.ServiceProvider.GetRequiredService<NekoNetClient.Services.Sync.MultiSessionManager>();
                _ = sessions.StartAsync(NekoNetClient.WebAPI.SignalR.SyncService.NekoNet);
                _ = sessions.StartAsync(NekoNetClient.WebAPI.SignalR.SyncService.Lightless);
            }
            catch { /* ignore if session manager not available */ }

#if !DEBUG
            if (_mareConfigService.Current.LogLevel != LogLevel.Information)
            {
                Mediator.Publish(new NotificationMessage("Abnormal Log Level",
                    $"Your log level is set to '{_mareConfigService.Current.LogLevel}' which is not recommended for normal usage. Set it to '{LogLevel.Information}' in \"Neko-Net Settings -> Debug\" unless instructed otherwise.",
                    MareConfiguration.Models.NotificationType.Error, TimeSpan.FromSeconds(15000)));
            }
#endif
        }
        catch (Exception ex)
        {
            Logger?.LogCritical(ex, "Error during launch of managers");
        }
    }
}
