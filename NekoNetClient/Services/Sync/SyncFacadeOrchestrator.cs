using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using System.Threading;
using System.Threading.Tasks;

/*
 * Summary
 * -------
 * Orchestrates the lifecycle of ISyncFacade based on the player’s login
 * state exposed via Dalamud. Starts the facade on login (or immediately if
 * already logged in at service start) and stops it on logout. Integrates
 * with MareMediator to receive login/logout messages.
 */

namespace NekoNetClient.Services.Sync;

public sealed class SyncFacadeOrchestrator : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ISyncFacade _facade;
    private readonly Services.DalamudUtilService _dalamud;

    public SyncFacadeOrchestrator(ILogger<SyncFacadeOrchestrator> logger, MareMediator mediator, ISyncFacade facade, Services.DalamudUtilService dalamud)
        : base(logger, mediator)
    {
        _facade = facade;
        _dalamud = dalamud;

        Mediator.Subscribe<DalamudLoginMessage>(this, OnLogin);
        Mediator.Subscribe<DalamudLogoutMessage>(this, OnLogout);
    }

    private void OnLogin(DalamudLoginMessage msg)
    {
        _ = _facade.StartAsync(CancellationToken.None);
    }

    private void OnLogout(DalamudLogoutMessage msg)
    {
        _ = _facade.StopAsync(CancellationToken.None);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_dalamud.IsLoggedIn)
        {
            await _facade.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _facade.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
