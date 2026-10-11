using System.Text.Json;
using Eve.Inventory;
using Eve.Sde;
using Eve.Universe;

namespace Eve.Services.Space;

public sealed class UndockContraband
{
    private readonly PgItemStore _items;
    private readonly SolarSystemCatalog _systems;
    private readonly Dictionary<(int TypeId, int FactionId), double> _confiscateMinSecurity;

    private UndockContraband(
        PgItemStore items,
        SolarSystemCatalog systems,
        Dictionary<(int TypeId, int FactionId), double> confiscateMinSecurity)
    {
        _items = items;
        _systems = systems;
        _confiscateMinSecurity = confiscateMinSecurity;
    }

    public static async Task<UndockContraband> LoadAsync(
        SdeReader sde,
        PgItemStore items,
        SolarSystemCatalog systems,
        CancellationToken ct = default)
    {
        var rules = new Dictionary<(int TypeId, int FactionId), double>();
        foreach (var row in await sde.ReadAsync("contrabandTypes", cancellationToken: ct))
        {
            if (row.Key is <= 0 or > int.MaxValue
                || row.Doc.ValueKind != JsonValueKind.Object
                || !row.Doc.TryGetProperty("factions", out var factions)
                || factions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var faction in factions.EnumerateArray())
            {
                if (faction.ValueKind != JsonValueKind.Object
                    || !faction.TryGetProperty("_key", out var factionIdValue)
                    || factionIdValue.ValueKind != JsonValueKind.Number
                    || !factionIdValue.TryGetInt32(out var factionId)
                    || factionId <= 0
                    || !faction.TryGetProperty("confiscateMinSec", out var minSecurityValue)
                    || minSecurityValue.ValueKind != JsonValueKind.Number
                    || !minSecurityValue.TryGetDouble(out var minSecurity)
                    || !double.IsFinite(minSecurity))
                {
                    continue;
                }

                rules[((int)row.Key, factionId)] = minSecurity;
            }
        }

        return new UndockContraband(items, systems, rules);
    }

    public async Task<IReadOnlyList<int>> InspectAsync(
        long characterId,
        long shipId,
        int solarSystemId,
        CancellationToken ct = default)
    {
        var system = _systems.Get(solarSystemId);
        if (characterId <= 0
            || shipId <= 0
            || system is null
            || system.FactionId <= 0
            || !double.IsFinite(system.Security))
        {
            return Array.Empty<int>();
        }

        var ship = await _items.GetAsync(shipId, ct);
        if (ship is null || ship.OwnerId != characterId)
        {
            return Array.Empty<int>();
        }

        var contraband = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in await _items.InLocationAsync(shipId, characterId, ct))
        {
            if (item.Flag != ItemFlags.Cargo
                || item.TypeId <= 0
                || !_confiscateMinSecurity.TryGetValue((item.TypeId, system.FactionId), out var minSecurity)
                || system.Security < minSecurity
                || !seen.Add(item.TypeId))
            {
                continue;
            }

            contraband.Add(item.TypeId);
        }

        return contraband;
    }
}