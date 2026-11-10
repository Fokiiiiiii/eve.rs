using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Npc;
using Eve.Inventory;
using Eve.Services.Space;
using Eve.Space;
using Eve.Structures;
using Eve.Wallet;
using Npgsql;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

/// <summary>
/// The Encounter Surveillance Systems: their store, the calls of the client's essMgr and dynamic resource cache, each SDE-eligible Main Bank in space, and the clock.
/// </summary>
internal sealed partial class CharacterServicesComposition
{
    private const long GeneratedEssItemIdBase = 8_440_000_000_000_000;
    private const int KnownSpaceSolarSystemMinimum = 30_000_000;
    private const int KnownSpaceSolarSystemMaximum = 30_999_999;
    private const int ZarzakhSolarSystemId = 30_100_000;
    private static readonly HashSet<int> NonClaimableEssRegionIds = [10000070, 10000004, 10000017, 10000019];

    private string essSchema = default!;
    private PgEssStore? _ess;
    private PgDynamicBountyStore? _dynamicBounty;
    private Eve.Services.Space.EssMgrService? _essMgrService;
    private Eve.Services.Space.EssMgrService EssMgr => _essMgrService ??= new(EssCallbacks());
    private Dictionary<int, string> _essQuadrantBySystem = [];
    private readonly SemaphoreSlim _essGeometryGate = new(1, 1);
    private EssGeometry? _essGeometry;

    private sealed record EssGeometry(int GroupId, double Radius, double ShieldCapacity, double ArmorHp, double StructureHp);

    private PgEssStore Ess => _ess ??= new PgEssStore(
        source, new WalletTransactions(source, walletSchema), essSchema,
        message => Console.Error.WriteLine($"eve-host: ess: {message}"),
        holderOf: async (systemId, characterId, essId, cancellationToken) =>
            spaceSessions is { } space ? await space.EssHolderAsync(systemId, characterId, essId, cancellationToken) : null,
        consumeKey: ConsumeEssKeyAsync,
        quadrantOf: systemId => _essQuadrantBySystem.TryGetValue(systemId, out var quadrant) ? quadrant : null,
        sovereigntyOwnerOf: SovereigntyOwnerOfAsync,
        corporationOf: CharacterCorporationOfAsync);

    /// <summary>Registers an ESS for every claimable nullsec system from the pinned SDE, then applies any explicit operator overrides.</summary>
    private async Task RegisterEssSystemsAsync()
    {
        var entries = Env("EVE_ESS_SYSTEMS", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var overrides = new List<(int SystemId, long EssId, long? OwnerId)>();
        foreach (var entry in entries)
        {
            var parts = entry.Split(':');
            if (parts.Length is < 2 or > 3 || !int.TryParse(parts[0], out var systemId) || systemId <= 0 ||
                !long.TryParse(parts[1], out var essId) || essId <= 0 ||
                (parts.Length == 3 && (!long.TryParse(parts[2], out var ownerId) || ownerId <= 0)))
            {
                throw new InvalidOperationException($"EVE_ESS_SYSTEMS: '{entry}' is not solarSystemId:essId[:ownerId]");
            }
            var owner = parts.Length == 3 ? long.Parse(parts[2], CultureInfo.InvariantCulture) : (long?)null;
            overrides.Add((systemId, essId, owner));
        }
        _essQuadrantBySystem = await ReadEssQuadrantsAsync();
        var deploymentSystems = await ReadClaimableEssDeploymentSystemsAsync();
        await Ess.RegisterManyAsync(deploymentSystems, now());
        foreach (var (systemId, essId, ownerId) in overrides)
            await Ess.RegisterAsync(systemId, essId, ownerId, now());
    }

    /// <summary>Claimable nullsec is known space with non-positive security, no faction marker, and outside the SDE's non-conquerable regions.</summary>
    private async Task<IReadOnlyList<(int SolarSystemId, long EssId)>> ReadClaimableEssDeploymentSystemsAsync()
    {
        var eligible = new List<(int SolarSystemId, long EssId)>();
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT key::bigint, (doc->>'regionID')::integer, (doc->>'securityStatus')::double precision, NULLIF(doc->>'factionID', '')::bigint " +
            "FROM sde.\"mapSolarSystems\" WHERE key::bigint BETWEEN $1 AND $2 ORDER BY ord", connection);
        command.Parameters.AddWithValue(KnownSpaceSolarSystemMinimum);
        command.Parameters.AddWithValue(KnownSpaceSolarSystemMaximum);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var systemId = checked((int)reader.GetInt64(0));
            var regionId = reader.GetInt32(1);
            var security = reader.GetDouble(2);
            var factionId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            if (systemId == ZarzakhSolarSystemId || NonClaimableEssRegionIds.Contains(regionId) || security > 0 || factionId is > 0)
                continue;
            eligible.Add((systemId, checked(GeneratedEssItemIdBase + systemId)));
        }
        return eligible;
    }

    /// <summary>The quadrant of each solar system, from the SDE's system names (the quadrant lists name systems, not ids).</summary>
    private async Task<Dictionary<int, string>> ReadEssQuadrantsAsync()
    {
        var regionNamesBySystemId = new Dictionary<int, string>();
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT systems.key::bigint, regions.doc->'name'->>'en' FROM sde.\"mapSolarSystems\" AS systems JOIN sde.\"mapRegions\" AS regions ON regions.key::bigint = (systems.doc->>'regionID')::bigint",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                regionNamesBySystemId[checked((int)reader.GetInt64(0))] = reader.GetString(1);
            }
        }
        return EssQuadrants.BySystemId(regionNamesBySystemId);
    }

    /// <summary>
    /// Takes one key of the type out of the current ship's cargo, in the bank's transaction: the candidate stack is found, then locked and read again inside the
    /// transaction, so that a key taken by another request makes this one refuse.
    /// </summary>
    private async ValueTask<bool> ConsumeEssKeyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, long keyTypeId, CancellationToken cancellationToken)
    {
        var candidate = (await items.OwnedByAsync(characterId, cancellationToken))
            .FirstOrDefault(item => item.TypeId == keyTypeId && item.LocationId == shipId && item.Flag == Eve.Ships.CharacterShips.CargoFlag && item.Quantity > 0);
        if (candidate is null || await items.LockAndGetAsync(connection, transaction, candidate.Id, cancellationToken) is not { } stack)
        {
            return false;
        }
        if (stack.OwnerId != characterId || stack.LocationId != shipId || stack.TypeId != keyTypeId ||
            stack.Flag != Eve.Ships.CharacterShips.CargoFlag || stack.Quantity <= 0)
        {
            return false;
        }
        if (stack.Quantity > 1)
        {
            await items.SetQuantityAsync(connection, transaction, stack.Id, stack.Quantity - 1, cancellationToken);
        }
        else
        {
            await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
        }
        return true;
    }

    /// <summary>The alliance that holds a system's sovereignty (the sovereignty table's system row), or null when no alliance holds it.</summary>
    private async ValueTask<long?> SovereigntyOwnerOfAsync(int systemId, CancellationToken cancellationToken)
    {
        var sovereignty = await ReadGameStoreTableAsync("sovereignty", cancellationToken);
        return sovereignty["systems"] is JsonObject systems
               && systems[systemId.ToString(CultureInfo.InvariantCulture)] is JsonObject row
               && long.TryParse(row["allianceID"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var alliance)
               && alliance > 0
            ? alliance
            : null;
    }

    private async ValueTask<long?> CharacterCorporationOfAsync(long characterId, CancellationToken cancellationToken)
    {
        var schema = Env("EVE_CHARACTERS_SCHEMA", "characters");
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new InvalidOperationException("EVE_CHARACTERS_SCHEMA is not a usable schema name");
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT corporation_id FROM {schema}.characters WHERE id = $1", connection)
        {
            Parameters = { new() { Value = characterId } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is long corporationId && corporationId > 0
            ? corporationId
            : null;
    }

    /// <summary>One system's ESS as the client reads it, or null when the system has none.</summary>
    private async ValueTask<EssMgrSystemData?> ReadEssSystemAsync(long systemId, CancellationToken cancellationToken) =>
        systemId is > 0 and <= int.MaxValue && await Ess.ReadAsync((int)systemId, cancellationToken) is { } state
            ? PgEssStore.ToClient(state)
            : null;

    /// <summary>Every ESS as the client reads it (the agency's list).</summary>
    private async ValueTask<IReadOnlyList<EssMgrSystemData>> ListEssSystemsAsync(CancellationToken cancellationToken) =>
        [.. (await Ess.ListAsync(cancellationToken)).Select(PgEssStore.ToClient)];

    private async ValueTask<Eve.Services.Core.DynamicBountyState?> ReadDynamicBountyAsync(long? solarSystemId, CancellationToken cancellationToken)
    {
        if (_dynamicBounty is not { } store || solarSystemId is not (> 0 and <= int.MaxValue))
            return null;
        var state = await store.ReadAsync((int)solarSystemId.Value, now(), cancellationToken);
        return new(state.Output, state.Active);
    }

    private async ValueTask<IReadOnlyList<DynamicBountyMapEntry>> ListDynamicBountyMapAsync(CancellationToken cancellationToken) =>
        _dynamicBounty is { } store ? await store.MapAsync(now(), cancellationToken) : [];

    /// <summary>The essMgr calls: the state, the requests, what the other pilots of a system are told, and the weapon timer a link starts.</summary>
    private Eve.Services.Space.EssMgrCallbacks EssCallbacks() => new(
        ReadSystemStateAsync: ReadEssSystemAsync,
        ExecuteAsync: async (request, cancellationToken) =>
        {
            var now = this.now();
            var result = await Ess.ExecuteAsync(request, now, cancellationToken);
            if (result.Succeeded && request.Operation == EssMgrOperation.AttemptLinkToMainBank)
            {
                // a link starts a weapon timer of its own, which does not refresh the log-off timer (CCP 19.10)
                await crimewatchStore.UpdateAsync(request.CharacterId,
                    state => state with { WeaponsUntilMs = Math.Max(state.WeaponsUntilMs, now + EssRules.WeaponsTimerMs) }, now, cancellationToken);
            }
            if (result.Succeeded && result.State is { } state)
            {
                foreach (var session in spaceSessions?.PilotSessionsIn((int)state.SolarSystemId) ?? [])
                {
                    if (!ReferenceEquals(session, request.Call.Session))
                    {
                        EssMgrService.NotifyEssDataChanged(session, state);
                    }
                }
            }
            return result;
        });

    /// <summary>The system's reserve quadrant as the cache manager lists its keys (null when the system is in no quadrant).</summary>
    private string? EssQuadrantOf(int systemId) => _essQuadrantBySystem.TryGetValue(systemId, out var quadrant) ? quadrant : null;

    /// <summary>Places the one ESS in a system before its next pilot enters, using its persisted identity and deterministic SDE anchor.</summary>
    private async Task PlaceEssBallAsync(ISimulation simulation, int solarSystemId, CancellationToken cancellationToken)
    {
        if (await Ess.PlacementAsync(solarSystemId, cancellationToken) is not { } state)
            return;

        var geometry = await EssGeometryAsync(cancellationToken);
        var anchors = await (_siteAnchors ??= new Eve.Services.Space.Sites.SdeSiteAnchors(sde))
            .OfAsync(solarSystemId, cancellationToken);
        var (_, position) = Eve.Services.Space.Sites.SitePlacement.Place(anchors, $"ess:{state.SolarSystemId}:{state.EssId}");
        var placed = await simulation.PlaceStructureAsync(new SimPlaceStructure(
            state.EssId,
            state.SolarSystemId,
            "Encounter Surveillance System",
            EssRules.TypeEncounterSurveillanceSystem,
            geometry.GroupId,
            position,
            geometry.Radius,
            state.OwnerId ?? 0,
            0,
            0,
            new SimStructureHealth(geometry.ShieldCapacity, geometry.ArmorHp, geometry.StructureHp,
                [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0, [0, 0, 0], [0, 0, 0]),
            new SimStructureStatus(StructureState.ShieldVulnerable, UpkeepState.FullPower, null, null, null)), cancellationToken);
        if (!placed)
            throw new SimUnavailableException($"the simulation disconnected while placing ESS {state.EssId}");
    }

    private async Task<EssGeometry> EssGeometryAsync(CancellationToken cancellationToken)
    {
        if (_essGeometry is { } cached)
            return cached;
        await _essGeometryGate.WaitAsync(cancellationToken);
        try
        {
            if (_essGeometry is { } loaded)
                return loaded;
            await dogma.EnsureTypesAsync([EssRules.TypeEncounterSurveillanceSystem], cancellationToken);
            var type = dogma.Type(EssRules.TypeEncounterSurveillanceSystem)
                ?? throw new InvalidDataException($"The SDE has no dogma type for ESS {EssRules.TypeEncounterSurveillanceSystem}.");
            var staticType = (await sde.ReadAsync("types", [(long)EssRules.TypeEncounterSurveillanceSystem], cancellationToken)).SingleOrDefault()
                ?? throw new InvalidDataException($"The SDE has no record for ESS type {EssRules.TypeEncounterSurveillanceSystem}.");
            var radius = staticType.Doc.TryGetProperty("radius", out var radiusValue) && radiusValue.TryGetDouble(out var staticRadius)
                ? staticRadius
                : 0d;
            if (!double.IsFinite(radius) || radius < 0)
                throw new InvalidDataException($"The SDE has an unusable radius for ESS type {EssRules.TypeEncounterSurveillanceSystem}.");
            return _essGeometry = new EssGeometry(type.GroupId, radius,
                type.Attributes.GetValueOrDefault(263, 0d),
                type.Attributes.GetValueOrDefault(265, 0d),
                type.Attributes.GetValueOrDefault(9, 0d));
        }
        finally
        {
            _essGeometryGate.Release();
        }
    }

    private async Task TickEssAsync(CancellationToken cancellationToken)
    {
        var events = await Ess.TickAsync(now(), cancellationToken);
        foreach (var systemId in events.Select(e => e.SolarSystemId).Distinct())
        {
            var sessions = spaceSessions?.PilotSessionsIn(systemId) ?? [];
            foreach (var change in events.Where(e => e.SolarSystemId == systemId && e.Kind != EssEventKind.Changed))
            {
                foreach (var session in sessions)
                {
                    if (change.Kind == EssEventKind.Theft)
                    {
                        session.SendNotification("OnMainBankPlayerHackSuccess", "clientID", Py.Dict(
                            ("solarSystemID", Py.Number(systemId)),
                            ("linkID", new PyStr(change.LinkId)),
                            ("characterID", Py.Number(change.CharacterId))));
                    }
                    else
                    {
                        session.SendNotification("OnMainBankPlayerLinkDisconnected", "clientID", Py.Dict(
                            ("solarSystemID", Py.Number(systemId)),
                            ("linkID", new PyStr(change.LinkId)),
                            ("characterID", Py.Number(change.CharacterId)),
                            ("reason", new PyInt(change.Reason))));
                    }
                }
            }
            if (await Ess.ReadAsync(systemId, cancellationToken) is { } state)
            {
                var client = PgEssStore.ToClient(state);
                foreach (var session in sessions)
                {
                    EssMgrService.NotifyEssDataChanged(session, client);
                }
            }
        }
    }
}
