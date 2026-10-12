using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Services;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class StructureControlPgFactAttribute : FactAttribute
{
    public StructureControlPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

public sealed class StructureControlReadModelTests
{
    [StructureControlPgFact]
    public async Task Current_pilot_reads_authoritative_structure_items_and_online_state_without_replacing_the_personal_ship()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var prefix = "control_" + Guid.NewGuid().ToString("N")[..12];
        var schemas = new[] { prefix + "c", prefix + "i", prefix + "s", prefix + "f", prefix + "r" };
        try
        {
            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(source, schemas[3]);
            await StructureStore.MigrateAsync(source, schemas[4]);
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var statics = await DogmaStatic.LoadAsync(sde);
            var characters = new PgCharacterStore(source, schemas[0]);
            var items = new PgItemStore(source, schemas[1]);
            var skills = new PgSkillStore(source, schemas[2]);
            var modules = new PgModuleStateStore(source, schemas[3]);
            var structures = new StructureStore(source, schemas[4]);
            var pilots = new StructurePilotStore(structures);
            var ships = new CharacterShips(source, statics, characters, items, skills, modules);
            const long structureId = 8_300_001, corporation = 1_000_001, station = 60_015_249;
            const int system = 30_000_142, astrahus = 35_832, standupNeutralizer = 35_925;
            long character, personalId, onlineId, offlineId, foreignId;
            StructurePilotRecord pilot;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                character = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, "Structure Reader", 1, 1, 1, 1, 1, 1, corporation, null, station, system, 20_000_020, 10_000_002, null, null));
                personalId = await items.CreateAsync(connection, transaction, new NewItem(11_019, character, station, ItemFlags.Hangar, 1, true));
                await characters.SetShipAsync(connection, transaction, character, personalId);
                Assert.True(await structures.CreateAsync(connection, transaction,
                    new NewStructure(structureId, astrahus, system, 1, 2, 3, 0, corporation, 0, "Controlled Astrahus", "")));
                onlineId = await items.CreateAsync(connection, transaction, new NewItem(standupNeutralizer, corporation, structureId, 27, 1, true));
                offlineId = await items.CreateAsync(connection, transaction, new NewItem(standupNeutralizer, corporation, structureId, 28, 1, true));
                foreignId = await items.CreateAsync(connection, transaction, new NewItem(standupNeutralizer, character, structureId, 29, 1, true));
                await modules.SetAsync(connection, transaction, onlineId, ItemState.Online);
                var acquired = await pilots.AcquireAsync(connection, transaction,
                    new StructurePilotRequest(structureId, character, 33, personalId, station, false), false);
                Assert.Equal(StructurePilotAcquireStatus.Acquired, acquired.Status);
                pilot = Assert.IsType<StructurePilotRecord>(acquired.Current);
                await transaction.CommitAsync();
            }
            var personal = Assert.IsType<ShipSnapshot>(await ships.LoadAsync(character));
            var allowed = true;
            var reader = new StructureControlReadModel(source, structures, pilots, items, modules, statics,
                (_, _, _, _) => ValueTask.FromResult(allowed));
            var session = Session(character, 33, structureId);
            var inventory = Assert.IsType<Eve.Services.Inventory.ControlledStructureInventory>(await reader.InventoryAsync(session, structureId, default));
            Assert.Equal((structureId, astrahus, corporation, (long)system),
                (inventory.Structure.Id, inventory.Structure.TypeId, inventory.Structure.OwnerId, inventory.Structure.LocationId));
            Assert.Equal(new[] { onlineId, offlineId }, inventory.Contents.Select(item => item.Id));
            Assert.DoesNotContain(inventory.Contents, item => item.Id == foreignId);
            var dogma = Assert.IsType<ShipSnapshot>(await reader.DogmaAsync(session, personal, default));
            Assert.Equal(structureId, dogma.Ship.Id);
            Assert.Equal(structureId, dogma.World.StructureId);
            Assert.Equal(ItemState.Online, dogma.StateOf(onlineId));
            Assert.Equal(ItemState.Offline, dogma.StateOf(offlineId));
            var mass = statics.AttributeId("mass")!.Value;
            Assert.Equal(statics.Type(astrahus)!.Attributes[mass], dogma.World.Get(structureId, mass));
            Assert.Equal(personalId, (await characters.GetAsync(character))!.ShipId);
            Assert.Null(await items.GetAsync(structureId));
            allowed = false;
            Assert.Null(await reader.InventoryAsync(session, structureId, default));
            Assert.Null(await reader.DogmaAsync(session, personal, default));
            allowed = true;
            Assert.Null(await reader.InventoryAsync(Session(character, 34, structureId), structureId, default));
            Assert.Null(await reader.InventoryAsync(Session(character + 1, 33, structureId), structureId, default));
            session.Set("shipid", new PyLong(personalId));
            Assert.Null(await reader.DogmaAsync(session, personal, default));
            session.Set("shipid", new PyLong(structureId));
            await using (var connection = await source.OpenConnectionAsync())
                Assert.True(await pilots.ReleaseAsync(connection, null, pilot));
            Assert.Null(await reader.InventoryAsync(session, structureId, default));
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static ClientSession Session(long character, long sessionId, long structureId)
    {
        var session = new ClientSession { CharacterId = character, SessionId = sessionId };
        session.Set("shipid", new PyLong(structureId));
        session.Set("structureid", new PyLong(structureId));
        return session;
    }
}
