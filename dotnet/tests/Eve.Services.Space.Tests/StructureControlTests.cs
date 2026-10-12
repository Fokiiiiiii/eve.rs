using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class StructureControlTests
{
    private const long Structure = 1_040_000_000_001;
    private const int System = 30_000_142;

    private static ClientSession Docked(Sink sink)
    {
        var session = new ClientSession(sink) { SessionId = 7, ClientId = 7, CharacterId = 20, UserId = 1 };
        session.Set("shipid", new PyLong(501));
        session.Set("structureid", new PyLong(Structure));
        session.Set("solarsystemid", new PyInt(System));
        session.Set("solarsystemid2", new PyInt(System));
        session.Set("locationid", new PyInt(System));
        session.Set("corpid", new PyInt(98_000_001));
        return session;
    }

    [Fact]
    public async Task Docked_control_uses_the_real_observer_wire_and_changes_only_shipid()
    {
        var sim = new FakeSim();
        using var space = new SpaceSessions(sim, null!, null!, null!, null!, () => 0);
        var sink = new Sink();
        var session = Docked(sink);
        var before = session.SnapshotAttributes();
        await space.SetStructureObserverAsync(session, System, Structure, 20, true);
        Assert.Empty(sink.Sent);
        Assert.Equal(501, session.GetInt("shipid"));
        Assert.Equal(new SimControlStructure(7, System, Structure, 20, true), Assert.Single(sim.Heard.OfType<SimControlStructure>()));
        space.ApplyStructureControlSession(session, System, Structure, 501, System, false);
        Assert.Equal(Structure, session.GetInt("shipid"));
        foreach (var (name, value) in before.Where(pair => pair.Key != "shipid")) Assert.Equal(value, session.Get(name));
        Assert.Equal(["$sessionchange", "OnShipCosmeticsChanged"], Sequence.Of(session));

        var count = sim.Heard.Count;
        space.Bind(session);
        space.UpdateState(session);
        Assert.Equal(count + 2, sim.Heard.Count);
        sim.Deliver(new SimNotify(7, "OnSlimItemChange", "clientID", MarshalCodec.Encode(Py.Tuple(new PyLong(Structure)))));
        Assert.Equal(["OnSlimItemChange"], Sequence.Of(session));
        await space.SetStructureObserverAsync(session, System, 0, 0, false);
        space.RestoreStructureControlSession(session, 501, System, false, System);
        Assert.Equal(501, session.GetInt("shipid"));
        Assert.Equal(Structure, session.GetInt("structureid"));
        Assert.False(space.IsStructureObserver(session));
        Assert.Equal(["$sessionchange", "OnShipCosmeticsChanged"], Sequence.Of(session));
    }

    [Fact]
    public async Task Docked_controller_queries_structure_health_without_a_personal_ship_in_space()
    {
        var sim = new FakeSim { ConditionAnswer = new SimCondition(0.2, 0.4, 0.6, 0.3) };
        using var space = new SpaceSessions(sim, null!, null!, null!, null!, () => 0);
        var session = Docked(new Sink());
        await space.SetStructureObserverAsync(session, System, Structure, 20, true);
        var condition = await space.ConditionAsync(session, CancellationToken.None);
        Assert.NotNull(condition);
        Assert.Equal(0.2, condition.Damage);
        Assert.Equal(0.6, condition.ShieldCharge);
        Assert.Contains(sim.Heard, message => message is SimCommand { Code: SimCommands.GetCondition });
    }

    [Fact]
    public async Task Refused_or_disconnected_observer_bind_changes_no_session_attributes()
    {
        var sim = new FakeSim { ControlAnswer = "STRUCTURE_TOO_FAR" };
        using var space = new SpaceSessions(sim, null!, null!, null!, null!, () => 0);
        var sink = new Sink();
        var session = Docked(sink);
        await Assert.ThrowsAsync<MachoWrappedException>(() => space.SetStructureObserverAsync(session, System, Structure, 20, true).AsTask());
        Assert.Equal(501, session.GetInt("shipid"));
        Assert.Empty(sink.Sent);
        Assert.False(space.IsStructureObserver(session));
        sim.IsConnected = false;
        await Assert.ThrowsAsync<SimUnavailableException>(() => space.SetStructureObserverAsync(session, System, Structure, 20, true).AsTask());
        Assert.False(space.IsStructureObserver(session));
    }

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> FlyingAsync()
    {
        var rig = await Rig.CreateAsync();
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        session.Set("corpid", new PyLong((await rig.Characters.GetAsync(character.CharacterId))!.CorporationId));
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        Events.Of(session);
        return (rig, character, session);
    }

    [PgFact]
    public async Task Boarding_and_returning_keep_the_personal_physical_ship_and_its_persisted_active_id()
    {
        var (rig, character, session) = await FlyingAsync();
        await using var _ = rig;
        var ship = session.GetInt("shipid")!.Value;
        var system = rig.Space.SolarSystemOf(session)!.Value;
        var before = await rig.Ships.LoadAsync(character.CharacterId);
        await rig.Space.SetStructureObserverAsync(session, system, Structure, character.CharacterId, true);
        rig.Space.ApplyStructureControlSession(session, system, Structure, ship, system, true);
        Assert.Equal(ship, rig.Space.ShipIdOf(session));
        Assert.Equal(ship, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(ship, (await rig.Ships.LoadAsync(character.CharacterId))!.Ship.Id);
        Assert.IsType<PyNone>(await rig.Space.EjectAsync(session, CancellationToken.None));
        Assert.Equal(ship, rig.Space.ShipIdOf(session));
        Assert.Equal(["$sessionchange", "OnShipCosmeticsChanged"], Sequence.Of(session));
        rig.Sim.Deliver(new SimNotify((ulong)session.SessionId, "OnDamageStateChange", "clientID",
            MarshalCodec.Encode(Py.Tuple(new PyLong(Structure), Py.Tuple(new PyReal(0.1), new PyReal(0.2), new PyReal(0.3))))));
        await rig.Space.QuiesceAsync();
        Assert.Equal(before!.Condition, (await rig.Ships.LoadAsync(character.CharacterId))!.Condition);
        await rig.Space.SetStructureObserverAsync(session, system, 0, 0, false);
        rig.Space.RestoreStructureControlSession(session, ship, system, true, system);
        Assert.Null(session.GetInt("structureid"));
        Assert.Equal(ship, session.GetInt("shipid"));
        Assert.Equal(ship, rig.Space.ShipIdOf(session));
        Assert.Equal(system, session.GetInt("locationid"));
    }

    [PgFact]
    public async Task None_return_creates_a_real_capsule_and_preserves_the_abandoned_personal_hull()
    {
        var (rig, character, session) = await FlyingAsync();
        await using var _ = rig;
        var ship = session.GetInt("shipid")!.Value;
        var system = rig.Space.SolarSystemOf(session)!.Value;
        await rig.Space.SetStructureObserverAsync(session, system, Structure, character.CharacterId, true);
        rig.Space.ApplyStructureControlSession(session, system, Structure, ship, system, true);
        await rig.Space.SetStructureObserverAsync(session, system, 0, 0, false);
        Assert.True(await rig.Space.EjectStructureControlToCapsuleAsync(session, CancellationToken.None));
        var capsule = (await rig.Ships.LoadAsync(character.CharacterId))!.Ship;
        Assert.Equal(CharacterShips.CapsuleTypeId, capsule.TypeId);
        Assert.Equal(capsule.Id, session.GetInt("shipid"));
        Assert.Equal(capsule.Id, rig.Space.ShipIdOf(session));
        Assert.Null(session.GetInt("structureid"));
        var abandoned = (await rig.Items.GetAsync(ship))!;
        Assert.Equal(system, abandoned.LocationId);
        Assert.Equal(CharacterShips.InSpaceFlag, abandoned.Flag);
        Assert.NotEqual(ship, capsule.Id);
        Assert.Contains(rig.Sim.Heard, message => message is SimRespawn);
    }


    [PgFact]
    public async Task A_boarded_hull_death_persists_capsule_and_none_eject_retries_without_duplicating_loss()
    {
        var (rig, character, session) = await FlyingAsync();
        await using var _ = rig;
        var ship = session.GetInt("shipid")!.Value;
        var system = rig.Space.SolarSystemOf(session)!.Value;
        long cargo;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            cargo = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(34, character.CharacterId, ship, CharacterShips.CargoFlag, 100, false));
            await transaction.CommitAsync();
        }
        await rig.Space.SetStructureObserverAsync(session, system, Structure, character.CharacterId, true);
        rig.Space.ApplyStructureControlSession(session, system, Structure, ship, system, true);

        var position = new double[] { 12_300, 45_600, 78_900 };
        var direction = new double[] { 0, 1, 0 };
        var destroyed = new SimDestroyed((1UL << 61) + 1, ship, 0, position, direction, 0, 0);
        rig.Sim.Deliver(destroyed);
        await rig.Space.QuiesceAsync();

        var capsule = (await rig.Ships.LoadAsync(character.CharacterId))!.Ship;
        Assert.Equal(CharacterShips.CapsuleTypeId, capsule.TypeId);
        Assert.Equal(capsule.Id, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Null(await rig.Items.GetAsync(ship));
        Assert.Null(await rig.Items.GetAsync(cargo));
        Assert.True(rig.Space.IsStructureObserver(session));
        Assert.Equal(Structure, session.GetInt("shipid"));
        Assert.Equal(capsule.Id, rig.Space.ShipIdOf(session));

        rig.Sim.Deliver(destroyed);
        await rig.Space.QuiesceAsync();
        Assert.Equal(capsule.Id, (await rig.Ships.LoadAsync(character.CharacterId))!.Ship.Id);
        Assert.Null(await rig.Items.GetAsync(ship));

        rig.Sim.RespawnAnswer = "CAPSULE_RESPAWN_REFUSED";
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Space.SetStructureObserverAsync(session, system, 0, 0, false).AsTask());
        Assert.True(rig.Space.IsStructureObserver(session));
        Assert.Equal(Structure, session.GetInt("shipid"));
        Assert.True(rig.Sim.Heard.OfType<SimControlStructure>().Last().Active);

        rig.Sim.RespawnAnswer = null;
        await rig.Space.SetStructureObserverAsync(session, system, 0, 0, false);
        Assert.True(await rig.Space.EjectStructureControlToCapsuleAsync(session, CancellationToken.None));
        Assert.False(rig.Space.IsStructureObserver(session));
        Assert.Equal(capsule.Id, session.GetInt("shipid"));
        Assert.Null(session.GetInt("structureid"));
        Assert.Equal(capsule.Id, rig.Space.ShipIdOf(session));
        var respawns = rig.Sim.Heard.OfType<SimRespawn>().Where(message => message.Ship.ShipId == capsule.Id).ToList();
        Assert.Equal(2, respawns.Count);
        Assert.Equal(position, respawns[^1].Position);
        Assert.Equal(direction, respawns[^1].Direction);

        await rig.Space.SetStructureObserverAsync(session, system, Structure, character.CharacterId, true);
        rig.Space.ApplyStructureControlSession(session, system, Structure, capsule.Id, system, true);
        await rig.Space.SetStructureObserverAsync(session, system, 0, 0, false);
        rig.Space.RestoreStructureControlSession(session, ship, system, true, system);
        Assert.Equal(capsule.Id, session.GetInt("shipid"));
        Assert.Null(session.GetInt("structureid"));
        Assert.Equal(capsule.Id, rig.Space.ShipIdOf(session));
    }

    [PgFact]
    public async Task A_failed_retained_hull_transaction_can_retry_on_release_without_another_death_notice()
    {
        var (rig, character, session) = await FlyingAsync();
        await using var _ = rig;
        var ship = session.GetInt("shipid")!.Value;
        var system = rig.Space.SolarSystemOf(session)!.Value;
        await rig.Space.SetStructureObserverAsync(session, system, Structure, character.CharacterId, true);
        rig.Space.ApplyStructureControlSession(session, system, Structure, ship, system, true);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using (var reject = new Npgsql.NpgsqlCommand($"ALTER TABLE \"{rig.InventorySchema}\".items ADD CONSTRAINT reject_control_capsule CHECK (type_id <> {CharacterShips.CapsuleTypeId}) NOT VALID", connection))
            await reject.ExecuteNonQueryAsync();
        rig.Sim.Deliver(new SimDestroyed((1UL << 61) + 1, ship, 0, [12300, 45600, 78900], [0, 1, 0], 0, 0));
        await rig.Space.QuiesceAsync();
        Assert.NotNull(await rig.Items.GetAsync(ship));
        Assert.Equal(ship, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.SetStructureObserverAsync(session, system, 0, 0, false).AsTask());
        Assert.True(rig.Space.IsStructureObserver(session));
        await using (var allow = new Npgsql.NpgsqlCommand($"ALTER TABLE \"{rig.InventorySchema}\".items DROP CONSTRAINT reject_control_capsule", connection))
            await allow.ExecuteNonQueryAsync();
        await rig.Space.SetStructureObserverAsync(session, system, 0, 0, false);
        Assert.True(await rig.Space.EjectStructureControlToCapsuleAsync(session, default));
        Assert.Null(await rig.Items.GetAsync(ship));
        var capsule = (await rig.Ships.LoadAsync(character.CharacterId))!.Ship;
        Assert.Equal(CharacterShips.CapsuleTypeId, capsule.TypeId);
        Assert.Equal(capsule.Id, session.GetInt("shipid"));
        Assert.Null(session.GetInt("structureid"));
    }
}
