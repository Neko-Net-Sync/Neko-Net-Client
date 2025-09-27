/*
    Neko-Net Client — Services.NotificationService
    --------------------------------------------
    Purpose
    - Shows transient and persistent notifications to the user from mediator events.
*/
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.Services.Mediator;
using NotificationType = NekoNetClient.MareConfiguration.Models.NotificationType;

namespace NekoNetClient.Services;

/// <summary>
/// Routes mediator-driven <see cref="NotificationMessage"/> events to the chosen delivery channel(s)
/// (chat, toast, or both) based on user configuration. Only emits when the player is logged in.
/// </summary>
public class NotificationService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly INotificationManager _notificationManager;
    private readonly IChatGui _chatGui;
    private readonly MareConfigService _configurationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationService"/>, wiring the mediator and
    /// providing access to Dalamud chat and toast systems.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="mediator">Mediator used for receiving <see cref="NotificationMessage"/> events.</param>
    /// <param name="dalamudUtilService">Dalamud utility used to check login state.</param>
    /// <param name="notificationManager">Dalamud notification manager to show toasts.</param>
    /// <param name="chatGui">Dalamud chat interface for chat output.</param>
    /// <param name="configurationService">Configuration to resolve preferred notification locations.</param>
    public NotificationService(ILogger<NotificationService> logger, MareMediator mediator,
        DalamudUtilService dalamudUtilService,
        INotificationManager notificationManager,
        IChatGui chatGui, MareConfigService configurationService) : base(logger, mediator)
    {
        _dalamudUtilService = dalamudUtilService;
        _notificationManager = notificationManager;
        _chatGui = chatGui;
        _configurationService = configurationService;
    }

    /// <summary>
    /// Subscribes to <see cref="NotificationMessage"/> and starts processing notifications.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<NotificationMessage>(this, ShowNotification);
        return Task.CompletedTask;
    }

    /// <summary>
    /// No-op on stop; presence is tied to login state and mediator subscription.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Prints an error message to the chat log with a standardized prefix.
    /// </summary>
    /// <param name="message">The message content.</param>
    private void PrintErrorChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[Neko-Net] Error: " + message);
        _chatGui.PrintError(se.BuiltString);
    }

    /// <summary>
    /// Prints an informational message to the chat log with a standardized prefix.
    /// </summary>
    /// <param name="message">The message content.</param>
    private void PrintInfoChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[Neko-Net] Info: ").AddItalics(message ?? string.Empty);
        _chatGui.Print(se.BuiltString);
    }

    /// <summary>
    /// Prints a warning message to the chat log with a standardized prefix and UI foreground color.
    /// </summary>
    /// <param name="message">The message content.</param>
    private void PrintWarnChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[Neko-Net] ").AddUiForeground("Warning: " + (message ?? string.Empty), 31).AddUiForegroundOff();
        _chatGui.Print(se.BuiltString);
    }

    /// <summary>
    /// Writes the provided message to chat according to its severity.
    /// </summary>
    private void ShowChat(NotificationMessage msg)
    {
        switch (msg.Type)
        {
            case NotificationType.Info:
                PrintInfoChat(msg.Message);
                break;

            case NotificationType.Warning:
                PrintWarnChat(msg.Message);
                break;

            case NotificationType.Error:
                PrintErrorChat(msg.Message);
                break;
        }
    }

    /// <summary>
    /// Primary handler that routes notifications to chat and/or toast based on configuration.
    /// Respects login state and ignores messages when not logged in.
    /// </summary>
    private void ShowNotification(NotificationMessage msg)
    {
        Logger.LogInformation("{msg}", msg.ToString());

        if (!_dalamudUtilService.IsLoggedIn) return;

        switch (msg.Type)
        {
            case NotificationType.Info:
                ShowNotificationLocationBased(msg, _configurationService.Current.InfoNotification);
                break;

            case NotificationType.Warning:
                ShowNotificationLocationBased(msg, _configurationService.Current.WarningNotification);
                break;

            case NotificationType.Error:
                ShowNotificationLocationBased(msg, _configurationService.Current.ErrorNotification);
                break;
        }
    }

    /// <summary>
    /// Dispatches a notification to the configured location.
    /// </summary>
    /// <param name="msg">The notification message.</param>
    /// <param name="location">The configured output target.</param>
    private void ShowNotificationLocationBased(NotificationMessage msg, NotificationLocation location)
    {
        switch (location)
        {
            case NotificationLocation.Toast:
                ShowToast(msg);
                break;

            case NotificationLocation.Chat:
                ShowChat(msg);
                break;

            case NotificationLocation.Both:
                ShowToast(msg);
                ShowChat(msg);
                break;

            case NotificationLocation.Nowhere:
                break;
        }
    }

    /// <summary>
    /// Displays a Dalamud toast notification using the mapped severity.
    /// </summary>
    /// <param name="msg">The notification payload.</param>
    private void ShowToast(NotificationMessage msg)
    {
        Dalamud.Interface.ImGuiNotification.NotificationType dalamudType = msg.Type switch
        {
            NotificationType.Error => Dalamud.Interface.ImGuiNotification.NotificationType.Error,
            NotificationType.Warning => Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
            NotificationType.Info => Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            _ => Dalamud.Interface.ImGuiNotification.NotificationType.Info
        };

        _notificationManager.AddNotification(new Notification()
        {
            Content = msg.Message ?? string.Empty,
            Title = msg.Title,
            Type = dalamudType,
            Minimized = false,
            InitialDuration = msg.TimeShownOnScreen ?? TimeSpan.FromSeconds(3)
        });
    }
}