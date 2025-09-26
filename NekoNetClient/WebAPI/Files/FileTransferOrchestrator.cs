using System;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.WebAPI.Files.Models;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

namespace NekoNetClient.WebAPI.Files;

public class FileTransferOrchestrator : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<Guid, bool> _downloadReady = new();
    private readonly ConcurrentDictionary<int, Uri> _cdnByServerIdx = new();
    private readonly ConcurrentDictionary<string, Uri> _cdnByServiceApiBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _serverIdxByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly MareConfigService _mareConfig;
    private readonly object _semaphoreModificationLock = new();
    private readonly TokenProvider _tokenProvider;
    private readonly ServerConfigurationManager _servers;
    private int _availableDownloadSlots;
    private SemaphoreSlim _downloadSemaphore;
    private int CurrentlyUsedDownloadSlots => _availableDownloadSlots - _downloadSemaphore.CurrentCount;

    public FileTransferOrchestrator(ILogger<FileTransferOrchestrator> logger, MareConfigService mareConfig,
        MareMediator mediator, TokenProvider tokenProvider, HttpClient httpClient,
        ServerConfigurationManager servers) : base(logger, mediator)
    {
        _mareConfig = mareConfig;
        _tokenProvider = tokenProvider;
        _httpClient = httpClient;
        _servers = servers;
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Neko-Net", ver!.Major + "." + ver!.Minor + "." + ver!.Build));

        _availableDownloadSlots = mareConfig.Current.ParallelDownloads;
        _downloadSemaphore = new(_availableDownloadSlots, _availableDownloadSlots);

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
            try
            {
                var idx = _servers.CurrentServerIndex;
                if (FilesCdnUri != null)
                {
                    _cdnByServerIdx[idx] = FilesCdnUri;
                    var hostKey = FilesCdnUri.IsDefaultPort ? FilesCdnUri.Host : $"{FilesCdnUri.Host}:{FilesCdnUri.Port}";
                    _serverIdxByHost[hostKey] = idx;
                }
            }
            catch { }
        });

        Mediator.Subscribe<ConfiguredConnectedMessage>(this, (msg) =>
        {
            try
            {
                var cdn = msg.Connection.ServerInfo.FileServerAddress;
                _cdnByServerIdx[msg.ServerIndex] = cdn;
                var hostKey = cdn.IsDefaultPort ? cdn.Host : $"{cdn.Host}:{cdn.Port}";
                _serverIdxByHost[hostKey] = msg.ServerIndex;
            }
            catch { }
        });

        Mediator.Subscribe<ServiceConnectedMessage>(this, (msg) =>
        {
            try
            {
                var baseKey = NormalizeApiBase(msg.ServiceApiBase);
                if (string.IsNullOrEmpty(baseKey)) return;
                var cdn = msg.Connection.ServerInfo.FileServerAddress;
                if (cdn == null)
                {
                    if (Uri.TryCreate(baseKey, UriKind.Absolute, out var fallback))
                    {
                        _cdnByServiceApiBase[baseKey] = fallback;
                    }
                    // Also map the base API host to the matching configured server index for token routing
                    var idx = FindServerIndexByApiBase(baseKey);
                    if (idx.HasValue && Uri.TryCreate(baseKey, UriKind.Absolute, out var baseUri2))
                    {
                        var baseHostKey = baseUri2.IsDefaultPort ? baseUri2.Host : $"{baseUri2.Host}:{baseUri2.Port}";
                        _serverIdxByHost[baseHostKey] = idx.Value;
                        Logger.LogDebug("Mapped API base host {host} to server index {idx} (service connected, no CDN)", baseHostKey, idx.Value);
                    }
                    return;
                }
                _cdnByServiceApiBase[baseKey] = cdn;
                // Map CDN host to the configured server index (so token provider chooses the right token)
                var serverIdx = FindServerIndexByApiBase(baseKey);
                if (serverIdx.HasValue)
                {
                    var hostKey = cdn.IsDefaultPort ? cdn.Host : $"{cdn.Host}:{cdn.Port}";
                    _serverIdxByHost[hostKey] = serverIdx.Value;
                    // Also map the API base host (e.g., connect.neko-net.cc) to the same server index
                    if (Uri.TryCreate(baseKey, UriKind.Absolute, out var baseUri))
                    {
                        var baseHostKey = baseUri.IsDefaultPort ? baseUri.Host : $"{baseUri.Host}:{baseUri.Port}";
                        _serverIdxByHost[baseHostKey] = serverIdx.Value;
                        Logger.LogDebug("Mapped CDN host {cdnHost} and API base host {apiHost} to server index {idx}", hostKey, baseHostKey, serverIdx.Value);
                    }
                }
            }
            catch { }
        });

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = null;
        });
        Mediator.Subscribe<DownloadReadyMessage>(this, (msg) =>
        {
            _downloadReady[msg.RequestId] = true;
        });
    }

    public Uri? FilesCdnUri { private set; get; }
    public Uri? GetFilesCdnUriForServerIndex(int serverIndex)
        => _cdnByServerIdx.TryGetValue(serverIndex, out var uri) ? uri : null;
    public Uri? GetFilesCdnUriForApiBase(string apiBase)
    {
        var key = NormalizeApiBase(apiBase);
        if (string.IsNullOrEmpty(key)) return null;
        if (_cdnByServiceApiBase.TryGetValue(key, out var uri)) return uri;
        if (Uri.TryCreate(key, UriKind.Absolute, out var fallback))
        {
            _cdnByServiceApiBase[key] = fallback;
            return fallback;
        }
        return null;
    }
    public List<FileTransfer> ForbiddenTransfers { get; } = [];
    public bool IsInitialized => FilesCdnUri != null;

    // Register multiple CDN hosts for a given service API base to route tokens correctly
    public void RegisterServiceHosts(string? serviceApiBase, IEnumerable<Uri> hosts)
    {
        var baseKey = NormalizeApiBase(serviceApiBase);
        if (string.IsNullOrEmpty(baseKey)) return;
        var idx = FindServerIndexByApiBase(baseKey);
        if (!idx.HasValue) return;
        foreach (var h in hosts)
        {
            try
            {
                var key = h.IsDefaultPort ? h.Host : $"{h.Host}:{h.Port}";
                _serverIdxByHost[key] = idx.Value;
            }
            catch { }
        }
    }

    public void ClearDownloadRequest(Guid guid)
    {
        _downloadReady.Remove(guid, out _);
    }

    public bool IsDownloadReady(Guid guid)
    {
        if (_downloadReady.TryGetValue(guid, out bool isReady) && isReady)
        {
            return true;
        }

        return false;
    }

    public void ReleaseDownloadSlot()
    {
        try
        {
            _downloadSemaphore.Release();
            Mediator.Publish(new DownloadLimitChangedMessage());
        }
        catch (SemaphoreFullException)
        {
            // ignore
        }
    }

    public async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, Uri uri,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        return await SendRequestInternalAsync(requestMessage, ct, httpCompletionOption).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync<T>(HttpMethod method, Uri uri, T content, CancellationToken ct) where T : class
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        if (content is not ByteArrayContent)
            requestMessage.Content = JsonContent.Create(content);
        else
            requestMessage.Content = content as ByteArrayContent;
        return await SendRequestInternalAsync(requestMessage, ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestStreamAsync(HttpMethod method, Uri uri, ProgressableStreamContent content, CancellationToken ct)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        requestMessage.Content = content;
        return await SendRequestInternalAsync(requestMessage, ct).ConfigureAwait(false);
    }

    public async Task WaitForDownloadSlotAsync(CancellationToken token)
    {
        lock (_semaphoreModificationLock)
        {
            if (_availableDownloadSlots != _mareConfig.Current.ParallelDownloads && _availableDownloadSlots == _downloadSemaphore.CurrentCount)
            {
                _availableDownloadSlots = _mareConfig.Current.ParallelDownloads;
                _downloadSemaphore = new(_availableDownloadSlots, _availableDownloadSlots);
            }
        }

        await _downloadSemaphore.WaitAsync(token).ConfigureAwait(false);
        Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public long DownloadLimitPerSlot()
    {
        var limit = _mareConfig.Current.DownloadSpeedLimitInBytes;
        if (limit <= 0) return 0;
        limit = _mareConfig.Current.DownloadSpeedType switch
        {
            MareConfiguration.Models.DownloadSpeeds.Bps => limit,
            MareConfiguration.Models.DownloadSpeeds.KBps => limit * 1024,
            MareConfiguration.Models.DownloadSpeeds.MBps => limit * 1024 * 1024,
            _ => limit,
        };
        var currentUsedDlSlots = CurrentlyUsedDownloadSlots;
        var avaialble = _availableDownloadSlots;
        var currentCount = _downloadSemaphore.CurrentCount;
        var dividedLimit = limit / (currentUsedDlSlots == 0 ? 1 : currentUsedDlSlots);
        if (dividedLimit < 0)
        {
            Logger.LogWarning("Calculated Bandwidth Limit is negative, returning Infinity: {value}, CurrentlyUsedDownloadSlots is {currentSlots}, " +
                "DownloadSpeedLimit is {limit}, available slots: {avail}, current count: {count}", dividedLimit, currentUsedDlSlots, limit, avaialble, currentCount);
            return long.MaxValue;
        }
        return Math.Clamp(dividedLimit, 1, long.MaxValue);
    }

    private static string? NormalizeApiBase(string? apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase)) return null;
        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri))
        {
            var trimmed = apiBase.Trim().TrimEnd('/');
            if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return "https://" + trimmed.Substring(6);
            if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "http://" + trimmed.Substring(5);
            return trimmed;
        }
        var builder = new UriBuilder(uri);
        if (string.Equals(builder.Scheme, "wss", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "https";
        else if (string.Equals(builder.Scheme, "ws", StringComparison.OrdinalIgnoreCase)) builder.Scheme = "http";
        // Keep only scheme + authority for key matching
        builder.Path = string.Empty;
        builder.Query = null;
        builder.Fragment = null;
        // If port is default for scheme, let it be default
        if ((string.Equals(builder.Scheme, "https", StringComparison.OrdinalIgnoreCase) && builder.Port == 443)
            || (string.Equals(builder.Scheme, "http", StringComparison.OrdinalIgnoreCase) && builder.Port == 80))
        {
            builder.Port = -1;
        }
        return builder.Uri.ToString().TrimEnd('/');
    }

    private async Task<HttpResponseMessage> SendRequestInternalAsync(HttpRequestMessage requestMessage,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        string? token = null;
        try
        {
            if (requestMessage.RequestUri != null)
            {
                var key = requestMessage.RequestUri.IsDefaultPort
                    ? requestMessage.RequestUri.Host
                    : $"{requestMessage.RequestUri.Host}:{requestMessage.RequestUri.Port}";
                Logger.LogTrace("Auth routing lookup for host key: {key}", key);
                if (_serverIdxByHost.TryGetValue(key, out var idx))
                {
                    Logger.LogDebug("Routing auth via server index {idx} for host {host}", idx, key);
                    token = await _tokenProvider.GetOrUpdateTokenForServer(idx, ct ?? CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    Logger.LogTrace("No mapped server index for host key {key}; using default token", key);
                }
            }
        }
        catch { }

        token ??= await _tokenProvider.GetOrUpdateToken(ct ?? CancellationToken.None).ConfigureAwait(false);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (requestMessage.Content != null && requestMessage.Content is not StreamContent && requestMessage.Content is not ByteArrayContent)
        {
            var content = await ((JsonContent)requestMessage.Content).ReadAsStringAsync().ConfigureAwait(false);
            Logger.LogDebug("Sending {method} to {uri} (Content: {content})", requestMessage.Method, requestMessage.RequestUri, content);
        }
        else
        {
            Logger.LogDebug("Sending {method} to {uri}", requestMessage.Method, requestMessage.RequestUri);
        }

        try
        {
            if (ct != null)
                return await _httpClient.SendAsync(requestMessage, httpCompletionOption, ct.Value).ConfigureAwait(false);
            return await _httpClient.SendAsync(requestMessage, httpCompletionOption).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during SendRequestInternal for {uri}", requestMessage.RequestUri);
            throw;
        }
    }

    private int? FindServerIndexByApiBase(string baseApi)
    {
        try
        {
            if (!Uri.TryCreate(baseApi, UriKind.Absolute, out var apiUri)) return null;
            var host = apiUri.Host;
            var count = _servers.GetServerCount();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var srv = _servers.GetServerByIndex(i);
                    if (Uri.TryCreate(srv.ServerUri, UriKind.Absolute, out var srvUri))
                    {
                        if (string.Equals(srvUri.Host, host, StringComparison.OrdinalIgnoreCase)) return i;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }
}

