using System.Runtime.CompilerServices;

namespace Eve.Services;

/// <summary>The PLEX average actually sent in this session's last successful config response.</summary>
public static class AdvertisedPlexPrice
{
    private sealed class Snapshot { public double? Value; }
    private static readonly ConditionalWeakTable<ClientSession, Snapshot> Values = new();
    public static void Set(ClientSession session, double? value)
    {
        var snapshot = Values.GetOrCreateValue(session);
        lock (snapshot) snapshot.Value = value;
    }
    public static double? Get(ClientSession session)
    {
        if (!Values.TryGetValue(session, out var snapshot)) return null;
        lock (snapshot) return snapshot.Value;
    }
}
