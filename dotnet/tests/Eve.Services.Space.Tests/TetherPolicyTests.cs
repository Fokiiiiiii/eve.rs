using Eve.Crimewatch;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;
using Xunit;

namespace Eve.Services.Space.Tests;

public sealed class TetherPolicyTests
{
    private const long Structure = 1_040_000_000_001;

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> EnterAsync()
    {
        var rig = await Rig.CreateAsync(protection: TimeSpan.FromHours(1), jumpCloak: TimeSpan.FromMinutes(1));
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(CombatState.Idle);
        rig.Space.ReadTetherCandidatesAsync = (_, _) => Task.FromResult<IReadOnlyList<SimTetherCandidate>>([new(Structure, 10_000)]);
        rig.Space.ReadTetherGeometryAsync = (_, _) => Task.FromResult<TetherGeometry?>(new(45_000, 10_000));
        rig.Space.RepairTetheredItemsAsync = (_, _, _) => Task.FromResult(false);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False);
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        await rig.Space.QuiesceAsync();
        await rig.Space.RefreshTetherPolicyAsync(session);
        Events.Of(session);
        return (rig, character, session);
    }

    private static SimTetherPolicy LastPolicy(Rig rig, ClientSession session) =>
        rig.Sim.Heard.OfType<SimTetherPolicy>().Last(p => p.SessionId == (ulong)session.SessionId);

    [PgFact]
    public async Task Policy_uses_remaining_weapon_time_replaces_rights_and_fails_closed_on_reader_failure()
    {
        var (rig, _, session) = await EnterAsync();
        await using var dispose = rig;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(CombatState.Idle with { WeaponsUntilMs = rig.Now + 9_500 });
        await rig.Space.RefreshTetherPolicyAsync(session);
        var policy = LastPolicy(rig, session);
        Assert.Equal(9_500UL, policy.BlockedForMs);
        Assert.Equal(3_000UL, policy.LeaseMs);
        Assert.Equal(new SimTetherCandidate(Structure, 10_000), Assert.Single(policy.Candidates));

        rig.Space.ReadTetherCandidatesAsync = (_, _) => Task.FromResult<IReadOnlyList<SimTetherCandidate>>([]);
        await rig.Space.RefreshTetherPolicyAsync(session);
        Assert.Empty(LastPolicy(rig, session).Candidates);

        rig.Space.ReadTetherCandidatesAsync = (_, _) => throw new InvalidOperationException("rights unavailable");
        await rig.Space.RefreshTetherPolicyAsync(session);
        Assert.Empty(LastPolicy(rig, session).Candidates);
        Assert.Contains(rig.Log, message => message.Contains("was withdrawn"));
    }

    [PgFact]
    public async Task Active_session_renews_the_lease_and_leaving_withdraws_the_entire_list()
    {
        var (rig, character, session) = await EnterAsync();
        await using var dispose = rig;
        await rig.Space.QuiesceAsync();
        var count = rig.Sim.Heard.OfType<SimTetherPolicy>().Count();
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimTetherPolicy>().Count() > count);
        Assert.Equal(3_000UL, LastPolicy(rig, session).LeaseMs);
        Assert.Single(LastPolicy(rig, session).Candidates);

        await rig.Space.OnCharacterLeavingAsync(session, character.CharacterId, CancellationToken.None);
        Assert.Empty(LastPolicy(rig, session).Candidates);
    }

    [PgFact]
    public async Task A_simulated_engagement_without_a_current_host_permission_cannot_repair_or_set_tether_state()
    {
        var (rig, _, session) = await EnterAsync();
        await using var dispose = rig;
        var repairs = 0;
        rig.Space.RepairTetheredItemsAsync = (_, _, _) => { Interlocked.Increment(ref repairs); return Task.FromResult(false); };
        rig.Space.ReadTetherCandidatesAsync = (_, _) => Task.FromResult<IReadOnlyList<SimTetherCandidate>>([]);
        await rig.Space.RefreshTetherPolicyAsync(session);
        rig.Sim.Say(new SimTetherChanged((ulong)session.SessionId, 0, Structure, true));
        await rig.Space.QuiesceAsync();
        Assert.False(rig.Space.IsTethered(session));
        Assert.Equal(0, repairs);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnSpecialFX");
    }

    [PgTheory]
    [InlineData("rights")]
    [InlineData("weapons")]
    [InlineData("expiry")]
    [InlineData("ship")]
    [InlineData("pilot")]
    public async Task Authorization_changes_during_geometry_wait_cancel_engagement_and_queued_repairs(string change)
    {
        var (rig, character, session) = await EnterAsync();
        await using var dispose = rig;
        var id = (ulong)session.SessionId;
        rig.Sim.Say(new SimNotify(id, "SetState", "clientID", MarshalCodec.Encode(Py.Tuple())));
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, false));
        await rig.Space.QuiesceAsync();
        rig.Sim.Say(new SimTetherChanged(id, Structure, 0, false));
        await rig.Space.QuiesceAsync();
        Events.Of(session);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var geometry = new TaskCompletionSource<TetherGeometry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairs = 0;
        rig.Space.ReadTetherGeometryAsync = async (_, _) =>
        {
            entered.TrySetResult();
            return await geometry.Task;
        };
        rig.Space.RepairTetheredItemsAsync = (_, shipId, _) =>
        {
            Assert.Equal(character.ShipId, shipId);
            Interlocked.Increment(ref repairs);
            return Task.FromResult(false);
        };
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, true));
        Task? leaving = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Renew the policy while the engagement is waiting for its geometry.
            await rig.Space.RefreshTetherPolicyAsync(session);
            switch (change)
            {
                case "rights":
                    rig.Space.ReadTetherCandidatesAsync = (_, _) => Task.FromResult<IReadOnlyList<SimTetherCandidate>>([]);
                    await rig.Space.RefreshTetherPolicyAsync(session);
                    Assert.Empty(LastPolicy(rig, session).Candidates);
                    break;
                case "weapons":
                    rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(CombatState.Idle with { WeaponsUntilMs = rig.Now + 9_500 });
                    await rig.Space.RefreshTetherPolicyAsync(session);
                    break;
                case "expiry":
                    rig.Space.ReadTetherCandidatesAsync = (_, _) => Task.FromResult<IReadOnlyList<SimTetherCandidate>>([]);
                    rig.Now += (long)SpaceSessions.TetherPolicyLeaseMs + 1;
                    break;
                case "ship":
                    session.Set("shipid", new PyLong(character.ShipId + 100));
                    break;
                default:
                    leaving = rig.Space.OnCharacterLeavingAsync(session, character.CharacterId, CancellationToken.None).AsTask();
                    break;
            }
        }
        finally { geometry.TrySetResult(new TetherGeometry(45_000, 10_000)); }
        if (leaving is not null) await leaving.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Space.QuiesceAsync();
        Assert.Equal(0, repairs);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnSpecialFX" && Events.Number(e.Args[7]) == 1);
    }

    [PgFact]
    public async Task Withdrawal_during_a_deferred_provider_wait_cannot_start_repair_fx()
    {
        var (rig, character, session) = await EnterAsync();
        await using var dispose = rig;
        var id = (ulong)session.SessionId;
        rig.Sim.Say(new SimNotify(id, "SetState", "clientID", MarshalCodec.Encode(Py.Tuple())));
        Events.Of(session);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairs = 0;
        rig.Space.RepairTetheredItemsAsync = async (_, shipId, _) =>
        {
            Assert.Equal(character.ShipId, shipId);
            Interlocked.Increment(ref repairs);
            entered.TrySetResult();
            return await repair.Task;
        };
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, false));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rig.Space.ReadTetherCandidatesAsync = (_, _) => Task.FromResult<IReadOnlyList<SimTetherCandidate>>([]);
            await rig.Space.RefreshTetherPolicyAsync(session);
        }
        finally { repair.TrySetResult(true); }
        await rig.Space.QuiesceAsync();
        Assert.Equal(1, repairs);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnSpecialFX" && Events.Number(e.Args[7]) == 1);
    }

    [PgFact]
    public async Task Tether_fx_has_the_client_arguments_repair_before_link_and_replays_after_ballpark_state()
    {
        var (rig, character, session) = await EnterAsync();
        await using var dispose = rig;
        var id = (ulong)session.SessionId;
        long repairedShip = 0;
        rig.Space.RepairTetheredItemsAsync = (_, expectedShipId, _) =>
        {
            repairedShip = expectedShipId;
            return Task.FromResult(false);
        };
        rig.Sim.Say(new SimNotify(id, "SetState", "clientID", MarshalCodec.Encode(Py.Tuple())));
        Events.Of(session);
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, true));
        await rig.Space.QuiesceAsync();
        Assert.True(rig.Space.IsTethered(session));
        Assert.Equal(character.ShipId, repairedShip);
        var fx = Events.Of(session).Where(e => e.Name == "OnSpecialFX").ToArray();
        Assert.Equal(["effects.TetheringRepair", "effects.Tethering"], fx.Select(e => JsSemantics.StringValue(e.Args[5])!).ToArray());
        Assert.All(fx, e =>
        {
            Assert.Equal("shipID", e.IdType);
            Assert.Equal(14, e.Args.Length);
            Assert.Equal(character.ShipId, Events.Number(e.Args[0]));
            Assert.IsType<PyNone>(e.Args[1]);
            Assert.IsType<PyNone>(e.Args[2]);
            Assert.Equal(1, Events.Number(e.Args[7]));
            Assert.Equal(1, Events.Number(e.Args[8]));
        });
        Assert.IsType<PyNone>(fx[0].Args[3]);
        Assert.Equal(10_000, Events.Number(fx[0].Args[9]));
        Assert.Equal(10_000, Events.Number(fx[0].Args[10]));
        Assert.Empty(Assert.IsType<PyDict>(fx[0].Args[13]).Entries);
        Assert.Equal(Structure, Events.Number(fx[1].Args[3]));
        Assert.Equal(86_400_000, Events.Number(fx[1].Args[9]));
        var graphic = Assert.IsType<PyDict>(fx[1].Args[13]);
        double Value(string key) => graphic.Entries.Single(e => JsSemantics.StringValue(e.Key) == key).Value switch
        { PyReal real => real.Value, PyInt integer => integer.Value, PyLong integer => integer.Value, _ => throw new InvalidOperationException() };
        Assert.Equal(45_000, Value("targetRadius"));
        Assert.Equal(55_000, Value("linkBarrier"));

        rig.Sim.Say(new SimNotify(id, "SetState", "clientID", MarshalCodec.Encode(Py.Tuple())));
        Assert.Equal(2, Events.Of(session).Count(e => e.Name == "OnSpecialFX"));
        rig.Sim.Say(new SimTetherChanged(id, Structure, 0, false));
        await rig.Space.QuiesceAsync();
        Assert.False(rig.Space.IsTethered(session));
        var stopped = Events.Of(session).Where(e => e.Name == "OnSpecialFX").ToArray();
        Assert.Equal(2, stopped.Length);
        Assert.All(stopped, e => { Assert.Equal(0, Events.Number(e.Args[7])); Assert.Equal(-1, Events.Number(e.Args[9])); });
    }

    [PgFact]
    public async Task Policy_refresh_and_duplicate_engagement_do_not_repeat_repair()
    {
        var (rig, _, session) = await EnterAsync();
        await using var dispose = rig;
        var id = (ulong)session.SessionId;
        var repairs = 0;
        rig.Space.RepairTetheredItemsAsync = (_, _, _) =>
        {
            Interlocked.Increment(ref repairs);
            return Task.FromResult(true);
        };
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, false));
        await rig.Space.QuiesceAsync();
        Assert.Equal(1, repairs);

        await rig.Space.RefreshTetherPolicyAsync(session);
        await rig.Space.QuiesceAsync();
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, false));
        await rig.Space.QuiesceAsync();
        Assert.Equal(1, repairs);

        rig.Sim.Say(new SimTetherChanged(id, Structure, 0, false));
        await rig.Space.QuiesceAsync();
        rig.Sim.Say(new SimTetherChanged(id, 0, Structure, false));
        await rig.Space.QuiesceAsync();
        Assert.Equal(2, repairs);
    }

    [PgFact]
    public async Task Fleet_compression_is_refused_before_mutating_a_tethered_pilots_cargo()
    {
        var (rig, character, session) = await EnterAsync();
        await using var dispose = rig;
        rig.Sim.Say(new SimTetherChanged((ulong)session.SessionId, 0, Structure, false));
        await rig.Space.QuiesceAsync();
        long itemId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            itemId = await rig.Items.CreateAsync(connection, transaction, new NewItem(1230, character.CharacterId, character.ShipId, ItemFlags.Cargo, 100));
            await transaction.CommitAsync();
        }
        var item = (await rig.Items.GetAsync(itemId))!;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Containers.CompressInSpaceAsync(session, item, character.ShipId, CancellationToken.None));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
        Assert.Equal(SpaceContainers.CompressTetheredNotice,
            JsSemantics.StringValue(values.Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value));
        Assert.Equal(100, (await rig.Items.GetAsync(itemId))!.Quantity);
    }

    [PgTheory]
    [InlineData("drive")]
    [InlineData("bridge")]
    [InlineData("conduit")]
    public async Task Cyno_delay_starts_after_protection_ends_and_an_early_command_updates_its_persisted_deadline(string how)
    {
        var (rig, character, session) = await EnterAsync();
        await using var dispose = rig;
        long persisted = 0;
        rig.Space.ReadTetherBlockUntilAsync = (_, _) => Task.FromResult(Interlocked.Read(ref persisted));
        rig.Space.WriteTetherBlockUntilAsync = (_, until, _) => { Interlocked.Exchange(ref persisted, until); return Task.CompletedTask; };
        const int destination = 30000142;
        var origin = rig.Space.SolarSystemOf(session)!.Value;
        rig.Sim.CynoJumpAnswer = new CommandReply(true, 0, null) { DestinationSystem = destination };
        switch (how)
        {
            case "drive": await rig.Space.CynoJumpAsync(session, character.ShipId, 42, destination, 0, 0, null, CancellationToken.None); break;
            case "bridge": await rig.Space.BridgeJumpAsync(session, character.ShipId, 43, 42, destination, null, CancellationToken.None); break;
            default: await rig.Space.ConduitJumpAsync(session, character.ShipId, 42, destination, [], null, CancellationToken.None); break;
        }
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, origin, 0, destination, 0));
        await rig.Space.QuiesceAsync();
        await rig.Space.RefreshTetherPolicyAsync(session);
        Assert.Equal(90_000UL, LastPolicy(rig, session).BlockedForMs);
        Assert.Equal(rig.Now + 90_000, persisted);

        rig.Now += 5_000;
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        await rig.Space.QuiesceAsync();
        await rig.Space.RefreshTetherPolicyAsync(session);
        Assert.Equal(rig.Now + 30_000, persisted);
        Assert.Equal(30_000UL, LastPolicy(rig, session).BlockedForMs);
    }
}
