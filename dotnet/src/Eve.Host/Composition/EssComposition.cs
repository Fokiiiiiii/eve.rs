using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Wallet;
using Npgsql;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

/// <summary>
/// The Encounter Surveillance Systems: their store, the calls of the client's essMgr and dynamic resource cache, the clock that works them, and the systems the
/// operator places (<c>EVE_ESS_SYSTEMS</c>: <c>solarSystemId:essId[:ownerId]</c>, comma separated). With no entries there is no ESS in any system.
/// </summary>
internal sealed partial class CharacterServicesComposition
{
    private string essSchema = default!;
    private PgEssStore? _ess;

    private PgEssStore Ess => _ess ??= new PgEssStore(
        source, new WalletTransactions(source, walletSchema), essSchema,
        message => Console.Error.WriteLine($"eve-host: ess: {message}"),
        linkGeometry: null);

    /// <summary>
    /// The ESS placement that the operator asks for. Each system's ESS is registered at start; a malformed entry stops the host, so that it does not run with
    /// an ESS it was not given.
    /// </summary>
    private async Task RegisterEssSystemsAsync()
    {
        var entries = Env("EVE_ESS_SYSTEMS", "");
        foreach (var entry in entries.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length is < 2 or > 3 || !int.TryParse(parts[0], out var systemId) || systemId <= 0 ||
                !long.TryParse(parts[1], out var essId) || essId <= 0 ||
                (parts.Length == 3 && (!long.TryParse(parts[2], out var ownerId) || ownerId <= 0)))
            {
                throw new InvalidOperationException($"EVE_ESS_SYSTEMS: '{entry}' is not solarSystemId:essId[:ownerId]");
            }
            var owner = parts.Length == 3 ? long.Parse(parts[2]) : (long?)null;
            await Ess.RegisterAsync(systemId, essId, owner, now());
        }
    }

    /// <summary>One system's ESS as the client reads it, or null when the system has none.</summary>
    private async ValueTask<EssMgrSystemData?> ReadEssSystemAsync(long systemId, CancellationToken cancellationToken) =>
        systemId is > 0 and <= int.MaxValue && await Ess.ReadAsync((int)systemId, cancellationToken) is { } state
            ? PgEssStore.ToClient(state)
            : null;

    /// <summary>Every ESS as the client reads it (the agency's list).</summary>
    private async ValueTask<IReadOnlyList<EssMgrSystemData>> ListEssSystemsAsync(CancellationToken cancellationToken) =>
        [.. (await Ess.ListAsync(cancellationToken)).Select(PgEssStore.ToClient)];

    /// <summary>The essMgr calls: the state, the requests, and what the other pilots of a system are told when a request changes it.</summary>
    private Eve.Services.Space.EssMgrCallbacks EssCallbacks() => new(
        ReadSystemStateAsync: ReadEssSystemAsync,
        ExecuteAsync: async (request, cancellationToken) =>
        {
            var result = await Ess.ExecuteAsync(request, now(), cancellationToken);
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

    /// <summary>The clock of the ESS (<c>space.ess.tick</c>): thefts, link ends, owner payouts and reserve pulses, told to the pilots of their systems.</summary>
    private async Task TickEssAsync(CancellationToken cancellationToken)
    {
        var events = await Ess.TickAsync(now(), (systemId, characterId) => spaceSessions?.IsPilotIn(characterId, systemId) ?? false, cancellationToken);
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
