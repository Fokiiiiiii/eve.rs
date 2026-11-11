using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Inventory;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    [PgFact]
    public async Task An_unresolved_npc_owner_is_not_persisted_as_a_character_or_notified_as_final_blow()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        const long npcCorporationId = 1_000_016;
        const long unknownNpcOwnerId = 9_999;
        const long npcVictimShipId = 2_800_000_002L;
        Assert.Null(await rig.Characters.GetAsync(unknownNpcOwnerId));
        var occurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var kill = new ShipKillmailEvent(
            null, npcCorporationId, 0, 500_001, npcVictimShipId, 587, 30_000_001, occurredAt,
            123, unknownNpcOwnerId,
            [new ShipKillmailAttacker(unknownNpcOwnerId, 0, 0, 0, unknownNpcOwnerId, 0, 216, 123, null, 3)], [], 15_000m);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Events.PublishOnceInTransactionAsync(connection, transaction, ShipKillmailEvent.Topic, kill,
                $"ship:{npcVictimShipId}");
            await transaction.CommitAsync();
        }

        JsonObject? record = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var state = await ReadGameStoreTableAsync(rig, "killmails");
            record = (state["records"] as JsonObject)?.Select(pair => pair.Value)
                .OfType<JsonObject>().FirstOrDefault(candidate => (long?)candidate["killmailTime"] == occurredAt);
            if (record is not null)
                break;
            await Task.Delay(50);
        }

        Assert.NotNull(record);
        Assert.Null(record["victimCharacterID"]);
        Assert.Null(record["finalCharacterID"]);
        Assert.Equal(npcCorporationId, (long?)record["victimCorporationID"]);
        Assert.Equal(15_000m, (decimal?)record["bountyClaimed"]);
        Assert.Empty(Assert.IsType<JsonArray>(record["attackers"]));
        Assert.Empty(await NoticesAsync(rig, unknownNpcOwnerId));
    }

    [RealAbyssalSimPgFact]
    public async Task A_player_ship_death_creates_a_persistent_victim_kill_report_notification()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("The test simulation did not start.");
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal))
                {
                    endpoint = line["eve-sim: listening on ".Length..];
                    break;
                }
            Assert.NotNull(endpoint);
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            await using var rig = await CharacterHostRig.CreateAsync(simSocket: $"tcp:{endpoint}");
            await rig.WaitForSimulationConnectionAsync();
            var characterId = await rig.CreateCharacterAsync();
            var shipId = await GiveItemAsync(rig, characterId, 587, rig.Station, ItemFlags.Hangar, singleton: true);
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await new PgItemStore(rig.Source, rig.InventorySchema).CreateAsync(connection, transaction,
                    new NewItem(34, characterId, shipId, ItemFlags.Cargo, Quantity: 5, Singleton: false));
                await transaction.CommitAsync();
            }
            await SetShipAsync(rig, characterId, shipId);
            var session = new ClientSession { SessionId = 92_001, CharacterId = characterId, UserId = 1, Sink = new RecordingPacketSink() };
            session.Set("stationid", new PyLong(rig.Station));
            rig.Sessions.Enter(characterId, session);
            var shipService = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("ship"));
            await BoundAtAsync(shipService, session, rig.Station, "Undock", new PyLong(shipId), PyBool.True);
            Assert.Equal((long?)shipId, session.GetInt("shipid"));
            Assert.NotNull((await rig.Simulation.CommandAsync((ulong)session.SessionId, SimCommands.GetCondition, [], CancellationToken.None)).Condition);

            var items = new PgItemStore(rig.Source, rig.InventorySchema);
            var victimBefore = await rig.Characters.GetAsync(characterId);
            var shipBefore = await items.GetAsync(shipId);
            Assert.Equal(shipId, victimBefore!.ShipId);
            Assert.NotNull(shipBefore);
            Assert.Equal((long)victimBefore.SolarSystemId, shipBefore!.LocationId);
            Assert.Equal(ItemFlags.InSpace, shipBefore.Flag);

            const long npcShipId = 2_700_000_009;
            const long npcCorporationId = 1_000_125;
            var npcDamage = new PyDict([
                new(new PyStr("attackType"), new PyStr("otherPlayerWeapons")),
                new(new PyStr("source"), new PyLong(npcShipId)),
                new(new PyStr("target"), new PyLong(shipId)),
                new(new PyStr("weapon"), new PyInt(216)),
                new(new PyStr("damage"), new PyReal(123.5)),
                new(new PyStr("owner"), Py.Tuple(new PyInt(2), new PyLong(npcCorporationId))),
            ]);
            var receive = typeof(SpaceSessions).GetMethod("OnReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            receive.Invoke(rig.Space, [new SimNotify((ulong)session.SessionId, "OnDamageMessage", "clientID",
                MarshalCodec.Encode(Py.Tuple(npcDamage)))]);

            var received = new ConcurrentQueue<SimMessage>();
            var lost = new TaskCompletionSource<ShipLoss>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Space.ShipLost += loss =>
            {
                if (loss.ShipId == shipId)
                    lost.TrySetResult(loss);
            };
            var pilotsField = typeof(SpaceSessions).GetField("_pilots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var pilots = pilotsField.GetValue(rig.Space)!;
            var containsPilot = pilots.GetType().GetMethod("ContainsKey")!;
            Assert.True((bool)containsPilot.Invoke(pilots, [(ulong)session.SessionId])!);
            var pilot = pilots.GetType().GetMethod("get_Item")!.Invoke(pilots, [(ulong)session.SessionId])!;
            Assert.Equal(shipId, (long)pilot.GetType().GetProperty("ShipId")!.GetValue(pilot)!);
            var simField = typeof(SpaceSessions).GetField("_sim", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.Same(rig.Simulation, simField.GetValue(rig.Space));
            rig.Simulation.Received += received.Enqueue;
            rig.Simulation.Destroy(shipId, SimCredit.Nobody);
            using var destroyed = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!received.OfType<SimDestroyed>().Any(message => message.ShipId == shipId))
                await Task.Delay(50, destroyed.Token);
            var destruction = received.OfType<SimDestroyed>().Single(message => message.ShipId == shipId);
            Assert.Equal((ulong)session.SessionId, destruction.SessionId);
            await rig.Space.QuiesceAsync(TimeSpan.FromSeconds(30));
            var loss = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((characterId, shipId, 587), (loss.CharacterId, loss.ShipId, loss.ShipTypeId));
            var removed = false;
            while (!removed && !destroyed.IsCancellationRequested)
            {
                removed = await items.GetAsync(shipId, destroyed.Token) is null;
                if (!removed)
                    await Task.Delay(50, destroyed.Token);
            }
            Assert.True(removed, $"Host did not persist destruction for session {destruction.SessionId}, ship {shipId}.");

            JsonObject[] notices = [];
            var notificationDeadline = DateTime.UtcNow.AddSeconds(10);
            JsonObject? victimNotice = null;
            while (DateTime.UtcNow < notificationDeadline)
            {
                notices = await NoticesAsync(rig, characterId);
                victimNotice = notices.FirstOrDefault(notice =>
                (int?)notice["typeID"] == 140 &&
                (int?)notice["groupID"] == 8 &&
                (long?)notice["receiverID"] == characterId &&
                notice["data"]?["killMailID"] is not null &&
                notice["data"]?["killMailHash"] is not null &&
                (int?)notice["data"]?["victimShipTypeID"] == 587);
                if (victimNotice is not null)
                    break;
                await Task.Delay(50, destroyed.Token);
            }
            Assert.NotNull(victimNotice);

            var killmailId = (long)victimNotice["data"]!["killMailID"]!;
            var killmailHash = victimNotice["data"]!["killMailHash"]!.GetValue<string>();
            var killmailState = await ReadGameStoreTableAsync(rig, "killmails");
            var record = Assert.IsType<JsonObject>((killmailState["records"] as JsonObject)?[killmailId.ToString()]);
            Assert.Equal(characterId, (long?)record["victimCharacterID"]);
            Assert.Equal(587, (int?)record["victimShipTypeID"]);
            Assert.Equal(123.5, (double?)record["victimDamageTaken"]);
            var npcAttacker = Assert.Single(Assert.IsType<JsonArray>(record["attackers"]).OfType<JsonObject>());
            Assert.Null(npcAttacker["characterID"]);
            Assert.Equal(npcCorporationId, (long?)npcAttacker["corporationID"]);
            Assert.Null(npcAttacker["shipTypeID"]);
            Assert.Equal(123.5, (double?)npcAttacker["damageDone"]);
            var cargoRecord = Assert.Single(Assert.IsType<JsonArray>(record["items"]).OfType<JsonObject>());
            Assert.Equal(34, (int?)cargoRecord["typeID"]);
            Assert.Equal(5L, cargoRecord["qtyDropped"]!.GetValue<long>() + cargoRecord["qtyDestroyed"]!.GetValue<long>());

            var killmailService = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("warStatisticMgr"));
            var report = await CallAsync(killmailService, "GetKillMail", session, new PyLong(killmailId), new PyStr(killmailHash));
            Assert.Equal(killmailId, (long)Eve.Macho.JsSemantics.NumberValue(Field(report, "killID")));
            Assert.Equal(characterId, (long)Eve.Macho.JsSemantics.NumberValue(Field(report, "victimCharacterID")));
            Assert.Equal(587, (long)Eve.Macho.JsSemantics.NumberValue(Field(report, "victimShipTypeID")));
            var killBlob = Eve.Macho.JsSemantics.StringValue(Field(report, "killBlob"));
            Assert.NotNull(killBlob);
            Assert.StartsWith("<attackers><a r=\"1000125\" w=\"216\" d=\"123\" t=\"0.0\" /></attackers><items><i t=\"34\" f=\"5\" s=\"0\" d=\"", killBlob);
            Assert.EndsWith("</i></items>", killBlob);

            var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
            var recent = Assert.IsType<PyList>(await CallAsync(charMgr, "GetRecentShipKillsAndLosses", session,
                new PyInt(25), new PyInt(0)));
            Assert.Single(recent.Items);

        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [PgFact]
    public async Task A_player_final_blow_gets_a_persisted_report_notification_and_recent_kill()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var victimId = await rig.CreateCharacterAsync();
        var attackerId = await rig.CreateCharacterAsync();
        var killRightOwnerId = await rig.CreateCharacterAsync();
        var victim = (await rig.Characters.GetAsync(victimId))!;
        const long victimShipId = 2_800_000_003L;
        const long attackerShipId = 2_800_000_004L;
        var occurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var crimewatch = new Eve.Crimewatch.PgCrimewatchStore(rig.Source, rig.CrimewatchSchema);
        var killRightId = await crimewatch.CreateKillRightAsync(
            killRightOwnerId, victimId, victim.SolarSystemId, occurredAt - 2);
        Assert.NotNull(killRightId);
        Assert.True(await crimewatch.SetKillRightSaleAsync(
            killRightOwnerId, killRightId.Value, 0m, null, occurredAt - 2));
        Assert.True((await crimewatch.BuyKillRightAsync(
            killRightId.Value, attackerId, [attackerId], null, occurredAt - 1, null)).Success);
        var playerBounties = new Eve.Crimewatch.PlayerBountyStore(rig.Source, rig.CrimewatchSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await playerBounties.RecordPlacementAsync(connection, transaction,
                new Eve.Crimewatch.BountyPool(victimId, Eve.Crimewatch.BountyOwnerKind.Character,
                    victim.CorporationId, victim.AllianceId ?? 0, 0), killRightOwnerId,
                12_345m, $"killmail-test:{victimShipId}", occurredAt - 2);
            await transaction.CommitAsync();
        }
        var kill = new ShipKillmailEvent(
            victimId, victim.CorporationId, victim.AllianceId ?? 0, victim.FactionId ?? 0,
            victimShipId, 587, victim.SolarSystemId, occurredAt, 44.5, attackerShipId,
            [new ShipKillmailAttacker(attackerId, 0, 0, 0, attackerShipId, 587, 216, 44.5, null, 1)], []);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Events.PublishOnceInTransactionAsync(connection, transaction, ShipKillmailEvent.Topic, kill,
                $"ship:{victimShipId}");
            await transaction.CommitAsync();
        }
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Events.PublishOnceInTransactionAsync(connection, transaction, ShipKillmailEvent.Topic, kill,
                $"ship:{victimShipId}");
            await transaction.CommitAsync();
        }

        JsonObject? finalNotice = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            finalNotice = (await NoticesAsync(rig, attackerId)).FirstOrDefault(notice =>
                (int?)notice["typeID"] == 141 && notice["data"]?["victimID"]?.GetValue<long>() == victimId);
            if (finalNotice is not null)
                break;
            await Task.Delay(50);
        }
        Assert.NotNull(finalNotice);
        var killmailId = finalNotice["data"]!["killMailID"]!.GetValue<long>();
        var hash = finalNotice["data"]!["killMailHash"]!.GetValue<string>();
        var killmailState = await ReadGameStoreTableAsync(rig, "killmails");
        var record = Assert.IsType<JsonObject>((killmailState["records"] as JsonObject)?[killmailId.ToString()]);
        Assert.Equal(killRightOwnerId, (long?)record["killRightSupplied"]);
        Assert.Equal(12_345m, (decimal?)record["bountyClaimed"]);
        Assert.Equal(0m, Assert.Single(await playerBounties.PoolsOfAsync([victimId])).Bounty);
        Assert.Equal(12_345m, Assert.Single(
            await playerBounties.TopHuntersAsync(Eve.Crimewatch.BountyOwnerKind.Character, 10),
            hunter => hunter.HunterId == attackerId).BountiesClaimed);
        Assert.Equal(1, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SchedulerSchema}.jobs WHERE kind = 'player-bounty.payout' AND key = 'player-bounty:kill:{victimShipId}'"));
        Assert.Empty(await crimewatch.KillRightsForCharacterAsync(killRightOwnerId, occurredAt + 1));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("warStatisticMgr"));
        var report = await CallAsync(service,
            "GetKillMail",
            new ClientSession { CharacterId = victimId, UserId = 1, Sink = new RecordingPacketSink() },
            new PyLong(killmailId), new PyStr(hash));
        Assert.Equal(attackerId, (long)Eve.Macho.JsSemantics.NumberValue(Field(report, "finalCharacterID")));

        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        var recent = Assert.IsType<PyList>(await CallAsync(charMgr, "GetRecentShipKillsAndLosses",
            new ClientSession { CharacterId = attackerId, UserId = 1, Sink = new RecordingPacketSink() },
            new PyInt(25), new PyInt(0)));
        Assert.Single(recent.Items);
    }
}
