using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using NekoNet.API.Data.Enum;

namespace NekoNetClient.WebAPI.SignalR;

internal static class SyncServiceSpecifications
{
    internal sealed record Specification(
        string HubEndpoint,
        string ApiPath,
        bool UseMareToken,
        bool RequiresWebSockets,
        IReadOnlyCollection<string> Hosts)
    {
        internal string ApiBase
        {
            get
            {
                if (!Uri.TryCreate(HubEndpoint, UriKind.Absolute, out var uri))
                    return HubEndpoint;

                var builder = new UriBuilder(uri);
                if (string.Equals(builder.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                    builder.Scheme = "https";
                else if (string.Equals(builder.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
                    builder.Scheme = "http";

                builder.Port = -1;
                builder.Path = "/";
                builder.Query = string.Empty;
                builder.Fragment = string.Empty;
                return builder.Uri.ToString().TrimEnd('/');
            }
        }
    }

    private static readonly IReadOnlyDictionary<SyncService, Specification> _specifications =
        new ReadOnlyDictionary<SyncService, Specification>(new Dictionary<SyncService, Specification>
        {
            {
                SyncService.NekoNet,
                Create(
                    "wss://connect.neko-net.cc/mare",
                    "/mare",
                    useMareToken: true,
                    requiresWebSockets: true)
            },
            {
                SyncService.Lightless,
                Create(
                    "wss://sync.lightless-sync.org/lightless",
                    "/lightless",
                    useMareToken: true,
                    requiresWebSockets: true)
            },
            {
                SyncService.TeraSync,
                Create(
                    "wss://tera.terasync.app/tera-sync-v2",
                    "/tera-sync-v2",
                    useMareToken: true,
                    requiresWebSockets: true)
            },
        });

    public static Specification Get(SyncService service) => _specifications[service];

    public static bool TryGet(SyncService service, out Specification specification)
        => _specifications.TryGetValue(service, out specification!);

    public static IEnumerable<KeyValuePair<SyncService, Specification>> Enumerate()
        => _specifications;

    public static bool TryResolveServiceByHost(string? hostOrUrl, out SyncService service)
    {
        var normalized = NormalizeHostOrAuthority(hostOrUrl);
        if (normalized == null)
        {
            service = default;
            return false;
        }

        foreach (var pair in _specifications)
        {
            if (pair.Value.Hosts.Contains(normalized))
            {
                service = pair.Key;
                return true;
            }
        }

        service = default;
        return false;
    }

    internal static string? NormalizeHostOrAuthority(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
        {
            return null;
        }

        var value = hostOrUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = value.StartsWith("//", StringComparison.Ordinal)
                ? "https:" + value
                : "https://" + value.TrimStart('/');
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return hostOrUrl.Trim().Trim('/').ToLowerInvariant();
        }

        return NormalizeAuthority(uri);
    }

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static Specification Create(string endpoint, string apiPath, bool useMareToken, bool requiresWebSockets)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            var authority = NormalizeAuthority(uri);
            if (!string.IsNullOrEmpty(authority))
            {
                hosts.Add(authority);
            }

            var hostOnly = uri.Host.ToLowerInvariant();
            hosts.Add(hostOnly);
        }

        return new Specification(endpoint, NormalizePath(apiPath), useMareToken, requiresWebSockets, hosts);
    }

    private static string? NormalizeAuthority(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        var defaultPort = uri.Scheme switch
        {
            "http" or "ws" => 80,
            "https" or "wss" => 443,
            _ => -1,
        };

        if (uri.IsDefaultPort || uri.Port == -1 || uri.Port == defaultPort)
        {
            return host;
        }

        return host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }
}
