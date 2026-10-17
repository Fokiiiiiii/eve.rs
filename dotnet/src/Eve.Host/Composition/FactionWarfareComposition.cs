using Eve.FactionWarfare;
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
