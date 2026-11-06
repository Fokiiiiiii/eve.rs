using Eve.Ships;
using Eve.Marshal;
using Eve.Services.Space.Effects;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The buffs a ship carries survive a restart (track C8): the host keeps them in its record when they change, restores them when the ship enters a grid again,
/// and the simulation tells the pilot the state (OnDbuffUpdated).
/// </summary>
public class BuffRecordHostTests
{
    private const int ShieldResistance = 8;
    private const double DurationMs = 60_000;

    [PgFact]
    public async Task A_ship_that_undocks_after_a_restart_is_told_the_buffs_its_record_keeps()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Space.BuffStore = new PgShipBuffStore(rig.Source, rig.SpaceSchema);
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        var shipId = character.ShipId;
        var (collection, value) = PanicBuffs.Of(rig.Statics).Single(buff => buff.Collection.Id == ShieldResistance);
        rig.Space.Buffs.Apply(shipId, collection, value, rig.Now, DurationMs);
        await rig.Space.KeepBuffsAsync(shipId, rig.Now);

        // the restart: the registry starts empty, and the record is what is left
        rig.Space.Buffs.DropAll();
        Assert.Empty(rig.Space.Buffs.StateOf(shipId, rig.Now));

        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);

        Assert.Contains(rig.Space.Buffs.StateOf(shipId, rig.Now), state => state.CollectionId == collection.Id && state.Value == value);
        Assert.Contains(
            rig.Sim.BuffStates,
            told => told.SessionId == (ulong)session.SessionId && told.ShipId == shipId
                && told.Entries.Any(entry => entry.CollectionId == collection.Id && entry.Value == value));
    }

    [PgFact]
    public async Task A_buff_whose_time_ran_out_before_the_restart_is_not_restored()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Space.BuffStore = new PgShipBuffStore(rig.Source, rig.SpaceSchema);
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        var shipId = character.ShipId;
        var (collection, value) = PanicBuffs.Of(rig.Statics).Single(buff => buff.Collection.Id == ShieldResistance);
        rig.Space.Buffs.Apply(shipId, collection, value, rig.Now - DurationMs - 1, DurationMs);
        await rig.Space.KeepBuffsAsync(shipId, rig.Now);
        rig.Space.Buffs.DropAll();

        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);

        Assert.Empty(rig.Space.Buffs.StateOf(shipId, rig.Now));
        Assert.DoesNotContain(rig.Sim.BuffStates, told => told.SessionId == (ulong)session.SessionId);
    }
}
