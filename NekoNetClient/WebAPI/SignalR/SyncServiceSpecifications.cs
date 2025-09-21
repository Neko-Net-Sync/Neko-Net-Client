using System;
using System.Collections.Generic;

namespace NekoNetClient.WebAPI.SignalR;

internal static class SyncServiceSpecifications
{
    internal sealed record SyncServiceSpec(string Endpoint, bool UseMareToken, bool WebSocketsOnly);

    private static readonly IReadOnlyDictionary<SyncService, SyncServiceSpec> ServiceMap =
        new Dictionary<SyncService, SyncServiceSpec>
        {
            { SyncService.NekoNet,   new("wss://connect.neko-net.cc/mare", true,  true) },
            { SyncService.Lightless, new("wss://sync.lightless-sync.org/lightless", true, true) },
            { SyncService.TeraSync,  new("wss://tera.terasync.app/tera-sync-v2", true, true) },
        };

    public static bool TryGetSpec(SyncService service, out SyncServiceSpec spec)
        => ServiceMap.TryGetValue(service, out spec);

    public static IEnumerable<KeyValuePair<SyncService, SyncServiceSpec>> Enumerate()
        => ServiceMap;

    public static string? GetCanonicalHost(SyncService service)
    {
        if (!TryGetSpec(service, out var spec)) return null;
        if (!Uri.TryCreate(spec.Endpoint, UriKind.Absolute, out var uri)) return null;

        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    public static string? GetCanonicalAuthority(SyncService service)
    {
        if (!TryGetSpec(service, out var spec)) return null;
        if (!Uri.TryCreate(spec.Endpoint, UriKind.Absolute, out var uri)) return null;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string? GetCanonicalPath(SyncService service)
    {
        if (!TryGetSpec(service, out var spec)) return null;
        if (!Uri.TryCreate(spec.Endpoint, UriKind.Absolute, out var uri)) return null;

        return uri.AbsolutePath;
    }
}
