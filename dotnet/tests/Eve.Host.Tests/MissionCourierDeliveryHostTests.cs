using Eve.Agents;
using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Sde;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class MissionCourierDeliveryHostTests
{
    private const int ShuttleTypeId = 587;

    [PgFact]
    public async Task Registered_docking_completes_couriers_only_from_the_destination_hangar_or_docked_ship()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync(simSocket: "tcp:127.0.0.1:1");
        var characterId = await rig.CreateCharacterAsync();
        await rig.FundAsync(characterId, 0m);
        var sde = new SdeReader(rig.Source);
        var stations = await StationCatalog.LoadAsync(sde);
        var systems = await SolarSystemCatalog.LoadAsync(sde);
        var gates = await StargateGraph.LoadAsync(sde);
        var staticMissions = await MissionStaticCatalog.LoadAsync(new SdeReader(rig.Source, MissionStaticCatalog.DefaultSchema));
        var templates = ClientMissionTemplates.Parse(ClientMissionMessages.Open(null));
        var agentRows = await sde.ReadAsync("npcCharacters");
        var agentsInSpace = await sde.ReadAsync("agentsInSpace");
        var agents = AgentCatalog.Build(agentRows, agentsInSpace,
                stationId => stations.Get(stationId) is { } station
                    ? new AgentStationPlace(station.TypeId, station.SolarSystemId)
                    : null,
                systemId => systemId is > 0 and <= int.MaxValue && systems.Get((int)systemId) is not null)
            .Where(agent => agent.AgentTypeId == AgentMissionKindRules.TypeBasicAgent &&
                            agent.MissionKind == "courier" && agent.Level is >= 1 and <= 5 &&
                            agent.BaseLocationId is > 0 && agent.SolarSystemId is > 0)
            .OrderBy(agent => agent.AgentId)
            .ToArray();
        var courierMissions = staticMissions.Missions
            .Where(mission => mission.Courier is { ObjectiveTypeId: > 0, ObjectiveQuantity: > 0 } &&
                              templates.GetValueOrDefault(mission.MissionId) == "agent.missionTemplatizedContent_BasicCourierMission" &&
                              staticMissions.MissionAvailability(mission.MissionId).ArcId is null &&
                              staticMissions.Level(mission.MissionId).Level is >= 1 and <= 5)
            .OrderBy(mission => mission.MissionId)
            .ToArray();

        var deliveries = new List<CourierCase>(2);
        foreach (var agent in agents)
        {
            if (agent.BaseLocationId is not { } pickupId || stations.Get(pickupId) is not { } pickup) continue;
            foreach (var mission in courierMissions)
            {
                if (staticMissions.Level(mission.MissionId).Level != agent.Level ||
                    deliveries.Any(delivery => delivery.Mission.MissionId == mission.MissionId || delivery.Agent.AgentId == agent.AgentId))
                    continue;
                var level = MissionCourierRules.CourierLevel(staticMissions.IsEpicArcStep(mission.MissionId),
                    agent.Level, staticMissions.Level(mission.MissionId).Level);
                var dropoff = MissionCourierRules.Destination(mission.MissionId, characterId,
                    pickup.Id, pickup.SolarSystemId, level, gates, stations.All);
                if (dropoff is null) continue;
                deliveries.Add(new CourierCase(mission, agent, pickup, dropoff));
                break;
            }
            if (deliveries.Count == 2) break;
        }
        Assert.Equal(2, deliveries.Count);
        var hangarDelivery = deliveries[0];
        var shipDelivery = deliveries[1];
        var store = new AgentMissionStore(rig.Source, rig.AgentsSchema);
        var now = DateTimeOffset.UtcNow;
        var hangarContent = ContentOf(hangarDelivery);
        await store.OfferAsync(characterId, hangarDelivery.Agent.AgentId, hangarContent, now,
            $"audit-offer:{characterId}:{hangarDelivery.Agent.AgentId}");
        await store.AcceptAsync(characterId, hangarDelivery.Agent.AgentId, now,
            $"audit-accept:{characterId}:{hangarDelivery.Agent.AgentId}");

        var sink = new Capture();
        var session = new ClientSession(sink)
        {
            UserId = 1,
            SessionId = characterId + 500_000,
            ClientId = characterId,
            CharacterId = characterId,
        };
        session.Set("charid", new PyLong(characterId));
        rig.Sessions.Enter(characterId, session);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var courier = hangarDelivery.Mission.Courier!;
        var wrongStation = Assert.IsAssignableFrom<Station>(stations.All.First(station =>
            station.Id != hangarDelivery.Pickup.Id && station.Id != hangarDelivery.Dropoff.Id));
        var wrongLocationCargo = await CreateCargoAsync(items, rig, characterId, wrongStation.Id,
            (int)courier.ObjectiveTypeId!.Value, courier.ObjectiveQuantity, courier.Singleton,
            MissionCourierRules.CargoTag(hangarDelivery.Mission.MissionId));

        var docked = rig.Space.PilotDocked
            ?? throw new Xunit.Sdk.XunitException("The Host did not wire the pilot-docked mission transition.");
        await docked(characterId, hangarDelivery.Dropoff.Id, CancellationToken.None);

        Assert.Equal(AgentMissionState.Accepted,
            (await store.ReadAsync(characterId, hangarDelivery.Agent.AgentId))!.State);
        Assert.Equal((wrongStation.Id, ItemFlags.Hangar),
            ((await items.GetAsync(wrongLocationCargo))!.LocationId, (await items.GetAsync(wrongLocationCargo))!.Flag));
        Assert.DoesNotContain(sink.Packets,
            packet => MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnAgentMissionChange" });

        await DeleteCargoAsync(items, rig, wrongLocationCargo);
        await CreateCargoAsync(items, rig, characterId, hangarDelivery.Dropoff.Id,
            (int)courier.ObjectiveTypeId.Value, courier.ObjectiveQuantity, courier.Singleton,
            MissionCourierRules.CargoTag(hangarDelivery.Mission.MissionId));
        session.Set("stationid", new PyLong(hangarDelivery.Dropoff.Id));
        await Task.WhenAll(
            docked(characterId, hangarDelivery.Dropoff.Id, CancellationToken.None),
            docked(characterId, hangarDelivery.Dropoff.Id, CancellationToken.None));
        Assert.Equal(AgentMissionState.Completed,
            (await store.ReadAsync(characterId, hangarDelivery.Agent.AgentId))!.State);
        Assert.DoesNotContain(await items.OwnedByAsync(characterId), item =>
            item.CustomInfo == MissionCourierRules.CargoTag(hangarDelivery.Mission.MissionId));
        var completedNotificationCount = sink.Packets.Count(
            packet => MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnAgentMissionChange" });
        Assert.Equal(1, completedNotificationCount);
        var wallet = new Eve.Wallet.WalletTransactions(rig.Source, rig.WalletSchema);
        var balanceAfterCompletion = await wallet.AccountBalanceAsync(new Eve.Wallet.WalletAccount(characterId), default);
        await docked(characterId, hangarDelivery.Dropoff.Id, CancellationToken.None);
        Assert.Equal(balanceAfterCompletion,
            await wallet.AccountBalanceAsync(new Eve.Wallet.WalletAccount(characterId), default));
        Assert.Equal(completedNotificationCount, sink.Packets.Count(
            packet => MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnAgentMissionChange" }));

        await store.OfferAsync(characterId, shipDelivery.Agent.AgentId, ContentOf(shipDelivery), now,
            $"audit-offer:{characterId}:{shipDelivery.Agent.AgentId}");
        await store.AcceptAsync(characterId, shipDelivery.Agent.AgentId, now,
            $"audit-accept:{characterId}:{shipDelivery.Agent.AgentId}");
        var shipCargo = shipDelivery.Mission.Courier!;
        var shipId = await CreateShipCargoAsync(items, rig, characterId, shipDelivery.Dropoff.Id,
            (int)shipCargo.ObjectiveTypeId!.Value, shipCargo.ObjectiveQuantity, shipCargo.Singleton,
            MissionCourierRules.CargoTag(shipDelivery.Mission.MissionId));
        session.Set("shipid", new PyLong(shipId));
        session.Set("stationid", new PyLong(shipDelivery.Dropoff.Id));
        await docked(characterId, shipDelivery.Dropoff.Id, CancellationToken.None);
        Assert.Equal(AgentMissionState.Completed,
            (await store.ReadAsync(characterId, shipDelivery.Agent.AgentId))!.State);
        Assert.DoesNotContain(await items.OwnedByAsync(characterId), item =>
            item.CustomInfo == MissionCourierRules.CargoTag(shipDelivery.Mission.MissionId));

        await rig.RestartAsync();
        var reopened = new AgentMissionStore(rig.Source, rig.AgentsSchema);
        Assert.Equal(AgentMissionState.Completed,
            (await reopened.ReadAsync(characterId, hangarDelivery.Agent.AgentId))!.State);
        Assert.Equal(AgentMissionState.Completed,
            (await reopened.ReadAsync(characterId, shipDelivery.Agent.AgentId))!.State);
    }

    private static AgentMissionContent ContentOf(CourierCase mission) => new(
        mission.Mission.MissionId, mission.Mission.NameId, mission.Mission.NameEn, "courier",
        mission.Agent.ImportantMission, mission.Mission.ExpirationMinutes);

    private static async Task<long> CreateCargoAsync(
        PgItemStore items, CharacterHostTests.CharacterHostRig rig, long ownerId, long stationId,
        int typeId, long quantity, bool singleton, string tag)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await items.CreateAsync(connection, transaction,
            new NewItem(typeId, ownerId, stationId, ItemFlags.Hangar, quantity,
                Singleton: singleton, CustomInfo: tag));
        await transaction.CommitAsync();
        return itemId;
    }

    private static async Task<long> CreateShipCargoAsync(
        PgItemStore items, CharacterHostTests.CharacterHostRig rig, long ownerId, long stationId,
        int typeId, long quantity, bool singleton, string tag)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var shipId = await items.CreateAsync(connection, transaction,
            new NewItem(ShuttleTypeId, ownerId, stationId, ItemFlags.Hangar, Singleton: true));
        await items.CreateAsync(connection, transaction,
            new NewItem(typeId, ownerId, shipId, ItemFlags.Cargo, quantity,
                Singleton: singleton, CustomInfo: tag));
        await transaction.CommitAsync();
        return shipId;
    }

    private static async Task DeleteCargoAsync(PgItemStore items, CharacterHostTests.CharacterHostRig rig, long itemId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await items.DeleteAsync(connection, transaction, itemId);
        await transaction.CommitAsync();
    }

    private sealed record CourierCase(MissionStatic Mission, AgentMissionAgentData Agent, Station Pickup, Station Dropoff);

    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
