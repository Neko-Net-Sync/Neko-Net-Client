/*
    Neko-Net Client — Services.PluginWarningNotificationService
    ----------------------------------------------------------
    Purpose
    - Detects missing plugin requirements for incoming character data and surfaces actionable notifications.
*/
using NekoNet.API.Data;
using NekoNet.API.Data.Comparer;
using NekoNetClient.Interop.Ipc;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.PlayerData.Data;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Mediator;
using System.Collections.Concurrent;

namespace NekoNetClient.Services;

/// <summary>
/// Detects when received player data references optional plugins that are not installed locally and emits
/// a single, consolidated warning per user to avoid notification spam.
/// </summary>
public class PluginWarningNotificationService
{
    private readonly ConcurrentDictionary<UserData, OptionalPluginWarning> _cachedOptionalPluginWarnings = new(UserDataComparer.Instance);
    private readonly IpcManager _ipcManager;
    private readonly MareConfigService _mareConfigService;
    private readonly MareMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginWarningNotificationService"/>.
    /// </summary>
    /// <param name="mareConfigService">Configuration to respect user preferences for optional warnings.</param>
    /// <param name="ipcManager">Provides IPC access to determine plugin availability.</param>
    /// <param name="mediator">Mediator used to publish consolidated warnings.</param>
    public PluginWarningNotificationService(MareConfigService mareConfigService, IpcManager ipcManager, MareMediator mediator)
    {
        _mareConfigService = mareConfigService;
        _ipcManager = ipcManager;
        _mediator = mediator;
    }

    /// <summary>
    /// Publishes a single warning notification indicating which optional plugins are missing locally in
    /// order to fully render data from the specified player.
    /// </summary>
    /// <param name="user">The user whose data triggered the check.</param>
    /// <param name="playerName">Display name used for the notification text.</param>
    /// <param name="changes">The set of changes present which may require optional plugins.</param>
    public void NotifyForMissingPlugins(UserData user, string playerName, HashSet<PlayerChanges> changes)
    {
        if (!_cachedOptionalPluginWarnings.TryGetValue(user, out var warning))
        {
            _cachedOptionalPluginWarnings[user] = warning = new()
            {
                ShownCustomizePlusWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShownHeelsWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShownHonorificWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShownMoodlesWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShowPetNicknamesWarning = _mareConfigService.Current.DisableOptionalPluginWarnings
            };
        }

        List<string> missingPluginsForData = [];
        if (changes.Contains(PlayerChanges.Heels) && !warning.ShownHeelsWarning && !_ipcManager.Heels.APIAvailable)
        {
            missingPluginsForData.Add("SimpleHeels");
            warning.ShownHeelsWarning = true;
        }
        if (changes.Contains(PlayerChanges.Customize) && !warning.ShownCustomizePlusWarning && !_ipcManager.CustomizePlus.APIAvailable)
        {
            missingPluginsForData.Add("Customize+");
            warning.ShownCustomizePlusWarning = true;
        }

        if (changes.Contains(PlayerChanges.Honorific) && !warning.ShownHonorificWarning && !_ipcManager.Honorific.APIAvailable)
        {
            missingPluginsForData.Add("Honorific");
            warning.ShownHonorificWarning = true;
        }

        if (changes.Contains(PlayerChanges.Moodles) && !warning.ShownMoodlesWarning && !_ipcManager.Moodles.APIAvailable)
        {
            missingPluginsForData.Add("Moodles");
            warning.ShownMoodlesWarning = true;
        }

        if (changes.Contains(PlayerChanges.PetNames) && !warning.ShowPetNicknamesWarning && !_ipcManager.PetNames.APIAvailable)
        {
            missingPluginsForData.Add("PetNicknames");
            warning.ShowPetNicknamesWarning = true;
        }

        if (missingPluginsForData.Any())
        {
            _mediator.Publish(new NotificationMessage("Missing plugins for " + playerName,
                $"Received data for {playerName} that contained information for plugins you have not installed. Install {string.Join(", ", missingPluginsForData)} to experience their character fully.",
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }
}