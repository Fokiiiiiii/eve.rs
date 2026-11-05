using Eve.Marshal;
using Eve.Npc;
using Eve.Space;
using Eve.Crimewatch;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class DynamicBountyHostTests
{
    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (rig, character, session);
    }

    [Fact]
    public void Evejs_equilibrium_scenario_is_one_million_times_one_point_five_before_bank_shares()
    {
        Assert.Equal(1_500_000m, NpcBounty.AmountOf(1_000_000m, 1m, 1.5m));
    }

    [PgFact]
    public async Task Npc_bounty_uses_equilibrium_output_before_ess_split_and_commits_input_before_notice()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var system = rig.Systems.Systems.First(system =>
            DynamicBountyRules.IsEligible(new DynamicBountySystem(system.Id, system.RegionId, system.Security)));
        Assert.Null(await rig.Space.RelocateAsync(session, system.Id, null, null, CancellationToken.None));

        var dbs = new PgDynamicBountyStore(rig.Source,
            rig.Systems.Systems.Select(system => new DynamicBountySystem(system.Id, system.RegionId, system.Security)),
            rig.BountySchema);
        var essSchema = $"ess_{Guid.NewGuid():N}"[..20];
        rig.KeepSchemas(essSchema);
        await PgEssStore.MigrateAsync(rig.Source, essSchema);
        var ess = new PgEssStore(rig.Source, rig.WalletTransactions, essSchema, rig.Log.Add);
        await ess.RegisterAsync(system.Id, 1_030_000_000_001, null, rig.Now);
        rig.Space.Ess = ess;
        rig.Space.DynamicBounties = dbs;
        rig.Space.RecordDynamicBountyPaidInTransactionAsync = (connection, transaction, systemId, gross, now, token) =>
            dbs.RecordPaidAsync(connection, transaction, systemId, gross, now, token);

        var spawn = await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 1, CancellationToken.None);
        Assert.Null(spawn.Error);
        var npc = rig.Sim.Heard.OfType<SimSpawn>().Last();
        rig.Sim.Say(new SimDestroyed(1UL << 62, npc.Ship.ShipId, 4_300_000_000_000_001,
            [0, 0, 0], [1, 0, 0], 47, character.ShipId));
        await Rig.UntilAsync(() => Events.Of(session, clear: false).Any(e => e.Name == "OnBountyAddedToPayout")
            && rig.Bounties.PendingAsync().Result.Count == 1);

        var notice = Events.Of(session).Single(e => e.Name == "OnBountyAddedToPayout");
        var fields = ((PyDict)((PyObject)notice.Args.Single()).Args).Entries
            .ToDictionary(pair => System.Text.Encoding.UTF8.GetString(((PyBytes)pair.Key).Value.Span), pair => pair.Value);
        var bucket = Assert.Single(await rig.Bounties.PendingAsync());
        Assert.Equal(12_375, Events.Number(fields["amount"]));
        Assert.Equal(PyBool.True, fields["isModified"]);
        Assert.Equal(12_375m, bucket.Amount);
        Assert.Equal(system.Id, bucket.SolarSystemId);
        Assert.Equal((9_000m, 1_125m), ((await ess.ReadAsync(system.Id))!.MainValue, (await ess.ReadAsync(system.Id))!.ReserveValue));

        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT paid_cents FROM {rig.BountySchema}.dynamic_bounty_periods WHERE solar_system_id = $1",
            connection);
        command.Parameters.AddWithValue(system.Id);
        Assert.Equal(2_250_000L, (long)(await command.ExecuteScalarAsync())!);
    }

    [PgFact]
    public async Task Ess_bounty_and_dynamic_input_roll_back_with_the_pilot_bucket()
    {
        var (rig, character, _) = await InSpaceAsync();
        await using var _ = rig;
        var system = rig.Systems.Systems.First(system =>
            DynamicBountyRules.IsEligible(new DynamicBountySystem(system.Id, system.RegionId, system.Security)));
        var dbs = new PgDynamicBountyStore(rig.Source,
            rig.Systems.Systems.Select(system => new DynamicBountySystem(system.Id, system.RegionId, system.Security)),
            rig.BountySchema);
        var essSchema = $"ess_{Guid.NewGuid():N}"[..20];
        rig.KeepSchemas(essSchema);
        await PgEssStore.MigrateAsync(rig.Source, essSchema);
        var ess = new PgEssStore(rig.Source, rig.WalletTransactions, essSchema, rig.Log.Add);
        await ess.RegisterAsync(system.Id, 1_030_000_000_001, null, rig.Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Bounties.RecordAsync(
            new BountyKill(character.CharacterId, system.Id, 23245, 4_300_000_000_000_002, 12_375m, IsModified: true), rig.Now,
            inTransaction: async (connection, transaction, token) =>
            {
                await ess.CreditBountyShareAsync(connection, transaction, system.Id, 9_000m, 1_125m, token);
                await dbs.RecordPaidAsync(connection, transaction, system.Id, 22_500m, rig.Now, token);
                throw new InvalidOperationException("force transaction rollback");
            }));

        Assert.Empty(await rig.Bounties.PendingAsync());
        var essState = (await ess.ReadAsync(system.Id))!;
        Assert.Equal((0m, 0m), (essState.MainValue, essState.ReserveValue));
        await using var check = await rig.Source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand(
            $"SELECT (SELECT COUNT(*) FROM {rig.BountySchema}.dynamic_bounty_systems), (SELECT COUNT(*) FROM {rig.BountySchema}.dynamic_bounty_periods)",
            check);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((0L, 0L), (reader.GetInt64(0), reader.GetInt64(1)));
    }

    [PgFact]
    public async Task A_player_ship_loss_commits_its_system_input_before_the_loss_event()
    {
        var (rig, victim, session) = await InSpaceAsync();
        await using var _ = rig;
        var system = rig.Systems.Systems.First(system =>
            DynamicBountyRules.IsEligible(new DynamicBountySystem(system.Id, system.RegionId, system.Security)));
        Assert.Null(await rig.Space.RelocateAsync(session, system.Id, null, null, CancellationToken.None));
        var killer = await rig.NewCharacterAsync("DBS Loss Killer", 2);
        var killerSession = await rig.SessionAsync(killer, 2);
        var oid = await rig.BindShipAsync(killerSession);
        await rig.ShipCallAsync(killerSession, "Undock", oid, new PyLong(killerSession.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(killerSession);
        Assert.Null(await rig.Space.RelocateAsync(killerSession, system.Id, null, null, CancellationToken.None));

        var dbs = new PgDynamicBountyStore(rig.Source,
            rig.Systems.Systems.Select(system => new DynamicBountySystem(system.Id, system.RegionId, system.Security)),
            rig.BountySchema);
        rig.Space.DynamicBounties = dbs;
        rig.Space.RecordDynamicBountyLossInTransactionAsync = async (connection, transaction, loss, token) =>
        {
            Assert.Equal(ShipLossCause.Player, loss.Cause);
            Assert.Equal(killer.CharacterId, loss.KillerCharacterId);
            await dbs.RecordLossAsync(connection, transaction, loss.SolarSystemId, loss.ShipId, 500_000_000m, rig.Now, token);
        };
        var losses = new List<ShipLoss>();
        rig.Space.ShipLost += losses.Add;

        rig.Sim.Say(new SimDestroyed((ulong)session.SessionId, victim.ShipId, 0, [0, 0, 0], [1, 0, 0], 47,
            killerSession.GetInt("shipid")!.Value, null));
        await Rig.UntilAsync(() => losses.Count == 1);

        await using var check = await rig.Source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand(
            $"SELECT loss_cents FROM {rig.BountySchema}.dynamic_bounty_losses WHERE loss_id = $1",
            check);
        query.Parameters.AddWithValue(victim.ShipId);
        Assert.Equal(50_000_000_000L, (long)(await query.ExecuteScalarAsync())!);
        Assert.Equal(1.61, (await dbs.ReadAsync(system.Id, rig.Now + DynamicBountyRules.TimeIntervalMs)).Output);
    }

    [PgFact]
    public async Task Dynamic_state_notification_is_emitted_only_after_the_daily_update_commits()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        Events.Of(session);
        var system = rig.Systems.Systems.First(system =>
            DynamicBountyRules.IsEligible(new DynamicBountySystem(system.Id, system.RegionId, system.Security)));
        var dbs = new PgDynamicBountyStore(rig.Source,
            rig.Systems.Systems.Select(system => new DynamicBountySystem(system.Id, system.RegionId, system.Security)),
            rig.BountySchema);
        DynamicBountyJobs.Register(rig.Scheduler, dbs,
            systemId => systemId == system.Id ? [session] : [], _ => { });
        var started = rig.Now;

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var input = await dbs.RecordPaidAsync(connection, transaction, system.Id, 200_000_000m, started, CancellationToken.None);
            await DynamicBountyJobs.ScheduleProcessAsync(rig.Scheduler, connection, transaction,
                system.Id, input!.NextAtMs, CancellationToken.None);
            rig.Now = input.NextAtMs;

            Assert.Equal(0, await rig.Scheduler.RunDueAsync());
            Assert.Empty(Events.Of(session));
            await transaction.CommitAsync();
        }

        Assert.Equal(2, await rig.Scheduler.RunDueAsync());
        var changed = Assert.Single(Events.Of(session), e => e.Name == "OnSystemDynamicBountyStateChanged");
        Assert.Equal("clientID", changed.IdType);
        Assert.Equal(new PyInt(system.Id), changed.Args[0]);
        Assert.Equal(new PyReal(1.476666666667), changed.Args[1]);
        Assert.Equal(1.476666666667, (await dbs.ReadAsync(system.Id, rig.Now)).Output);
    }
}
