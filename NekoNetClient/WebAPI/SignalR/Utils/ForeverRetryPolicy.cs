/*
   File: ForeverRetryPolicy.cs
   Role: SignalR retry policy that retries indefinitely with backoff, emitting user notifications after
         initial attempts fail and publishing a Disconnected message when prolonged.
*/
using Microsoft.AspNetCore.SignalR.Client;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.Services.Mediator;

namespace NekoNetClient.WebAPI.SignalR.Utils;

/// <summary>
/// Retry policy that never gives up, gradually increasing delay and notifying the user after multiple failures.
/// </summary>
public class ForeverRetryPolicy : IRetryPolicy
{
    private readonly MareMediator _mediator;
    private bool _sentDisconnected = false;

    public ForeverRetryPolicy(MareMediator mediator)
    {
        _mediator = mediator;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        TimeSpan timeToWait = TimeSpan.FromSeconds(new Random().Next(10, 20));
        if (retryContext.PreviousRetryCount == 0)
        {
            _sentDisconnected = false;
            timeToWait = TimeSpan.FromSeconds(3);
        }
        else if (retryContext.PreviousRetryCount == 1) timeToWait = TimeSpan.FromSeconds(5);
        else if (retryContext.PreviousRetryCount == 2) timeToWait = TimeSpan.FromSeconds(10);
        else
        {
            if (!_sentDisconnected)
            {
                _mediator.Publish(new NotificationMessage("Connection lost", "Connection lost to server", NotificationType.Warning, TimeSpan.FromSeconds(10)));
                _mediator.Publish(new DisconnectedMessage());
            }
            _sentDisconnected = true;
        }

        return timeToWait;
    }
}