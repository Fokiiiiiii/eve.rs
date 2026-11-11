using System.Collections.Concurrent;
using Eve.Data;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Fleet_broadcast_rate_limit_is_shared_across_manager_and_bound_object_host_routes()
    {
        const long fleetId = 900_401;
        const long leaderId = 900_402;
        const long memberId = 900_403;
        const long solarSystemId = 30_000_141;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        await using var dataSource = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = $"fleet_broadcast_{Guid.NewGuid():N}";
        await CreateFleetBroadcastSchemaAsync(dataSource, schema);

        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var seed = new NpgsqlCommand($"""
                INSERT INTO {schema}.fleets (fleet_id, leader_character_id) VALUES ({fleetId}, {leaderId});
                INSERT INTO {schema}.fleet_members (fleet_id, character_id, wing_id, squad_id, role, job)
                    VALUES ({fleetId}, {leaderId}, -1, -1, 1, 2), ({fleetId}, {memberId}, -1, -1, 4, 0);
                """, connection))
                await seed.ExecuteNonQueryAsync();

            var store = new FleetStore(dataSource, schema);
            var connected = new ConcurrentDictionary<long, ClientSession>();
            var opened = 0;
            var now = FixedNowMs;
            var broadcastRateLimiter = new FleetBroadcastRateLimiter();
            var fleetObject = new FleetObjectService(store, () => now,
                sessionForCharacter: id => connected.GetValueOrDefault(id),
                sameBubble: (_, _) => true,
                broadcastRateLimiter: broadcastRateLimiter);
            var fleetManager = new FleetManagerService(store,
                sessionOf: id => connected.GetValueOrDefault(id),
                sameBubble: (_, _) => true,
                nowUnixMs: () => now,
                broadcastRateLimiter: broadcastRateLimiter);
            Start(configure: registry =>
            {
                registry.Register(fleetObject);
                registry.Register(fleetManager);
            }, sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? leaderId : memberId;
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("solarsystemid2", new PyLong(solarSystemId));
                session.Set("ballparkid", new PyLong(77));
                connected[characterId] = session;
            });

            var leaderFront = await Handshaken(await ConnectAsync());
            var leaderOpen = Open(1_790_646_377_000UL * 15, 48);
            await leaderFront.SendAsync(leaderOpen);
            await leaderFront.ReceiveAsync();
            var memberFront = await Handshaken(await ConnectAsync());
            var memberOpen = Open(1_790_646_377_000UL * 15 + 1, 49);
            await memberFront.SendAsync(memberOpen);
            await memberFront.ReceiveAsync();

            var transport = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            var emptyKwargs = new PyDict([]);
            var callId = 1L;
            async Task<((string Name, string IdType, PyValue[] Args)[] Notifications, PyValue? Result)> Call(
                string service, string method, params PyValue[] args)
            {
                await leaderFront.SendAsync(new Packet(leaderOpen.SessionId, MarshalCodec.Encode(
                    Packets.CallWithKwargs(leaderOpen, callId++, service, method, new PyTuple(args), transport))));
                var notifications = new List<(string Name, string IdType, PyValue[] Args)>();
                while (true)
                {
                    var send = Assert.IsType<Send>(await leaderFront.ReceiveAsync());
                    var decoded = MarshalCodec.Decode(send.Payload);
                    var packet = Assert.IsType<PyObject>(decoded);
                    var values = Assert.IsType<PyTuple>(packet.Args).Items;
                    if (Number(values[0]) == MachoMessage.Notification)
                    {
                        notifications.Add(ReadFleetNotification(send));
                        continue;
                    }
                    return ([.. notifications], Result(decoded));
                }
            }

            async Task AssertMemberBroadcastAsync(string expectedName)
            {
                var received = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
                Assert.Equal(("OnFleetBroadcast", "fleetid"), (received.Name, received.IdType));
                Assert.Equal(expectedName, T(received.Args[0]));
                Assert.Equal(6, received.Args.Length);
            }

            static PyValue[] BroadcastArgs(string name) =>
                [new PyStr(name), new PyInt(3), PyNone.Instance, PyNone.Instance];

            var first = await Call("fleetMgr", "BroadcastToSystem", BroadcastArgs("EnemySpotted"));
            Assert.Same(PyNone.Instance, first.Result);
            Assert.Equal("OnFleetBroadcast", Assert.Single(first.Notifications).Name);
            await AssertMemberBroadcastAsync("EnemySpotted");

            var immediatelyRepeated = await Call("fleetObjectHandler", "MachoBindObject",
                new PyTuple([new PyLong(fleetId)]),
                new PyTuple([new PyStr("SendBroadcast"), new PyTuple(BroadcastArgs("EnemySpotted")), emptyKwargs]));
            Assert.Empty(immediatelyRepeated.Notifications);
            Assert.IsType<PyTuple>(immediatelyRepeated.Result);

            now += 665;
            var earlyDifferent = await Call("fleetMgr", "BroadcastToBubble", BroadcastArgs("NeedBackup"));
            Assert.Empty(earlyDifferent.Notifications);
            now += 1;
            var stillEarlyDifferent = await Call("fleetMgr", "BroadcastToSystem", BroadcastArgs("NeedBackup"));
            Assert.Empty(stillEarlyDifferent.Notifications);
            now += 1;
            var different = await Call("fleetObjectHandler", "MachoBindObject",
                new PyTuple([new PyLong(fleetId)]),
                new PyTuple([new PyStr("SendBroadcast"), new PyTuple(BroadcastArgs("NeedBackup")), emptyKwargs]));
            Assert.Equal("OnFleetBroadcast", Assert.Single(different.Notifications).Name);
            await AssertMemberBroadcastAsync("NeedBackup");

            now += 1_999;
            var sameTooSoon = await Call("fleetMgr", "BroadcastToSystem", BroadcastArgs("NeedBackup"));
            Assert.Empty(sameTooSoon.Notifications);
            now += 1;
            var sameAllowed = await Call("fleetMgr", "BroadcastToSystem", BroadcastArgs("NeedBackup"));
            Assert.Equal("OnFleetBroadcast", Assert.Single(sameAllowed.Notifications).Name);
            await AssertMemberBroadcastAsync("NeedBackup");
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateFleetBroadcastSchemaAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var create = new NpgsqlCommand($"""
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.fleets (
                fleet_id bigint PRIMARY KEY,
                leader_character_id bigint,
                auto_join_squad_id bigint,
                max_member_count smallint NOT NULL DEFAULT 256,
                is_free_move boolean NOT NULL DEFAULT false,
                advert_active boolean NOT NULL DEFAULT false,
                motd text NOT NULL DEFAULT ''
            );
            CREATE TABLE {schema}.fleet_members (
                fleet_id bigint NOT NULL,
                character_id bigint NOT NULL,
                wing_id bigint NOT NULL DEFAULT -1,
                squad_id bigint NOT NULL DEFAULT -1,
                role smallint NOT NULL,
                job smallint NOT NULL DEFAULT 0,
                joined_at timestamptz NOT NULL DEFAULT now(),
                accepts_fleet_warp boolean NOT NULL DEFAULT true,
                accepts_conduit_jumps boolean NOT NULL DEFAULT true,
                accepts_fleet_regroups boolean NOT NULL DEFAULT true,
                ship_type_id bigint,
                PRIMARY KEY (fleet_id, character_id),
                UNIQUE (character_id)
            );
            CREATE TABLE {schema}.fleet_wings (
                fleet_id bigint NOT NULL,
                wing_id bigint NOT NULL,
                name text NOT NULL DEFAULT '',
                position integer NOT NULL DEFAULT 0,
                PRIMARY KEY (fleet_id, wing_id)
            );
            CREATE TABLE {schema}.fleet_squads (
                fleet_id bigint NOT NULL,
                squad_id bigint NOT NULL,
                wing_id bigint NOT NULL,
                name text NOT NULL DEFAULT '',
                position integer NOT NULL DEFAULT 0,
                PRIMARY KEY (fleet_id, squad_id)
            );
            CREATE TABLE {schema}.fleet_join_requests (
                fleet_id bigint NOT NULL,
                character_id bigint NOT NULL,
                corporation_id bigint,
                alliance_id bigint,
                war_faction_id bigint,
                security_status double precision NOT NULL DEFAULT 0,
                created_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (fleet_id, character_id)
            );
            """, connection);
        await create.ExecuteNonQueryAsync();
    }
}
