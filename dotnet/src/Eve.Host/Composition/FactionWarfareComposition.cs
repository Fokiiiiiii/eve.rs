using Eve.FactionWarfare;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private FwStore factionWarfareStore = default!;
    private FwService factionWarfare = default!;

    /// <summary>
    /// The factional warfare state (schema <c>factionwarfare</c>): the store, and on the first start the occupied systems of the two warzones, seeded
    /// from the warzone map and the SDE faction of each system (<see cref="FwWarzones.InitialOccupier"/>). Later starts keep the stored state. The
    /// facWarMgr service and the donation tax read this state.
    /// </summary>
    /// <summary>
    /// The sovereignty snapshot with its factional warfare occupations replaced by the factionwarfare state: the client's warzone occupation reads
    /// the store, which is the owner of the occupier (the sovereignty document carries none of the state).
    /// </summary>
    private async ValueTask<Eve.Services.Core.SovFwReadSnapshot> FactionWarfareSnapshotAsync(Eve.Services.ServiceCall call)
    {
        var snapshot = await sovereigntyReadModel.ReadSnapshotAsync(call);
        var occupied = await factionWarfare.OccupiedSystemsAsync(call.Cancellation);
        return snapshot with
        {
            FwOccupationStates = occupied.Select(system => new Eve.Services.Core.FwOccupationRecord(system.WarzoneId, system.SolarSystemId, system.OccupierId)).ToArray(),
        };
    }

    /// <summary>
    /// map.GetFacWarZoneInfo: the points a faction holds in its warzone (the systems it occupies), the warzone's size (the points that make 100
    /// percent, which the hub donation tax reads) and the upgrade level of each system it holds. Null for a non-occupier (the caller's fallback).
    /// </summary>
    private async ValueTask<PyValue?> FactionWarfareZoneInfoAsync(long factionId, CancellationToken cancellationToken)
    {
        if (!FwFactions.IsOccupier(factionId))
            return null;
        var warzoneId = FwWarzones.Ids.First(id => FwWarzones.FactionsOf(id).Contains(factionId));
        var held = (await factionWarfare.OccupiedSystemsAsync(cancellationToken))
            .Where(system => system.WarzoneId == warzoneId && system.OccupierId == factionId)
            .ToArray();
        var levels = held.Select(system => new KeyValuePair<PyValue, PyValue>(new PyInt(system.SolarSystemId), new PyInt(FwRules.UpgradeLevel(system.LpPool))));
        return Py.KeyVal(
            ("factionID", new PyInt(factionId)),
            ("factionPoints", new PyInt(held.Length)),
            ("maxWarZonePoints", new PyInt(FwWarzones.SystemsOf(warzoneId).Count)),
            ("systemUpgradeLevel", new PyDict([.. levels])));
    }

    /// <summary>
    /// The factional warfare upgrade level of a system for a character's market orders: the system's LP pool, unless its occupier is an enemy of the
    /// character's war faction (the direct enlistment, else the corporation's). Read in the caller's transaction.
    /// </summary>
    /// <summary>The war faction of a character: its direct enlistment, else its corporation's faction (0: none, or no such character).</summary>
    private async ValueTask<long> WarFactionOfAsync(long characterId, CancellationToken cancellationToken)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is null)
            return 0;
        var corporationFaction = Eve.Services.Corporations.CorporationWire.FactionId(await corporations.GetAsync(character.CorporationId, cancellationToken));
        return character.DirectFactionId ?? corporationFaction ?? 0;
    }

    private async ValueTask<int> FactionWarfareUpgradeLevelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long solarSystemId,
        CancellationToken cancellationToken)
    {
        var warFaction = await WarFactionOfAsync(characterId, cancellationToken);
        var system = await factionWarfareStore.SystemAsync(connection, transaction, solarSystemId, false, cancellationToken);
        return system is null ? 0 : FwRules.EffectiveUpgradeLevel(system.LpPool, system.OccupierId, warFaction);
    }

    private async Task FactionWarfare()
    {
        var schema = Env("EVE_FACTIONWARFARE_SCHEMA", FwStore.DefaultSchema);
        await FwStore.MigrateAsync(source, schema);
        factionWarfareStore = new FwStore(source, schema);
        factionWarfare = new FwService(factionWarfareStore, loyaltyPoints);
        var systems = new List<FwSystem>();
        foreach (var warzoneId in FwWarzones.Ids)
        {
            foreach (var systemId in FwWarzones.SystemsOf(warzoneId))
            {
                var faction = (long)(solarSystems.Get((int)systemId)?.FactionId ?? 0);
                var occupier = FwWarzones.InitialOccupier(warzoneId, faction);
                if (occupier != 0)
                    systems.Add(new FwSystem(systemId, warzoneId, occupier, 0, 0, 0));
            }
        }
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await factionWarfareStore.SeedSystemsAsync(connection, transaction, systems);
        await transaction.CommitAsync();
    }
}
