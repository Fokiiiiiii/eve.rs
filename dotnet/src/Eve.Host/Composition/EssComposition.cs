using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Inventory;
using Eve.Services.Space;
using Eve.Space;
using Eve.Structures;
using Eve.Wallet;
using Npgsql;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

/// <summary>
/// The Encounter Surveillance Systems: their store, the calls of the client's essMgr and dynamic resource cache, the ESS ball each one has in space, the clock

/// is no ESS in any system.
/// </summary>
internal sealed partial class CharacterServicesComposition
{
    private string essSchema = default!;
    private PgEssStore? _ess;
    private Eve.Services.Space.EssMgrService? _essMgrService;
    private Eve.Services.Space.EssMgrService EssMgr => _essMgrService ??= new(EssCallbacks());
    private Dictionary<int, string> _essQuadrantBySystem = [];

    private PgEssStore Ess => _ess ??= new PgEssStore(
        source, new WalletTransactions(source, walletSchema), essSchema,
        message => Console.Error.WriteLine($"eve-host: ess: {message}"),
        holderOf: async (systemId, characterId, _, cancellationToken) =>
            spaceSessions is { } space ? await space.EssHolderAsync(systemId, characterId, cancellationToken) : null,
        consumeKey: ConsumeEssKeyAsync,
        quadrantOf: systemId => _essQuadrantBySystem.TryGetValue(systemId, out var quadrant) ? quadrant : null,
        sovereigntyOwnerOf: SovereigntyOwnerOfAsync,
        corporationOf: CharacterCorporationOfAsync);

    /// <summary>
    /// The ESS placement that the operator asks for. Each system's ESS is registered at start; a malformed entry stops the host, so that it does not run with
    /// an ESS it was not given.
    /// </summary>
    private async Task RegisterEssSystemsAsync()
    {
        var entries = Env("EVE_ESS_SYSTEMS", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            return;   // no ESS: the SDE's system names are not needed
        }
        _essQuadrantBySystem = await ReadEssQuadrantsAsync();
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
            await Ess.RegisterAsync(systemId, essId, owner, now());
        }
    }

    /// <summary>The quadrant of each solar system, from the SDE's system names (the quadrant lists name systems, not ids).</summary>
    private async Task<Dictionary<int, string>> ReadEssQuadrantsAsync()
    {
        var names = new Dictionary<int, string>();
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT key::bigint, doc->'name'->>'en' FROM sde.\"mapSolarSystems\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                names[checked((int)reader.GetInt64(0))] = reader.GetString(1);
            }
        }
        return EssQuadrants.BySystemId(names);
    }

    /// <summary>
    /// Takes one key of the type out of the character's hangar, in the bank's transaction: the candidate stack is found, then locked and read again inside the
    /// transaction, so that a key taken by another request makes this one refuse (the key comes from the hangar only).
    /// </summary>
    private async ValueTask<bool> ConsumeEssKeyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long keyTypeId, CancellationToken cancellationToken)
    {
        var candidate = (await items.OwnedByAsync(characterId, cancellationToken))
            .FirstOrDefault(item => item.TypeId == keyTypeId && item.Flag == ItemFlags.Hangar && item.Quantity > 0);
        if (candidate is null || await items.LockAndGetAsync(connection, transaction, candidate.Id, cancellationToken) is not { } stack)
        {
            return false;
        }
        if (stack.OwnerId != characterId || stack.TypeId != keyTypeId || stack.Flag != ItemFlags.Hangar || stack.Quantity <= 0)
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

    /// <summary>
    /// The ESS balls, one per ESS: a structure-kind ball at the system's origin (<see cref="EssRules.BallX"/>, the rule), with the type's hit points and radius,
    /// put in space when the host starts so that a pilot in its system can link to it.
    /// </summary>
    private async Task PlaceEssBallsAsync(ISimulation simulation)
    {
        var type = dogma.Type(EssRules.TypeEncounterSurveillanceSystem);
        double Attribute(int id) => type?.Attributes.GetValueOrDefault(id, 0d) ?? 0d;
        foreach (var state in await Ess.ListAsync())
        {
            simulation.PlaceStructure(new SimPlaceStructure(
                state.EssId,
                state.SolarSystemId,
                "Encounter Surveillance System",
                EssRules.TypeEncounterSurveillanceSystem,
                type?.GroupId ?? 0,
                [EssRules.BallX, EssRules.BallY, EssRules.BallZ],
                Attribute(162),
                state.OwnerId ?? 0,
                0,
                0,
                new SimStructureHealth(Attribute(263), Attribute(265), Attribute(9), [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0, [0, 0, 0], [0, 0, 0]),
                new SimStructureStatus(StructureState.ShieldVulnerable, UpkeepState.FullPower, null, null, null)));
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
