using System.Text;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Fleet_creation_initializes_a_default_wing_and_squad_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var service = new FleetObjectService(store, () => FixedNowMs,
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = leaderId;
                    session.Set("charid", new PyLong(leaderId));
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open(1_790_646_377_000UL * 15 + 55, 55);
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = Eve.Services.Py.Dict(("machoVersion", new PyInt(1)));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "fleetObjectHandler", "CreateFleet", new PyTuple([]), kwargs))));
            Send response;
            while (true)
            {
                response = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = MachoPacket.Decode(MarshalCodec.Decode(response.Payload));
                Assert.NotNull(packet);
                if (packet!.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse) break;
            }
            var bound = Assert.IsType<PySubStruct>(Result(MarshalCodec.Decode(response.Payload)));
            Assert.NotNull(bound);

            var fleetId = await store.FleetIdForCharacterAsync(leaderId);
            Assert.NotNull(fleetId);
            var snapshot = await store.SnapshotAsync(fleetId!.Value);
            Assert.NotNull(snapshot);
            var wing = Assert.Single(snapshot!.Wings);
            Assert.Single(wing.Squads);
        }
        finally
        {
            var fleetId = await store.FleetIdForCharacterAsync(leaderId);
            if (fleetId is > 0) await store.DisbandAsync(fleetId.Value, leaderId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_make_leader_transfers_the_boss_job_without_changing_command_roles_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var targetId = leaderId + 1;
        var fleetId = await store.CreateAsync(leaderId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, leaderId, targetId,
                FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, targetId));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? leaderId : targetId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var leader = await OpenFleetFrontAsync(leaderId, 60);
            var target = await OpenFleetFrontAsync(targetId, 61);
            var leaderOid = await BindFleetAsync(leader.Front, leader.Open, fleetId, 1);
            _ = await BindFleetAsync(target.Front, target.Open, fleetId, 1);

            var transfer = await BoundFleetCallAsync(leader.Front, leader.Open, leaderOid, 2,
                "MakeLeader", new PyLong(targetId));
            Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(transfer.Payload)));

            var persisted = await store.SnapshotAsync(fleetId);
            Assert.NotNull(persisted);
            var boss = Assert.Single(persisted!.Members, member => FleetStore.HasBossJob(member.Job));
            Assert.Equal(targetId, boss.CharacterId);
            var newBoss = persisted.Members.Single(member => member.CharacterId == targetId);
            var formerBoss = persisted.Members.Single(member => member.CharacterId == leaderId);
            Assert.Equal(FleetStore.Member, newBoss.Role);
            Assert.Equal(FleetStore.Leader, formerBoss.Role);
            Assert.False(FleetStore.HasBossJob(formerBoss.Job));

            var targetChanges = new[]
            {
                ReadFleetNotification(Assert.IsType<Send>(await target.Front.ReceiveAsync())),
                ReadFleetNotification(Assert.IsType<Send>(await target.Front.ReceiveAsync()))
            };
            Assert.All(targetChanges, notification => Assert.Equal("OnFleetMemberChanged", notification.Name));
            Assert.Equal(new PyLong(leaderId), targetChanges[0].Args[0]);
            Assert.Equal(new PyInt(FleetStore.Leader), targetChanges[0].Args[4]);
            Assert.Equal(new PyInt(FleetStore.Leader), targetChanges[0].Args[9]);
            Assert.Equal(new PyInt(FleetStore.CreatorJob), targetChanges[0].Args[5]);
            Assert.Equal(new PyInt(0), targetChanges[0].Args[10]);
            Assert.Equal(new PyLong(targetId), targetChanges[1].Args[0]);
            Assert.Equal(new PyInt(FleetStore.Member), targetChanges[1].Args[4]);
            Assert.Equal(new PyInt(FleetStore.Member), targetChanges[1].Args[9]);
            Assert.Equal(new PyInt(0), targetChanges[1].Args[5]);
            Assert.Equal(new PyInt(FleetStore.CreatorJob), targetChanges[1].Args[10]);
        }
        finally
        {
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_boss_can_promote_and_demote_the_fleet_commander_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberId = bossId + 1;
        var fleetId = await store.CreateAsync(bossId);
        var initial = await store.SnapshotAsync(fleetId);
        Assert.NotNull(initial);
        var wingId = Assert.Single(initial!.Wings).WingId;
        var squadId = Assert.Single(initial.Wings[0].Squads).SquadId;
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, bossId, memberId,
                FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, memberId));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? bossId : memberId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var boss = await OpenFleetFrontAsync(bossId, 62);
            var member = await OpenFleetFrontAsync(memberId, 63);
            var bossOid = await BindFleetAsync(boss.Front, boss.Open, fleetId, 1);
            _ = await BindFleetAsync(member.Front, member.Open, fleetId, 1);

            var demoteBoss = await BoundFleetCallAsync(boss.Front, boss.Open, bossOid, 2,
                "MoveMember", new PyLong(bossId), new PyLong(wingId), new PyLong(squadId), new PyInt(FleetStore.Member));
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(demoteBoss.Payload)));
            var bossState = await store.MemberForCharacterAsync(bossId);
            Assert.NotNull(bossState);
            Assert.Equal((wingId, squadId, FleetStore.Member), (bossState!.WingId, bossState.SquadId, bossState.Role));
            Assert.True(FleetStore.HasBossJob(bossState.Job));
            var bossDemotion = ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync()));
            Assert.Equal("OnFleetMemberChanged", bossDemotion.Name);
            Assert.Equal(new PyLong(bossId), bossDemotion.Args[0]);
            Assert.Equal(new PyInt(FleetStore.Leader), bossDemotion.Args[4]);
            Assert.Equal(new PyInt(FleetStore.Member), bossDemotion.Args[9]);

            var promoteMember = await BoundFleetCallAsync(boss.Front, boss.Open, bossOid, 3,
                "MoveMember", new PyLong(memberId), new PyLong(FleetStore.NoUnit), new PyLong(FleetStore.NoUnit),
                new PyInt(FleetStore.Leader));
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(promoteMember.Payload)));
            var promotedNotifications = new[]
            {
                ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync())),
                ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync()))
            };
            Assert.Equal(["OnFleetMemberChanged", "OnFleetMove"], promotedNotifications.Select(item => item.Name));
            Assert.Equal(new PyInt(FleetStore.Member), promotedNotifications[0].Args[4]);
            Assert.Equal(new PyInt(FleetStore.Leader), promotedNotifications[0].Args[9]);
            var promoted = await store.MemberForCharacterAsync(memberId);
            Assert.NotNull(promoted);
            Assert.Equal((FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Leader),
                (promoted!.WingId, promoted.SquadId, promoted.Role));
            var promotedFleet = await store.SnapshotAsync(fleetId);
            Assert.NotNull(promotedFleet);
            Assert.Equal(memberId, Assert.Single(promotedFleet!.Members, member => member.Role == FleetStore.Leader).CharacterId);

            var duplicateLeader = await BoundFleetCallWithNotificationsAsync(boss.Front, boss.Open, bossOid, 4,
                "MoveMember", new PyLong(bossId), new PyLong(wingId), new PyLong(squadId), new PyInt(FleetStore.Leader));
            Assert.Equal(PyBool.False, Result(MarshalCodec.Decode(duplicateLeader.Reply.Payload)));
            var moveFailed = Assert.Single(duplicateLeader.Notifications);
            Assert.Equal("OnFleetMoveFailed", moveFailed.Name);
            Assert.Equal(new PyLong(bossId), moveFailed.Args[0]);
            Assert.Equal(PyBool.False, moveFailed.Args[1]);
            var unchangedBoss = await store.MemberForCharacterAsync(bossId);
            Assert.NotNull(unchangedBoss);
            Assert.Equal((wingId, squadId, FleetStore.Member),
                (unchangedBoss!.WingId, unchangedBoss.SquadId, unchangedBoss.Role));

            var demoteMember = await BoundFleetCallAsync(boss.Front, boss.Open, bossOid, 5,
                "MoveMember", new PyLong(memberId), new PyLong(wingId), new PyLong(squadId), new PyInt(FleetStore.Member));
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(demoteMember.Payload)));
            var demotedNotifications = new[]
            {
                ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync())),
                ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync()))
            };
            Assert.Equal(["OnFleetMemberChanged", "OnFleetMove"], demotedNotifications.Select(item => item.Name));
            Assert.Equal(new PyInt(FleetStore.Leader), demotedNotifications[0].Args[4]);
            Assert.Equal(new PyInt(FleetStore.Member), demotedNotifications[0].Args[9]);
            var demoted = await store.MemberForCharacterAsync(memberId);
            Assert.NotNull(demoted);
            Assert.Equal((wingId, squadId, FleetStore.Member), (demoted!.WingId, demoted.SquadId, demoted.Role));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, bossId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_boss_leave_assigns_the_boss_job_to_the_lowest_character_id_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var lowestRemainingId = bossId + 1;
        var fleetCommanderId = bossId + 2;
        var fleetId = await store.CreateAsync(bossId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            foreach (var characterId in new[] { lowestRemainingId, fleetCommanderId })
            {
                Assert.True(await store.InviteAsync(fleetId, bossId, characterId,
                    FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));
                Assert.NotNull(await store.JoinAsync(fleetId, characterId));
            }

            await using (var connection = await source.OpenConnectionAsync())
            await using (var changeRoles = connection.CreateCommand())
            {
                changeRoles.CommandText = $"UPDATE {schema}.fleet_members SET wing_id = -1, squad_id = -1, role = $1 WHERE fleet_id = $2 AND character_id = $3";
                changeRoles.Parameters.AddWithValue(FleetStore.Member);
                changeRoles.Parameters.AddWithValue(fleetId);
                changeRoles.Parameters.AddWithValue(bossId);
                Assert.Equal(1, await changeRoles.ExecuteNonQueryAsync());
                changeRoles.Parameters[2].Value = fleetCommanderId;
                changeRoles.Parameters[0].Value = FleetStore.Leader;
                Assert.Equal(1, await changeRoles.ExecuteNonQueryAsync());
            }

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) switch
                    {
                        1 => bossId,
                        2 => lowestRemainingId,
                        _ => fleetCommanderId
                    };
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var boss = await OpenFleetFrontAsync(bossId, 64);
            var lowest = await OpenFleetFrontAsync(lowestRemainingId, 65);
            _ = await OpenFleetFrontAsync(fleetCommanderId, 66);
            var bossOid = await BindFleetAsync(boss.Front, boss.Open, fleetId, 1);

            var leave = await BoundFleetCallAsync(boss.Front, boss.Open, bossOid, 2, "LeaveFleet", PyNone.Instance);
            Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(leave.Payload)));
            Assert.Null(await store.FleetIdForCharacterAsync(bossId));

            var successor = await store.MemberForCharacterAsync(lowestRemainingId);
            Assert.NotNull(successor);
            Assert.True(FleetStore.HasBossJob(successor!.Job));
            var commander = await store.MemberForCharacterAsync(fleetCommanderId);
            Assert.NotNull(commander);
            Assert.Equal(FleetStore.Leader, commander!.Role);
            Assert.False(FleetStore.HasBossJob(commander.Job));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var readBoss = connection.CreateCommand())
            {
                readBoss.CommandText = $"SELECT leader_character_id FROM {schema}.fleets WHERE fleet_id = $1";
                readBoss.Parameters.AddWithValue(fleetId);
                Assert.Equal(lowestRemainingId, Convert.ToInt64(await readBoss.ExecuteScalarAsync(),
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            var successorNotifications = new[]
            {
                ReadFleetNotification(Assert.IsType<Send>(await lowest.Front.ReceiveAsync())),
                ReadFleetNotification(Assert.IsType<Send>(await lowest.Front.ReceiveAsync()))
            };
            Assert.Equal(["OnFleetLeave", "OnFleetMemberChanged"], successorNotifications.Select(item => item.Name));
            Assert.Equal(new PyLong(bossId), successorNotifications[0].Args[0]);
            Assert.Equal(new PyInt(FleetStore.CreatorJob), successorNotifications[1].Args[10]);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, lowestRemainingId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_squad_commander_can_invite_a_member_into_their_squad_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var commanderId = leaderId + 1;
        var inviteeId = leaderId + 2;
        var secondInviteeId = leaderId + 3;
        var thirdInviteeId = leaderId + 4;
        var fleetId = await store.CreateAsync(leaderId);
        var initial = await store.SnapshotAsync(fleetId);
        Assert.NotNull(initial);
        var wing = Assert.Single(initial!.Wings);
        var squadId = Assert.Single(wing.Squads).SquadId;
        var otherWingId = await store.CreateWingAsync(fleetId, leaderId);
        var otherSquadId = await store.CreateSquadAsync(fleetId, leaderId, otherWingId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, leaderId, commanderId, wing.WingId, squadId,
                FleetStore.SquadCommander));
            Assert.NotNull(await store.JoinAsync(fleetId, commanderId));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) switch
                    {
                        1 => commanderId,
                        2 => inviteeId,
                        3 => secondInviteeId,
                        _ => thirdInviteeId
                    };
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var commander = await OpenFleetFrontAsync(commanderId, 56);
            var invitee = await OpenFleetFrontAsync(inviteeId, 57);
            var secondInvitee = await OpenFleetFrontAsync(secondInviteeId, 58);
            var thirdInvitee = await OpenFleetFrontAsync(thirdInviteeId, 59);
            var commanderOid = await BindFleetAsync(commander.Front, commander.Open, fleetId, 1);
            var inviteeOid = await BindFleetAsync(invitee.Front, invitee.Open, fleetId, 1);
            var secondInviteeOid = await BindFleetAsync(secondInvitee.Front, secondInvitee.Open, fleetId, 1);
            var thirdInviteeOid = await BindFleetAsync(thirdInvitee.Front, thirdInvitee.Open, fleetId, 1);

            var outOfScope = await BoundFleetCallAsync(commander.Front, commander.Open, commanderOid, 2,
                "Invite", new PyLong(inviteeId), new PyLong(otherWingId), new PyLong(otherSquadId),
                new PyInt(FleetStore.Member));
            Assert.Equal("FleetNotCommanderOrBoss", FleetUserError(outOfScope));
            Assert.Null(await store.JoinAsync(fleetId, inviteeId));

            var invitation = await BoundFleetCallAsync(commander.Front, commander.Open, commanderOid, 2,
                "Invite", new PyLong(inviteeId), new PyLong(wing.WingId), new PyLong(squadId),
                new PyInt(FleetStore.Member));
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(invitation.Payload)));
            Assert.Equal("OnFleetInvite", ReadFleetNotification(Assert.IsType<Send>(await invitee.Front.ReceiveAsync())).Name);

            var accepted = await BoundFleetCallAsync(invitee.Front, invitee.Open, inviteeOid, 2,
                "AcceptInvite", PyNone.Instance);
            var acceptedPacket = MachoPacket.Decode(MarshalCodec.Decode(accepted.Payload));
            Assert.NotNull(acceptedPacket);
            Assert.True(acceptedPacket!.Type == MachoMessage.CallRsp,
                acceptedPacket.Type == MachoMessage.ErrorResponse ? FleetUserError(accepted) : acceptedPacket.Type.ToString());
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(accepted.Payload)));
            var joined = await store.MemberForCharacterAsync(inviteeId);
            Assert.NotNull(joined);
            Assert.Equal((wing.WingId, squadId, FleetStore.Member), (joined!.WingId, joined.SquadId, joined.Role));
            Assert.Equal(fleetId, await store.FleetIdForCharacterAsync(inviteeId));

            var massInvitation = await BoundFleetCallAsync(commander.Front, commander.Open, commanderOid, 3,
                "MassInvite", new PyList([new PyLong(secondInviteeId)]), new PyLong(wing.WingId),
                new PyLong(squadId), new PyInt(FleetStore.Member));
            Assert.Equal(new PyList([new PyLong(secondInviteeId)]), Result(MarshalCodec.Decode(massInvitation.Payload)));
            Assert.Equal("OnFleetInvite", ReadFleetNotification(Assert.IsType<Send>(await secondInvitee.Front.ReceiveAsync())).Name);
            var secondAccepted = await BoundFleetCallAsync(secondInvitee.Front, secondInvitee.Open, secondInviteeOid, 2,
                "AcceptInvite", PyNone.Instance);
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(secondAccepted.Payload)));
            var defaultJoined = await store.MemberForCharacterAsync(secondInviteeId);
            Assert.NotNull(defaultJoined);
            Assert.Equal(wing.WingId, defaultJoined!.WingId);
            Assert.Equal(squadId, defaultJoined.SquadId);

            var defaultInvitation = await BoundFleetCallAsync(invitee.Front, invitee.Open, inviteeOid, 4,
                "Invite", new PyLong(thirdInviteeId), PyNone.Instance, PyNone.Instance, PyNone.Instance);
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(defaultInvitation.Payload)));
            Assert.Equal("OnFleetInvite", ReadFleetNotification(Assert.IsType<Send>(await thirdInvitee.Front.ReceiveAsync())).Name);
            var thirdAccepted = await BoundFleetCallAsync(thirdInvitee.Front, thirdInvitee.Open, thirdInviteeOid, 2,
                "AcceptInvite", PyNone.Instance);
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(thirdAccepted.Payload)));
            var defaultMember = await store.MemberForCharacterAsync(thirdInviteeId);
            Assert.NotNull(defaultMember);
            Assert.Equal(wing.WingId, defaultMember!.WingId);
            Assert.Equal(squadId, defaultMember.SquadId);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_invitation_acceptance_rejects_duplicate_wing_and_squad_commanders_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var wingInviteeId = bossId + 1;
        var squadInviteeId = bossId + 2;
        var existingWingCommanderId = bossId + 3;
        var existingSquadCommanderId = bossId + 4;
        var fleetId = await store.CreateAsync(bossId);
        var initial = await store.SnapshotAsync(fleetId);
        Assert.NotNull(initial);
        var wing = Assert.Single(initial!.Wings);
        var squadId = Assert.Single(wing.Squads).SquadId;
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, bossId, existingWingCommanderId,
                wing.WingId, FleetStore.NoUnit, FleetStore.WingCommander));
            Assert.NotNull(await store.JoinAsync(fleetId, existingWingCommanderId));
            Assert.True(await store.InviteAsync(fleetId, bossId, existingSquadCommanderId,
                wing.WingId, squadId, FleetStore.SquadCommander));
            Assert.NotNull(await store.JoinAsync(fleetId, existingSquadCommanderId));
            Assert.True(await store.InviteAsync(fleetId, bossId, wingInviteeId,
                wing.WingId, FleetStore.NoUnit, FleetStore.WingCommander));
            Assert.True(await store.InviteAsync(fleetId, bossId, squadInviteeId,
                wing.WingId, squadId, FleetStore.SquadCommander));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? wingInviteeId : squadInviteeId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var wingInvitee = await OpenFleetFrontAsync(wingInviteeId, 67);
            var squadInvitee = await OpenFleetFrontAsync(squadInviteeId, 68);
            var wingOid = await BindFleetAsync(wingInvitee.Front, wingInvitee.Open, fleetId, 1);
            var squadOid = await BindFleetAsync(squadInvitee.Front, squadInvitee.Open, fleetId, 1);

            var wingAccept = await BoundFleetCallAsync(wingInvitee.Front, wingInvitee.Open, wingOid, 2,
                "AcceptInvite", PyNone.Instance);
            Assert.Equal("FleetError", FleetUserError(wingAccept));
            Assert.Null(await store.MemberForCharacterAsync(wingInviteeId));
            Assert.Equal(1, await InvitationCountAsync(source, schema, fleetId, wingInviteeId));

            var squadAccept = await BoundFleetCallAsync(squadInvitee.Front, squadInvitee.Open, squadOid, 2,
                "AcceptInvite", PyNone.Instance);
            Assert.Equal("FleetError", FleetUserError(squadAccept));
            Assert.Null(await store.MemberForCharacterAsync(squadInviteeId));
            Assert.Equal(1, await InvitationCountAsync(source, schema, fleetId, squadInviteeId));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, bossId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_boss_can_invite_a_pilot_to_an_empty_fleet_commander_role_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var inviteeId = bossId + 1;
        var fleetId = await store.CreateAsync(bossId);
        var initial = await store.SnapshotAsync(fleetId);
        Assert.NotNull(initial);
        var wing = Assert.Single(initial!.Wings);
        var squadId = Assert.Single(wing.Squads).SquadId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var moveBoss = connection.CreateCommand())
        {
            moveBoss.CommandText = $"UPDATE {schema}.fleet_members SET wing_id = $1, squad_id = $2, role = $3 WHERE fleet_id = $4 AND character_id = $5";
            moveBoss.Parameters.AddWithValue(wing.WingId);
            moveBoss.Parameters.AddWithValue(squadId);
            moveBoss.Parameters.AddWithValue(FleetStore.Member);
            moveBoss.Parameters.AddWithValue(fleetId);
            moveBoss.Parameters.AddWithValue(bossId);
            Assert.Equal(1, await moveBoss.ExecuteNonQueryAsync());
        }
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? bossId : inviteeId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var boss = await OpenFleetFrontAsync(bossId, 69);
            var invitee = await OpenFleetFrontAsync(inviteeId, 70);
            var bossOid = await BindFleetAsync(boss.Front, boss.Open, fleetId, 1);
            var inviteeOid = await BindFleetAsync(invitee.Front, invitee.Open, fleetId, 1);

            var invite = await BoundFleetCallAsync(boss.Front, boss.Open, bossOid, 2,
                "MassInvite", new PyList([new PyLong(inviteeId)]), PyNone.Instance, PyNone.Instance,
                new PyInt(FleetStore.Leader));
            Assert.Equal(new PyList([new PyLong(inviteeId)]), Result(MarshalCodec.Decode(invite.Payload)));
            Assert.Equal("OnFleetInvite", ReadFleetNotification(Assert.IsType<Send>(await invitee.Front.ReceiveAsync())).Name);

            var accept = await BoundFleetCallAsync(invitee.Front, invitee.Open, inviteeOid, 2,
                "AcceptInvite", PyNone.Instance);
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(accept.Payload)));
            var bossState = await store.MemberForCharacterAsync(bossId);
            Assert.NotNull(bossState);
            Assert.Equal(FleetStore.Member, bossState!.Role);
            Assert.True(FleetStore.HasBossJob(bossState.Job));
            var newCommander = await store.MemberForCharacterAsync(inviteeId);
            Assert.NotNull(newCommander);
            Assert.Equal((FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Leader),
                (newCommander!.WingId, newCommander.SquadId, newCommander.Role));
            Assert.False(FleetStore.HasBossJob(newCommander.Job));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, bossId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_accepting_an_invite_after_joining_another_fleet_rejects_with_reason_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var inviterId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var inviteeId = inviterId + 1;
        var otherBossId = inviterId + 2;
        var invitedFleetId = await store.CreateAsync(inviterId);
        var otherFleetId = await store.CreateAsync(otherBossId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Assert.True(await store.InviteAsync(invitedFleetId, inviterId, inviteeId,
                FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));
            Assert.True(await store.InviteAsync(otherFleetId, otherBossId, inviteeId,
                FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(otherFleetId, inviteeId));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? inviterId : inviteeId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    if (characterId == inviteeId)
                    {
                        session.Set("fleetid", new PyLong(otherFleetId));
                        session.Set("fleetrole", new PyInt(FleetStore.Member));
                    }
                    connected[characterId] = session;
                });

            var inviter = await OpenFleetFrontAsync(inviterId, 71);
            var invitee = await OpenFleetFrontAsync(inviteeId, 72);
            _ = await BindFleetAsync(inviter.Front, inviter.Open, invitedFleetId, 1);
            var inviteeOid = await BindFleetAsync(invitee.Front, invitee.Open, invitedFleetId, 1);

            var accept = await BoundFleetCallAsync(invitee.Front, invitee.Open, inviteeOid, 2,
                "AcceptInvite", PyNone.Instance);
            Assert.Equal("FleetError", FleetUserError(accept));
            Assert.Equal(otherFleetId, await store.FleetIdForCharacterAsync(inviteeId));
            Assert.Equal(0, await InvitationCountAsync(source, schema, invitedFleetId, inviteeId));
            var rejection = ReadFleetNotification(Assert.IsType<Send>(await inviter.Front.ReceiveAsync()));
            Assert.Equal("OnFleetJoinReject", rejection.Name);
            Assert.Equal(new PyLong(inviteeId), rejection.Args[0]);
            Assert.Equal(new PyInt(2), rejection.Args[1]);
        }
        finally
        {
            if (invitedFleetId > 0) await store.DisbandAsync(invitedFleetId, inviterId);
            if (otherFleetId > 0) await store.DisbandAsync(otherFleetId, otherBossId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_declining_an_invite_notifies_the_inviter_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var inviterId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var inviteeId = inviterId + 1;
        var fleetId = await store.CreateAsync(inviterId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, inviterId, inviteeId,
                FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? inviterId : inviteeId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var inviter = await OpenFleetFrontAsync(inviterId, 73);
            var invitee = await OpenFleetFrontAsync(inviteeId, 74);
            _ = await BindFleetAsync(inviter.Front, inviter.Open, fleetId, 1);
            var inviteeOid = await BindFleetAsync(invitee.Front, invitee.Open, fleetId, 1);

            var decline = await BoundFleetCallAsync(invitee.Front, invitee.Open, inviteeOid, 2, "RejectInvite");
            Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(decline.Payload)));
            Assert.Equal(0, await InvitationCountAsync(source, schema, fleetId, inviteeId));
            var rejection = ReadFleetNotification(Assert.IsType<Send>(await inviter.Front.ReceiveAsync()));
            Assert.Equal("OnFleetJoinReject", rejection.Name);
            Assert.Equal(new PyLong(inviteeId), rejection.Args[0]);
            Assert.Equal(PyNone.Instance, rejection.Args[1]);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, inviterId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_squad_commander_can_move_a_member_into_their_squad_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var commanderId = leaderId + 1;
        var memberId = leaderId + 2;
        var fleetId = await store.CreateAsync(leaderId);
        var created = await store.SnapshotAsync(fleetId);
        Assert.NotNull(created);
        var wing = Assert.Single(created!.Wings);
        var wingId = wing.WingId;
        var squadId = Assert.Single(wing.Squads).SquadId;
        var emptySquadId = await store.CreateSquadAsync(fleetId, leaderId, wingId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, leaderId, commanderId, wingId, squadId, FleetStore.SquadCommander));
            Assert.NotNull(await store.JoinAsync(fleetId, commanderId));
            Assert.True(await store.InviteAsync(fleetId, leaderId, memberId, FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, memberId));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) switch
                    {
                        1 => commanderId,
                        _ => memberId
                    };
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var commander = await OpenFleetFrontAsync(commanderId, 51);
            var member = await OpenFleetFrontAsync(memberId, 52);
            var commanderOid = await BindFleetAsync(commander.Front, commander.Open, fleetId, 1);
            _ = await BindFleetAsync(member.Front, member.Open, fleetId, 1);

            var reply = await BoundFleetCallAsync(commander.Front, commander.Open, commanderOid, 2,
                "MoveMember", new PyLong(memberId), new PyLong(wingId), new PyLong(squadId), new PyInt(FleetStore.Member));
            Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(reply.Payload)));

            var moved = await store.MemberForCharacterAsync(memberId);
            Assert.NotNull(moved);
            Assert.Equal(wingId, moved!.WingId);
            Assert.Equal(squadId, moved.SquadId);
            Assert.Equal(FleetStore.Member, moved.Role);
            var memberNotifications = new[]
            {
                ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync())),
                ReadFleetNotification(Assert.IsType<Send>(await member.Front.ReceiveAsync()))
            };
            Assert.Equal(["OnFleetMemberChanged", "OnFleetMove"], memberNotifications.Select(item => item.Name));

            var deleteEmptySquad = await BoundFleetCallAsync(commander.Front, commander.Open, commanderOid, 3,
                "DeleteSquad", new PyLong(emptySquadId));
            Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(deleteEmptySquad.Payload)));
            var afterDelete = await store.SnapshotAsync(fleetId);
            Assert.NotNull(afterDelete);
            Assert.DoesNotContain(afterDelete!.Wings.Single().Squads, squad => squad.SquadId == emptySquadId);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    [PgFact]
    public async Task Fleet_cannot_delete_an_occupied_squad_or_wing_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = await CreateFleetSchemaAsync(source);
        var store = new FleetStore(source, schema);
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberId = leaderId + 1;
        var fleetId = await store.CreateAsync(leaderId);
        var created = await store.SnapshotAsync(fleetId);
        Assert.NotNull(created);
        var wingId = Assert.Single(created!.Wings).WingId;
        var squadId = await store.CreateSquadAsync(fleetId, leaderId, wingId);
        var connected = new Dictionary<long, ClientSession>();
        var sessionsOpened = 0;
        var service = new FleetObjectService(store, () => FixedNowMs,
            sessionForCharacter: characterId => connected.GetValueOrDefault(characterId));

        try
        {
            Assert.True(await store.InviteAsync(fleetId, leaderId, memberId, wingId, squadId, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, memberId));

            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var characterId = Interlocked.Increment(ref sessionsOpened) == 1 ? leaderId : memberId;
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    connected[characterId] = session;
                });

            var leader = await OpenFleetFrontAsync(leaderId, 53);
            _ = await OpenFleetFrontAsync(memberId, 54);
            var leaderOid = await BindFleetAsync(leader.Front, leader.Open, fleetId, 1);

            var deleteSquad = await BoundFleetCallAsync(leader.Front, leader.Open, leaderOid, 2,
                "DeleteSquad", new PyLong(squadId));
            Assert.Equal("FleetError", FleetUserError(deleteSquad));
            var afterSquadRefusal = await store.SnapshotAsync(fleetId);
            Assert.NotNull(afterSquadRefusal);
            Assert.Contains(afterSquadRefusal!.Wings.Single().Squads, squad => squad.SquadId == squadId);
            var squadMember = await store.MemberForCharacterAsync(memberId);
            Assert.NotNull(squadMember);
            Assert.Equal(wingId, squadMember!.WingId);
            Assert.Equal(squadId, squadMember.SquadId);

            var deleteWing = await BoundFleetCallAsync(leader.Front, leader.Open, leaderOid, 3,
                "DeleteWing", new PyLong(wingId));
            Assert.Equal("FleetError", FleetUserError(deleteWing));
            var afterWingRefusal = await store.SnapshotAsync(fleetId);
            Assert.NotNull(afterWingRefusal);
            Assert.Contains(afterWingRefusal!.Wings, wing => wing.WingId == wingId);
            var persistedMember = await store.MemberForCharacterAsync(memberId);
            Assert.NotNull(persistedMember);
            Assert.Equal(wingId, persistedMember!.WingId);
            Assert.Equal(squadId, persistedMember.SquadId);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
            await DropFleetSchemaAsync(source, schema);
        }
    }

    private async Task<(TestFront Front, SessionOpen Open)> OpenFleetFrontAsync(long characterId, uint clientId)
    {
        var front = await Handshaken(await ConnectAsync());
        var open = Open(1_790_646_377_000UL * 15 + clientId, clientId);
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        return (front, open);
    }

    private async Task<string> BindFleetAsync(TestFront front, SessionOpen open, long fleetId, long callId)
    {
        var kwargs = Eve.Services.Py.Dict(("machoVersion", new PyInt(1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId, "fleetObjectHandler", "MachoBindObject",
            new PyTuple([new PyLong(fleetId), PyNone.Instance]), kwargs))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
        var bound = Assert.IsType<PySubStruct>(result.Items[0]);
        var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        return Assert.IsType<string>(JsSemantics.StringValue(reference.Items[0]));
    }

    private static async Task<Send> BoundFleetCallAsync(
        TestFront front, SessionOpen open, string objectId, long callId, string method, params PyValue[] args)
    {
        var kwargs = Eve.Services.Py.Dict(("machoVersion", new PyInt(1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, callId, objectId, method, new PyTuple(args), kwargs))));
        while (true)
        {
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var packet = MachoPacket.Decode(MarshalCodec.Decode(reply.Payload));
            Assert.NotNull(packet);
            if (packet!.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse)
                return reply;
        }
    }

    private static async Task<(Send Reply, List<(string Name, string IdType, PyValue[] Args)> Notifications)>
        BoundFleetCallWithNotificationsAsync(
            TestFront front, SessionOpen open, string objectId, long callId, string method, params PyValue[] args)
    {
        var kwargs = Eve.Services.Py.Dict(("machoVersion", new PyInt(1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, callId, objectId, method, new PyTuple(args), kwargs))));
        var notifications = new List<(string Name, string IdType, PyValue[] Args)>();
        while (true)
        {
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var packet = MachoPacket.Decode(MarshalCodec.Decode(reply.Payload));
            Assert.NotNull(packet);
            if (packet!.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse)
                return (reply, notifications);
            notifications.Add(ReadFleetNotification(reply));
        }
    }

    private static string FleetUserError(Send reply)
    {
        var packet = MachoPacket.Decode(MarshalCodec.Decode(reply.Payload));
        Assert.NotNull(packet);
        Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
        var state = Assert.IsType<PyTuple>(packet.Payload).Items;
        Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(state[1]).Value);
        var wrapped = Assert.IsType<PyTuple>(state[2]).Items;
        var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
        var header = Assert.IsType<PyTuple>(userError.Header).Items;
        Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
        var args = Assert.IsType<PyTuple>(header[1]).Items;
        return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
    }

    private static async Task<long> InvitationCountAsync(NpgsqlDataSource source, string schema,
        long fleetId, long characterId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2";
        command.Parameters.AddWithValue(fleetId);
        command.Parameters.AddWithValue(characterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> CreateFleetSchemaAsync(NpgsqlDataSource source)
    {
        var schema = $"ft_{Guid.NewGuid():N}";
        await Migrations.ApplyAsync(source, schema,
        [
            new Migration("001_fleet_authority_test", """
                CREATE TABLE fleets (
                    fleet_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    leader_character_id bigint NOT NULL,
                    date_created_filetime bigint NOT NULL DEFAULT 0,
                    auto_join_squad_id bigint,
                    max_member_count smallint NOT NULL DEFAULT 256,
                    is_free_move boolean NOT NULL DEFAULT false,
                    advert_active boolean NOT NULL DEFAULT false,
                    advert_join_limit integer NOT NULL DEFAULT 256,
                    motd text NOT NULL DEFAULT '',
                    fleet_name text NOT NULL DEFAULT '',
                    description text NOT NULL DEFAULT '',
                    invite_scope integer NOT NULL DEFAULT 0,
                    join_needs_approval boolean NOT NULL DEFAULT false,
                    hide_info boolean NOT NULL DEFAULT false,
                    public_min_standing double precision NOT NULL DEFAULT -10
                );
                CREATE TABLE fleet_members (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    character_id bigint NOT NULL UNIQUE,
                    wing_id bigint NOT NULL DEFAULT -1,
                    squad_id bigint NOT NULL DEFAULT -1,
                    role smallint NOT NULL CHECK (role BETWEEN 1 AND 4),
                    job smallint NOT NULL DEFAULT 0,
                    accepts_fleet_warp boolean NOT NULL DEFAULT true,
                    accepts_conduit_jumps boolean NOT NULL DEFAULT true,
                    accepts_fleet_regroups boolean NOT NULL DEFAULT true,
                    ship_type_id bigint,
                    joined_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (fleet_id, character_id)
                );
                CREATE TABLE fleet_wings (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    wing_id bigint GENERATED BY DEFAULT AS IDENTITY,
                    name text NOT NULL DEFAULT '',
                    position integer NOT NULL,
                    PRIMARY KEY (fleet_id, wing_id)
                );
                CREATE TABLE fleet_squads (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    squad_id bigint GENERATED BY DEFAULT AS IDENTITY,
                    wing_id bigint NOT NULL,
                    name text NOT NULL DEFAULT '',
                    position integer NOT NULL,
                    PRIMARY KEY (fleet_id, squad_id),
                    FOREIGN KEY (fleet_id, wing_id) REFERENCES fleet_wings(fleet_id, wing_id) ON DELETE CASCADE
                );
                CREATE TABLE fleet_invitations (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    character_id bigint NOT NULL,
                    inviter_id bigint NOT NULL,
                    wing_id bigint NOT NULL DEFAULT -1,
                    squad_id bigint NOT NULL DEFAULT -1,
                    role smallint NOT NULL CHECK (role BETWEEN 1 AND 4),
                    created_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (fleet_id, character_id)
                );
                CREATE TABLE fleet_join_requests (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    character_id bigint NOT NULL,
                    corporation_id bigint,
                    alliance_id bigint,
                    war_faction_id bigint,
                    security_status double precision NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (fleet_id, character_id)
                );
                """)
        ]);
        return schema;
    }

    private static async Task DropFleetSchemaAsync(NpgsqlDataSource source, string schema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Eve.Data.Sql.QuoteSchema(schema)} CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}
