using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Relations;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class TetherAuthorityTests
{
    private const long Owner = 98_000_001, CharacterId = 20, Structure = 1_040_000_000_001;
    private const int SystemId = 30000142;

    [Fact]
    public void Core_state_and_upkeep_must_all_allow_tethering()
    {
        var live = new StructureSnapshot { StructureId = Structure, State = StructureState.ShieldVulnerable, CoreInstalled = true, Upkeep = UpkeepState.FullPower };
        Assert.True(TetherAuthority.Eligible(live));
        Assert.True(TetherAuthority.Eligible(live with { Upkeep = UpkeepState.LowPower }));
        Assert.False(TetherAuthority.Eligible(live with { CoreInstalled = false }));
        Assert.False(TetherAuthority.Eligible(live with { Upkeep = UpkeepState.Abandoned }));
        Assert.False(TetherAuthority.Eligible(live with { State = StructureState.Anchoring }));
        Assert.False(TetherAuthority.Eligible(live with { State = StructureState.FittingInvulnerable }));
        Assert.False(TetherAuthority.Eligible(live with { DestroyedMs = 1 }));
    }

    [Theory]
    [InlineData(500001, 500004, true)]
    [InlineData(500003, 500002, true)]
    [InlineData(500001, 500003, false)]
    [InlineData(500002, 500004, false)]
    [InlineData(0, 500004, false)]
    [InlineData(500001, 500002, false)]
    [InlineData(500003, 500004, false)]
    [InlineData(500010, 500004, true)]
    [InlineData(500011, 500003, true)]
    public void Enemy_fw_space_uses_occupation_and_insurgency_relations(long faction, long occupier, bool blocked) =>
        Assert.Equal(blocked, TetherAuthority.FwEnemy(faction, occupier));

    [PgFact]
    public async Task Candidates_use_persisted_state_sde_geometry_actual_docking_acl_and_fw_occupation()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = $"tether_s_{Guid.NewGuid():N}";
        await StructureStore.MigrateAsync(source, schema);
        try
        {
            var store = new StructureStore(source, schema);
            var sde = new SdeReader(source);
            var dogma = await DogmaStatic.LoadAsync(sde);
            long corporation = Owner;
            long? ownerFaction = null;
            JsonObject sovereignty = new();
            async Task<JsonObject> Read(string table, CancellationToken token) =>
                table == "structures" ? await store.ReadLegacyTableAsync(token)
                : table == "sovereignty" ? sovereignty : new JsonObject();
            var directory = new PersistenceStructureDirectoryReadModel(Read,
                (_, _) => Task.FromResult(new Affiliation(corporation, null, null)), sde, null);
            async Task<bool> Dock(long character, long structure, CancellationToken token)
            {
                var access = await directory.GetDockingAccessSnapshotsAsync(character, corporation, 0, [structure], token);
                return access.SingleOrDefault() is { } row && StructureDirectoryService.CanDock(row, structure, character, corporation, 0);
            }
            var authority = new TetherAuthority(source, store, dogma, directory, Dock, _ => 587, Read,
                (_, _) => Task.FromResult(ownerFaction), null!, null!, null!, null!, (_, _) => false);
            var session = new ClientSession();
            session.Set("charid", new PyInt(CharacterId));
            session.Set("solarsystemid2", new PyInt(SystemId));

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.CreateAsync(connection, transaction, new NewStructure(Structure, 35832, SystemId, 0, 0, 0, 0, Owner, 0, "Tether", ""));
                await store.SaveAsync(connection, transaction, new StructureSnapshot { StructureId = Structure, State = StructureState.ShieldVulnerable, CoreInstalled = true, Upkeep = UpkeepState.FullPower });
                await store.SetServiceAsync(connection, transaction, Structure, StructureServiceIds.Docking, ServiceState.Online, 0);
                await store.CreateAsync(connection, transaction, new NewStructure(Structure + 1, 35832, SystemId + 1, 0, 0, 0, 0, Owner, 0, "Other system", ""));
                await store.SaveAsync(connection, transaction, new StructureSnapshot { StructureId = Structure + 1, State = StructureState.ShieldVulnerable, CoreInstalled = true, Upkeep = UpkeepState.FullPower });
                await store.SetServiceAsync(connection, transaction, Structure + 1, StructureServiceIds.Docking, ServiceState.Online, 0);
                await transaction.CommitAsync();
            }
            var candidate = Assert.Single(await authority.ReadTetherCandidatesAsync(session, CancellationToken.None));
            Assert.Equal((Structure, 10_000d), (candidate.StructureId, candidate.Range));
            var geometry = await authority.ReadTetherGeometryAsync(Structure, CancellationToken.None);
            Assert.Equal((45_000d, 10_000d), (geometry!.Radius, geometry.Range));

            corporation = Owner + 1;
            Assert.Empty(await authority.ReadTetherCandidatesAsync(session, CancellationToken.None));
            corporation = Owner;
            sovereignty = new JsonObject { ["systems"] = new JsonObject {
                [SystemId.ToString()] = new JsonObject { ["warzoneID"] = 1, ["factionID"] = 500004 } } };
            session.Set("warfactionid", new PyInt(500001));
            Assert.Empty(await authority.ReadTetherCandidatesAsync(session, CancellationToken.None));
            session.Set("warfactionid", PyNone.Instance);
            ownerFaction = 500001;
            Assert.Empty(await authority.ReadTetherCandidatesAsync(session, CancellationToken.None));
            ownerFaction = null;
            Assert.Single(await authority.ReadTetherCandidatesAsync(session, CancellationToken.None));

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var record = (await store.GetAsync(Structure))!;
                await store.SaveAsync(connection, transaction, record.Snapshot with { CoreInstalled = false });
                await transaction.CommitAsync();
            }
            Assert.Empty(await authority.ReadTetherCandidatesAsync(session, CancellationToken.None));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Free_repairs_persist_fitted_and_drone_bay_condition_keep_burned_modules_offline_and_leave_cargo_alone()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N");
        string[] schemas = [$"tether_c_{suffix}", $"tether_i_{suffix}", $"tether_k_{suffix}", $"tether_f_{suffix}"];
        await PgCharacterStore.MigrateAsync(source, schemas[0]);
        await PgItemStore.MigrateAsync(source, schemas[1]);
        await Eve.Skills.PgSkillStore.MigrateAsync(source, schemas[2]);
        await PgModuleStateStore.MigrateAsync(source, schemas[3]);
        try
        {
            var dogma = await DogmaStatic.LoadAsync(new SdeReader(source));
            var characters = new PgCharacterStore(source, schemas[0]);
            var items = new PgItemStore(source, schemas[1]);
            var skills = new Eve.Skills.PgSkillStore(source, schemas[2]);
            var conditions = new PgModuleStateStore(source, schemas[3]);
            var ships = new CharacterShips(source, dogma, characters, items, skills, conditions);
            long character, ship, fitted, cargo, drone;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                character = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, "Tether Pilot " + suffix[..12], 0, 1, 1, 1, 1, 1373, Owner, null, 0, SystemId, 20000020, 10000002, null, null));
                ship = await items.CreateAsync(connection, transaction, new NewItem(587, character, SystemId, CharacterShips.InSpaceFlag, 1, true));
                fitted = await items.CreateAsync(connection, transaction, new NewItem(439, character, ship, ItemFlags.MedSlot0, 1, true));
                cargo = await items.CreateAsync(connection, transaction, new NewItem(439, character, ship, ItemFlags.Cargo, 1, true));
                drone = await items.CreateAsync(connection, transaction, new NewItem(2454, character, ship, CharacterShips.DroneBayFlag, 1, true));
                await characters.SetShipAsync(connection, transaction, character, ship);
                await transaction.CommitAsync();
            }
            var burnt = new ModuleCondition(1, 0.5, 0, true);
            await conditions.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [fitted] = burnt, [cargo] = burnt, [drone] = burnt });
            await conditions.SetShipConditionAsync(drone, new ShipCondition(0.5, 0.2, 0.3, 0.5, true));
            var session = new ClientSession();
            session.Set("charid", new PyLong(character));
            session.Set("shipid", new PyLong(ship));
            var authority = new TetherAuthority(source, null!, dogma, null!, null!, null!, null!, null!,
                ships, items, conditions, null!, (_, _) => true);
            Assert.False(await authority.RepairTetheredItemsAsync(session, ship + 1, CancellationToken.None));
            Assert.Equal(burnt, (await conditions.ModuleConditionsAsync([fitted]))[fitted]);
            Assert.True(await authority.RepairTetheredItemsAsync(session, ship, CancellationToken.None));

            var restarted = new PgModuleStateStore(source, schemas[3]);
            var after = await restarted.ModuleConditionsAsync([fitted, cargo, drone]);
            Assert.Equal(ModuleCondition.Mended, after[fitted]);
            Assert.Equal(ModuleCondition.Mended, after[drone]);
            Assert.Equal(burnt, after[cargo]);
            Assert.Equal(ShipCondition.Full, await restarted.ShipConditionAsync(drone));
            Assert.Equal(ItemState.Offline, (await restarted.StatesAsync([fitted]))[fitted]);
            Assert.False(await authority.RepairTetheredItemsAsync(session, ship, CancellationToken.None));
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
