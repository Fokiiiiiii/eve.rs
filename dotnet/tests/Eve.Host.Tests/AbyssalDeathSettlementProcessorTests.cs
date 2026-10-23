using System.Collections.Concurrent;
using System.Reflection;
using Eve.Characters;
using Eve.Content;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalDeathSettlementProcessorTests
{
    [PgFact]
    public async Task Run_expiry_retry_reloads_clone_rows_and_cleans_up_when_the_host_has_no_resident()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N");
        string[] schemas = [$"abyssal_dc_{suffix}", $"abyssal_di_{suffix}", $"abyssal_ds_{suffix}", $"abyssal_df_{suffix}"];

        try
        {
            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(source, schemas[3]);

            var sde = new SdeReader(source);
            var statics = await DogmaStatic.LoadAsync(sde);
            var items = new PgItemStore(source, schemas[1]);
            var characters = new PgCharacterStore(source, schemas[0]);
            var skills = new PgSkillStore(source, schemas[2]);
            var modules = new PgModuleStateStore(source, schemas[3]);
            var ships = new CharacterShips(source, statics, characters, items, skills, modules);
            var itemTypes = new ItemTypeCatalog(sde);
            var creation = await CreationCatalog.LoadAsync(sde, (long?)null);
            var stations = await StationCatalog.LoadAsync(sde);
            var abyssalCatalog = await AbyssalStaticCatalog.LoadAsync(sde);
            var spaceSystemId = abyssalCatalog.AbyssalSystemsById.Keys.Order().First();
            var systemRow = (await sde.ReadAsync("mapSolarSystems", [(long)spaceSystemId])).Single();
            var constellationId = systemRow.Doc.GetProperty("constellationID").GetInt32();
            var regionId = systemRow.Doc.GetProperty("regionID").GetInt32();

            var race = creation.Races.OrderBy(value => value.Id).First();
            var bloodline = creation.Bloodlines.First(value => value.RaceId == race.Id);
            var school = CreationCatalog.Schools.First(value => value.RaceId == race.Id);
            var starter = creation.StarterFor(school.CorporationId)
                ?? throw new InvalidDataException($"No starter location for corporation {school.CorporationId}.");
            var home = stations.Get(starter.StationId)
                ?? throw new InvalidDataException($"Starter station {starter.StationId} is absent from the SDE.");

            long characterId;
            long shipId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(
                        1,
                        "Abyssal death " + suffix[..10],
                        0,
                        race.Id,
                        bloodline.Id,
                        1,
                        school.Id,
                        CreationCatalog.CharacterTypeId(bloodline.Id),
                        school.CorporationId,
                        null,
                        home.Id,
                        spaceSystemId,
                        constellationId,
                        regionId,
                        null,
                        null));
                shipId = await items.CreateAsync(connection, transaction,
                    new NewItem(race.ShipTypeId, characterId, spaceSystemId, CharacterShips.InSpaceFlag, 1, Singleton: true));
                await characters.SetShipAsync(connection, transaction, characterId, shipId);
                await transaction.CommitAsync();
            }

            var contentId = Guid.NewGuid().ToString("D");
            var request = new AbyssalParticipantDeathSettlementRequestedEvent(
                75,
                contentId,
                $"abyssal:{contentId}:death:{characterId}",
                characterId,
                shipId,
                AbyssalDeathSettlementCause.RunExpired,
                new AbyssalParticipantOrigin(
                    starter.SolarSystemId,
                    starter.StationId,
                    new AbyssalPosition(0, 0, 0),
                    new AbyssalPosition(0, 0, 1),
                    1),
                spaceSystemId,
                2,
                1,
                null);

            var processor = new AbyssalDeathSettlementProcessor(
                ships, characters, items, itemTypes, creation, stations);
            var first = await processor.SettleAsync(request, wreckTypeId: null);

            // A process restart loses local state. The retry must reconstruct the completed clone from its persisted rows.
            var restartedProcessor = new AbyssalDeathSettlementProcessor(
                ships, characters, items, itemTypes, creation, stations);
            var second = await restartedProcessor.SettleAsync(request, wreckTypeId: null);
            var persistedCharacter = await characters.GetAsync(second.CharacterId)
                ?? throw new InvalidDataException("The completed clone character was not persisted.");
            var persistedShip = await items.GetAsync(second.ReplacementShipId)
                ?? throw new InvalidDataException("The completed clone ship was not persisted.");
            var systems = await SolarSystemCatalog.LoadAsync(sde);
            var simulation = DispatchProxy.Create<ISimulation, AbyssalRemovalSim>();
            var sim = (AbyssalRemovalSim)(object)simulation;
            using (var space = new SpaceSessions(
                       simulation, ships, statics, stations, systems, () => 1_700_000_000_000))
            {
                space.RookieShipOfRace = raceId => creation.Race(raceId)?.ShipTypeId;
                var cleanup = await space.ResumeAbyssalParticipantSettlementAsync(
                    request, persistedCharacter, persistedShip);

                Assert.True(cleanup.Succeeded, cleanup.Error);
                Assert.False(cleanup.ResidentRemoved);
                Assert.True(cleanup.AlreadyAbsent);
                Assert.False(cleanup.SessionUpdated);
            }
            var removal = Assert.Single(sim.RemoveRequests);
            Assert.Equal(new SimSceneKey(spaceSystemId, 75, 2), removal.Scene);
            Assert.Equal(shipId, removal.ShipId);

            Assert.Equal(first.CharacterId, second.CharacterId);
            Assert.Equal(first.ReplacementShipId, second.ReplacementShipId);
            Assert.NotNull(first.ShipDeath);
            Assert.NotNull(first.PodDeath);
            Assert.False(first.AuthorityAlreadyCompleted);
            Assert.Null(second.ShipDeath);
            Assert.Null(second.PodDeath);
            Assert.True(second.AuthorityAlreadyCompleted);
            Assert.Equal(home.Id, first.StationId);
            Assert.Equal(starter.SolarSystemId, first.SolarSystemId);
            Assert.Null(await items.GetAsync(shipId));

            var character = await characters.GetAsync(characterId);
            Assert.Equal(first.ReplacementShipId, character!.ShipId);
            Assert.Equal(home.Id, character.StationId);
            var replacement = await items.GetAsync(first.ReplacementShipId);
            Assert.Equal((race.ShipTypeId, characterId, home.Id, ItemFlags.Hangar),
                (replacement!.TypeId, replacement.OwnerId, replacement.LocationId, replacement.Flag));
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
    public class AbyssalRemovalSim : DispatchProxy
    {
        public ConcurrentQueue<SimRemoveAbyssalParticipant> RemoveRequests { get; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name == "get_IsConnected") return true;
            if (method.Name is "add_Received" or "remove_Received" or "add_Lost" or "remove_Lost")
                return null;
            if (method.Name == "RemoveAbyssalParticipantAsync")
            {
                var request = (SimRemoveAbyssalParticipant)(args?[0]
                    ?? throw new InvalidOperationException("The participant removal request is missing."));
                RemoveRequests.Enqueue(request);
                return Task.FromResult(new SimRemoveAbyssalParticipantReply(request.RequestId, true, null));
            }
            throw new NotSupportedException($"Unexpected simulation call in this recovery test: {method.Name}");
        }
    }
}
