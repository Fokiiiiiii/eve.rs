using Eve.GameStore;
using Eve.Ships;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The mining ledger (M7): a granted cycle's fact becomes one entry of the character's row, in the subscriber's transaction.</summary>
public sealed class MiningLedgerTests
{
    private static OreMinedEvent Fact(long characterId, long quantity) => new(
        characterId, 98000001, 30000142, 9000001, 11019, 9000002, 3651, MiningSource.Module, 1230, "ore", quantity, 2, 0);

    private static async Task PublishAsync(Rig rig, OreMinedEvent fact, bool commit)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Events.PublishAsync(connection, transaction, OreMinedEvent.Topic, fact, $"char:{fact.CharacterId}");
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }
        rig.Events.Wake();
    }

    [PgFact]
    public async Task A_granted_cycle_is_one_entry_of_the_character_and_a_replay_writes_nothing_more()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = $"ml_{Guid.NewGuid():N}"[..14];
        try
        {
            var store = new PersistenceStore(rig.Source, schema);
            await store.EnsureSchemaAsync();
            var recorder = new MiningLedgerRecorder(store, await store.AcquireOwnerAsync(PersistenceRoles.World, "ledger-test"));
            rig.Events.Subscribe("mining.ledger", [OreMinedEvent.Topic], async (context, token) =>
            {
                // the same event handled twice (a replay): the entry is there once
                await recorder.RecordAsync(context, token);
                await recorder.RecordAsync(context, token);
            });
            await rig.Events.PumpAsync();

            await PublishAsync(rig, Fact(1001, 100), commit: true);
            await PublishAsync(rig, Fact(1002, 40), commit: false);   // a grant that rolled back announces nothing
            await rig.Events.PumpAsync();

            var table = await store.LoadTableAsync(MiningLedgerRecorder.Table);
            var characters = table["characters"]!.AsObject();
            Assert.Equal(new[] { "1001" }, characters.Select(pair => pair.Key));
            var bucket = characters["1001"]!.AsObject();
            var entries = bucket["entries"]!.AsArray();
            var entry = Assert.Single(entries)!.AsObject();
            Assert.Equal(
                (1001L, 98000001L, 30000142L, 3651L, 100L, 2L, 0L, "ore", 11019L, 0L),
                (entry["characterID"]!.GetValue<long>(), entry["corporationID"]!.GetValue<long>(), entry["solarSystemID"]!.GetValue<long>(),
                    entry["moduleTypeID"]!.GetValue<long>(), entry["quantity"]!.GetValue<long>(), entry["quantityWasted"]!.GetValue<long>(),
                    entry["quantityCritical"]!.GetValue<long>(), entry["yieldKind"]!.GetValue<string>(), entry["shipTypeID"]!.GetValue<long>(),
                    entry["observerItemID"]!.GetValue<long>()));
            // the personal entry is shown ten minutes after the cycle (miningCharacterLedgerDelayMs)
            var eventDate = long.Parse(entry["eventDate"]!.GetValue<string>());
            Assert.Equal(eventDate + MiningLedgerRecorder.CharacterDelayMs * 10_000, long.Parse(entry["characterVisibleAfter"]!.GetValue<string>()));
        }
        finally
        {
            await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
