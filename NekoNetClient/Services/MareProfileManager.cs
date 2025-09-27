/*
    Neko-Net Client — Services.MareProfileManager
    -------------------------------------------
    Purpose
    - Maintains cached profiles and mediates profile retrieval/display for pairs across services.
*/
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Data.Comparer;
using NekoNet.API.Dto.User;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Services.Mediator;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace NekoNetClient.Services;

/// <summary>
/// Manages retrieval and caching of user profiles from the service and provides helpers for UI to
/// consume consistent profile data (images, supporter badge, description, NSFW handling).
/// </summary>
public class MareProfileManager : MediatorSubscriberBase
{
    // Minimal 1x1 PNG base64 placeholders to guarantee valid images for UI rendering
    private const string _pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8AABgAD/AL+Y8QBAAAAAElFTkSuQmCC";

    private const string _noDescription = "-- User has no description set --";
    private const string _nsfw = "Profile not displayed - NSFW";
    private readonly ApiController _apiController;
    private readonly MareConfigService _mareConfigService;
    private readonly ConcurrentDictionary<UserData, MareProfileData> _mareProfiles = new(UserDataComparer.Instance);

    // Fallback/logo images. In the original repo these may be themed images; we use safe tiny placeholders here.
    private readonly MareProfileData _defaultProfileData = new(IsFlagged: false, IsNSFW: false, _pixel, string.Empty, _noDescription);
    private readonly MareProfileData _loadingProfileData = new(IsFlagged: false, IsNSFW: false, _pixel, string.Empty, "Loading Data from server...");
    private readonly MareProfileData _nsfwProfileData = new(IsFlagged: false, IsNSFW: false, _pixel, string.Empty, _nsfw);

    /// <summary>
    /// Initializes a new instance of the <see cref="MareProfileManager"/>, wiring mediator subscriptions
    /// and preparing the profile cache.
    /// </summary>
    public MareProfileManager(ILogger<MareProfileManager> logger, MareConfigService mareConfigService,
        MareMediator mediator, ApiController apiController) : base(logger, mediator)
    {
        _mareConfigService = mareConfigService;
        _apiController = apiController;

        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData != null)
                _mareProfiles.Remove(msg.UserData, out _);
            else
                _mareProfiles.Clear();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => _mareProfiles.Clear());
    }

    /// <summary>
    /// Retrieves profile data for the given user from cache, starting an asynchronous refresh
    /// from the service if no cached data exists.
    /// </summary>
    public MareProfileData GetMareProfile(UserData data)
    {
        if (!_mareProfiles.TryGetValue(data, out var profile))
        {
            _ = Task.Run(() => GetMareProfileFromService(data));
            return _loadingProfileData;
        }

        return profile;
    }

    /// <summary>
    /// Background retrieval and caching of profile data for the specified user.
    /// Applies NSFW visibility rules based on configuration and whether the profile is the player's own.
    /// </summary>
    private async Task GetMareProfileFromService(UserData data)
    {
        try
        {
            _mareProfiles[data] = _loadingProfileData;
            var profile = await _apiController.UserGetProfile(new UserDto(data)).ConfigureAwait(false);

            MareProfileData profileData = new(profile.Disabled, profile.IsNSFW ?? false,
                string.IsNullOrEmpty(profile.ProfilePictureBase64) ? _pixel : profile.ProfilePictureBase64,
                // Show supporter badge if an alias is set and differs from the UID
                !string.IsNullOrEmpty(data.Alias) && !string.Equals(data.Alias, data.UID, StringComparison.Ordinal) ? _pixel : string.Empty,
                string.IsNullOrEmpty(profile.Description) ? _noDescription : profile.Description);

            if (profileData.IsNSFW && !_mareConfigService.Current.ProfilesAllowNsfw
                && !string.Equals(_apiController.UID, data.UID, StringComparison.Ordinal))
            {
                _mareProfiles[data] = _nsfwProfileData;
            }
            else
            {
                _mareProfiles[data] = profileData;
            }
        }
        catch (Exception ex)
        {
            // if fails save DefaultProfileData to dict
            Logger.LogWarning(ex, "Failed to get Profile from service for user {user}", data);
            _mareProfiles[data] = _defaultProfileData;
        }
    }
}
