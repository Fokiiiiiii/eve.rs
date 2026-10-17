using Eve.Dungeons;
using Eve.FactionWarfare;
using Eve.Services.Space;
using Eve.Services.Space.Waves;
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
    /// <summary>
    /// The corporation of a character and its loyalty point tax rate, for the member credits of the LP payouts (0 and 0 when the character is unknown).
    /// </summary>
    private async ValueTask<(long CorporationId, decimal TaxRate)> MemberLpTermsAsync(long characterId, CancellationToken cancellationToken)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is null)
            return (0, 0m);
        var corporation = await corporations.GetAsync(character.CorporationId, cancellationToken);
        return corporation is null ? (0, 0m) : (corporation.Id, corporation.LoyaltyPointTaxRate);
    }

    /// <summary>
    /// A pilot's kill of an NPC of a faction that fights the pilot's war faction, in a warzone system, is one kill on the pilot's and its corporation's
    /// factional warfare rows. The wiki gives no victory points for kills (complexes only) and the ship-kill LP is not built, so only the kill count is
    /// recorded (DEVIATIONS "Factional warfare"). The check is the client's IsCombatEnemyFaction.
    /// </summary>
    private void RecordFactionWarfareNpcKill(Eve.Services.Space.NpcKill kill)
    {
        if (kill.FinalBlowCharacterId <= 0 || kill.Profile.FactionId <= 0 || FwWarzones.WarzoneOf(kill.SolarSystemId) == 0)
            return;
        RecordFactionWarfareNpcKillAsync(kill.FinalBlowCharacterId, kill.Profile.FactionId, kill.SolarSystemId, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task RecordFactionWarfareNpcKillAsync(long characterId, long npcFactionId, long solarSystemId, CancellationToken cancellationToken)
    {
        var warFaction = await WarFactionOfAsync(characterId, cancellationToken);
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is null)
            return;
        await factionWarfare.RecordNpcKillAsync(characterId, character.CorporationId, warFaction, npcFactionId, solarSystemId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            cancellationToken);
    }

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

    /// <summary>
    /// The members of a war faction for the faction statistics: the characters whose war faction it is, the members of its militia corporation, and the
    /// largest enlisted corporation's members (characters per corporation).
    /// </summary>
    private async ValueTask<Eve.Services.Core.FwFactionMembers> FactionMembersAsync(long factionId, IReadOnlyList<long> enlisted, CancellationToken cancellationToken)
    {
        var militia = FwFactions.MilitiaCorporationOf(factionId);
        var total = await characters.CountWarFactionMembersAsync(factionId, enlisted, cancellationToken);
        var counts = await characters.CountByCorporationAsync([.. enlisted, militia], cancellationToken);
        long top = enlisted.Select(corporation => counts.GetValueOrDefault(corporation)).DefaultIfEmpty(0).Max();
        return new Eve.Services.Core.FwFactionMembers(total, counts.GetValueOrDefault(militia), top);
    }

    /// <summary>The alliance affiliation of each corporation that is in an alliance now (the alliance statistics group the corporations by it).</summary>
    private async ValueTask<IReadOnlyList<(long CorporationId, long AllianceId)>> AffiliationsAsync(CancellationToken cancellationToken) =>
        await corporations.CurrentAllianceAffiliationsAsync(cancellationToken);

    /// <summary>The dungeon catalog of the complexes' sites (set when the complexes start).</summary>
    private Eve.Dungeons.DungeonCatalog? fwDungeons;

    /// <summary>
    /// The open complexes of a system as the system scanner's anomaly overlay lists them (the <c>CosmicAnomalyInfo</c> fields of EveJS
    /// <c>buildAnomalySiteInfo</c>): the site is the complex's dungeon instance, the dungeon its template, and the target the first acceleration gate of its rooms.
    /// </summary>
    private async Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ComplexAnomaliesAsync(long solarSystemId, CancellationToken cancellationToken)
    {
        if (factionWarfareStore is null || fwDungeons is null || solarSystemId <= 0)
            return [];
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var rows = new List<System.Text.Json.Nodes.JsonObject>();
        foreach (var complex in await factionWarfareStore.OpenComplexesAsync(connection, null, cancellationToken))
        {
            if (complex.SolarSystemId != solarSystemId || complex.InstanceId <= 0)
                continue;
            if (await foundations.Content.GetAsync(complex.InstanceId, cancellationToken) is not { } instance || instance.Data<DungeonInstanceData>() is not { } data)
                continue;
            if (fwDungeons.GetDungeon(data.DungeonID) is not DungeonFound found)
                continue;
            var layout = DungeonRoomLayout.Build(found.Dungeon, data.Origin, dungeonWavesDirector!.GateRadii);
            var target = layout.Gates.Count > 0 ? SpaceContainers.AccelerationGateDeployableId(instance.Id, layout.Gates[0].GateIndex).ToString() : "";
            rows.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["siteID"] = instance.Id,
                ["instanceID"] = instance.Id,
                ["solarSystemID"] = solarSystemId,
                ["targetID"] = target,
                ["difficulty"] = found.Dungeon.Difficulty ?? 1,
                ["dungeonID"] = data.DungeonID,
                ["archetypeID"] = found.Dungeon.ArchetypeID,
                ["position"] = new System.Text.Json.Nodes.JsonArray(0.0, 0.0, 0.0),
                ["allowedTypes"] = new System.Text.Json.Nodes.JsonArray(),
            });
        }
        return rows;
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
        // the complexes are dungeon sites on the space runtime: without it there are none (the owner data and the pilots' sites are the space's)
        if (dungeonWavesDirector is null)
        {
            Console.Error.WriteLine("eve-host: factional warfare complexes need the space runtime; none are started");
            return;
        }
        fwDungeons = Eve.Dungeons.DungeonCatalog.LoadEmbedded();
        var sites = new FwComplexSites(dungeonWavesDirector, fwDungeons, foundations.Content,
            system => spaceSessions?.SessionsInSystem(system) ?? [],
            CurrentDungeonInstanceAsync,
            async (characterId, cancellationToken) => await WarFactionOfAsync(characterId, cancellationToken));
        var complexes = new FwComplexDirector(factionWarfareStore, foundations.Scheduler, foundations.Events, sites);
        await complexes.AttachAsync();
        foundations.Events.Subscribe("factionwarfare.complex-notices", [FwComplexDirector.ResolvedTopic],
            (context, cancellation) =>
            {
                NotifyComplexResolved(context.Event.Payload<FwComplexResolution>()!);
                return Task.CompletedTask;
            });
    }

    /// <summary>The victory point state of every occupied system (the client's fwVictoryPointMgr: the attacker points against the vulnerability threshold).</summary>
    private async ValueTask<IReadOnlyDictionary<long, Eve.Services.Core.FwVictoryPointState>> VictoryPointStatesAsync(CancellationToken cancellationToken) =>
        (await factionWarfare.OccupiedSystemsAsync(cancellationToken))
            .ToDictionary(system => system.SolarSystemId, system => new Eve.Services.Core.FwVictoryPointState(system.AttackerPoints));

    /// <summary>
    /// A complex resolved: the pilots in its system get OnFacWarDungeonCompleted (the client's hero notice: the side that conquered it, or the side that defended it),
    /// and the system's victory point state is updated for them.
    /// </summary>
    private void NotifyComplexResolved(FwComplexResolution resolution)
    {
        long faction = resolution.AttackersWon ? resolution.AttackerId : resolution.OccupierId;
        SendAtSystems([resolution.SolarSystemId], "OnFacWarDungeonCompleted", resolution.AttackersWon ? PyBool.True : PyBool.False, new PyInt(faction));
        SendAtSystems([resolution.SolarSystemId], "OnVictoryPointStateUpdated", new PyInt(resolution.SolarSystemId),
            Eve.Services.Core.FwVictoryPointMgrService.StatePayload(new Eve.Services.Core.FwVictoryPointState(resolution.AttackerPoints)));
    }
}
