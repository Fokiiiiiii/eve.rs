using Eve.Inventory;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The host's side of a command burst (track C6, protocol 42): a cycle settles the buffs of its source's fleet in reach, gives each ship the buffed numbers and tells
/// each pilot its buff state (<c>OnDbuffUpdated</c>); a later cycle refreshes them, and they run out when their time does.
/// </summary>
public class CommandBurstHostTests
{
    /// <summary>A Shield Command Burst I (42529) with a Shield Harmonizing charge (42695): the shield resistance buff, -8 (the wiki's +8 %, shown inverted).</summary>
    private const int ShieldCommandBurstI = 42529, ShieldHarmonizing = 42695;
    private const int ShieldResistance = 10;
    private const long FleetId = 701;
    private const long BuffMs = 60_000;
    /// <summary>The Pulse Activated Nexus Invulnerability Core (42522) and its duration (panicDuration, 240 s), and the collections it grants.</summary>
    private const int PanicCore = 42522, ShieldInvulnerability = 8, DisallowWeapons = 59;
    private const long CoreMs = 240_000;

    [PgFact]
    public async Task A_burst_cycle_buffs_the_source_and_its_fleet_ship_in_reach_and_tells_each_pilot_its_state()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, ShieldCommandBurstI);
        var ally = await DroneHostTests.FlyingWithADroneAsync(rig, characterName: "Fleet Mate", sessionId: 2);
        rig.Space.FleetIdOfAsync = (_, _) => Task.FromResult<long?>(FleetId);
        await LoadChargeAsync(rig, source, ShieldHarmonizing);
        rig.Space.StartModuleForTest((ulong)source.Session.SessionId, source.Module);

        var result = await ModuleFuelHostTests.SettleAsync(rig, BurstCycle(source, 1, ShipIdOf(source), ShipIdOf(ally)));

        Assert.True(result.Accepted, result.Reason);
        Assert.Contains(rig.Space.Buffs.StateOf(ShipIdOf(source), rig.Now), state => state.CollectionId == ShieldResistance && state.Value == -8);
        Assert.Contains(rig.Space.Buffs.StateOf(ShipIdOf(ally), rig.Now), state => state.CollectionId == ShieldResistance && state.Value == -8);
        foreach (var pilot in new[] { source, ally })
        {
            Assert.Contains(
                rig.Sim.BuffStates,
                told => told.SessionId == (ulong)pilot.Session.SessionId && told.ShipId == ShipIdOf(pilot)
                    && told.Entries.Any(entry => entry.CollectionId == ShieldResistance && entry.Value == -8 && entry.RemainingMs == BuffMs));
        }
    }

    [PgFact]
    public async Task A_pilot_outside_the_source_fleet_is_not_buffed_though_its_ship_is_in_reach()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, ShieldCommandBurstI);
        var outsider = await DroneHostTests.FlyingWithADroneAsync(rig, characterName: "Outsider", sessionId: 3);
        rig.Space.FleetIdOfAsync = (character, _) => Task.FromResult<long?>(character == source.Character ? FleetId : null);
        await LoadChargeAsync(rig, source, ShieldHarmonizing);
        rig.Space.StartModuleForTest((ulong)source.Session.SessionId, source.Module);

        var result = await ModuleFuelHostTests.SettleAsync(rig, BurstCycle(source, 2, ShipIdOf(source), ShipIdOf(outsider)));

        Assert.True(result.Accepted, result.Reason);
        Assert.Empty(rig.Space.Buffs.StateOf(ShipIdOf(outsider), rig.Now));
        Assert.DoesNotContain(rig.Sim.BuffStates, told => told.ShipId == ShipIdOf(outsider) && told.Entries.Count > 0);
    }

    [PgFact]
    public async Task A_later_cycle_refreshes_the_buffs_to_a_new_duration()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, ShieldCommandBurstI);
        await LoadChargeAsync(rig, source, ShieldHarmonizing);
        rig.Space.StartModuleForTest((ulong)source.Session.SessionId, source.Module);
        var first = rig.Now;
        Assert.True((await ModuleFuelHostTests.SettleAsync(rig, BurstCycle(source, 3, ShipIdOf(source)))).Accepted);

        rig.Now += 30_000;
        Assert.True((await ModuleFuelHostTests.SettleAsync(rig, BurstCycle(source, 4, ShipIdOf(source)))).Accepted);

        // the second cycle's buff runs to its own expiry, 60 s after it was applied: still on at 61 s after the first cycle
        var state = Assert.Single(rig.Space.Buffs.StateOf(ShipIdOf(source), first + 61_000));
        Assert.Equal((ShieldResistance, -8d, rig.Now + BuffMs), (state.CollectionId, state.Value, state.ExpiresAtMs));
    }

    [PgFact]
    public async Task The_buffs_run_out_when_their_time_does_and_the_pilot_is_told_its_state_without_them()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, ShieldCommandBurstI);
        await LoadChargeAsync(rig, source, ShieldHarmonizing);
        rig.Space.StartModuleForTest((ulong)source.Session.SessionId, source.Module);
        Assert.True((await ModuleFuelHostTests.SettleAsync(rig, BurstCycle(source, 5, ShipIdOf(source)))).Accepted);

        rig.Now += BuffMs + 1;
        await rig.Containers.SweepAsync();

        Assert.Empty(rig.Space.Buffs.StateOf(ShipIdOf(source), rig.Now));
        var last = rig.Sim.BuffStates.Last(told => told.ShipId == ShipIdOf(source));
        Assert.Empty(last.Entries);
    }

    [PgFact]
    public async Task A_burst_whose_module_does_not_run_is_refused_as_not_controlled_and_buffs_nobody()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, ShieldCommandBurstI);
        await LoadChargeAsync(rig, source, ShieldHarmonizing);

        var result = await ModuleFuelHostTests.SettleAsync(rig, BurstCycle(source, 6, ShipIdOf(source)));

        Assert.False(result.Accepted);
        Assert.Equal("NOT_CONTROLLED", result.Reason);
        Assert.Empty(rig.Space.Buffs.StateOf(ShipIdOf(source), rig.Now));
    }

    [PgFact]
    public async Task The_invulnerability_core_grant_gives_the_source_its_buffs_for_the_core_duration_and_not_a_frigate_of_the_fleet()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, PanicCore);
        var frigate = await DroneHostTests.FlyingWithADroneAsync(rig, characterName: "Fleet Frigate", sessionId: 2);
        rig.Space.FleetIdOfAsync = (_, _) => Task.FromResult<long?>(FleetId);
        rig.Space.StartModuleForTest((ulong)source.Session.SessionId, source.Module);

        var result = await ModuleFuelHostTests.SettleAsync(rig, GrantCycle(source, 7, ShipIdOf(source), ShipIdOf(frigate)));

        Assert.True(result.Accepted, result.Reason);
        var granted = rig.Space.Buffs.StateOf(ShipIdOf(source), rig.Now);
        Assert.Contains(granted, state => state.CollectionId == ShieldInvulnerability && state.Value == -99.99);
        Assert.Contains(granted, state => state.CollectionId == DisallowWeapons && state.Value == 1);
        Assert.All(granted, state => Assert.Equal(rig.Now + CoreMs, state.ExpiresAtMs));
        Assert.Empty(rig.Space.Buffs.StateOf(ShipIdOf(frigate), rig.Now));
        Assert.Contains(
            rig.Sim.BuffStates,
            told => told.SessionId == (ulong)source.Session.SessionId && told.Entries.Any(entry => entry.CollectionId == ShieldInvulnerability));
    }

    [PgFact]
    public async Task A_grant_for_a_core_that_the_pilot_does_not_run_is_refused_and_buffs_nobody()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await FlyingWithAsync(rig, PanicCore);

        var result = await ModuleFuelHostTests.SettleAsync(rig, GrantCycle(source, 8, ShipIdOf(source)));

        Assert.False(result.Accepted);
        Assert.Equal("NOT_CONTROLLED", result.Reason);
        Assert.Empty(rig.Space.Buffs.StateOf(ShipIdOf(source), rig.Now));
    }

    [Fact]
    public void The_grant_reaches_the_subcapital_mining_and_hauling_groups_and_no_other_ship()
    {
        Assert.All(new[] { 463, 543, 28, 380, 1202 }, group => Assert.True(PanicGroups.Allows(group)));
        // a Rorqual (883), an Orca (941), a Freighter (513) and a Rifter's frigate group (25) are not granted
        Assert.All(new[] { 883, 941, 513, 25 }, group => Assert.False(PanicGroups.Allows(group)));
    }

    [PgFact]
    public async Task The_core_buffs_are_the_static_datas_collections_with_the_client_values()
    {
        await using var rig = await Rig.CreateAsync();

        var buffs = PanicBuffs.Of(rig.Statics);

        Assert.Equal(PanicBuffs.Values.Count, buffs.Count);
        Assert.Equal(
            new[] { 3, 4, 5, 6, 8, 9, 55, 56, 57, 58, 59, 61 },
            buffs.Select(buff => buff.Collection.Id).Order().ToArray());
    }

    /// <summary>
    /// A pilot in space with a module of this type fitted and online in a free mid slot (the helper's own module goes in a high slot, where the default fit of the
    /// ship already sits, so a charge loaded into it would pair with the wrong item).
    /// </summary>
    private static async Task<DroneHostTests.Flying> FlyingWithAsync(Rig rig, int moduleTypeId)
    {
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig);
        var shipId = ShipIdOf(flying);
        var used = (await rig.Items.InLocationAsync(shipId, flying.Character)).Select(item => item.Flag).ToHashSet();
        var flag = Enumerable.Range(ItemFlags.MedSlot0, 8).First(candidate => !used.Contains(candidate));
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var moduleId = await rig.Items.CreateAsync(connection, transaction, new NewItem(moduleTypeId, flying.Character, shipId, flag, 1, true));
        await rig.Modules.SetAsync(connection, transaction, moduleId, ItemState.Online);
        await transaction.CommitAsync();
        return flying with { Module = moduleId };
    }

    /// <summary>The invulnerability core's grant as the simulation sends it at the start: the ships it found in reach, the source among them.</summary>
    private static SimModuleWorkCycle GrantCycle(DroneHostTests.Flying flying, ulong cycleId, params long[] recipients) => new(
        (ulong)flying.Session.SessionId, flying.Character, ShipIdOf(flying), flying.System, flying.Module, 0, cycleId, SimModuleWorkCommands.Panic,
        0, 0, 0, 0, 0, 0, 0, 0, 0, recipients);

    /// <summary>A burst's cycle as the simulation sends it at the cycle's end: the ships it found in reach, the source among them.</summary>
    private static SimModuleWorkCycle BurstCycle(DroneHostTests.Flying flying, ulong cycleId, params long[] recipients) => new(
        (ulong)flying.Session.SessionId, flying.Character, ShipIdOf(flying), flying.System, flying.Module, 0, cycleId, SimModuleWorkCommands.Burst,
        0, 0, 0, 0, 0, 0, 0, 0, 0, recipients);

    private static long ShipIdOf(DroneHostTests.Flying flying) => flying.Session.GetInt("shipid")!.Value;

    /// <summary>Loads a charge into the module's slot (its flag), two rounds of it.</summary>
    private static async Task LoadChargeAsync(Rig rig, DroneHostTests.Flying flying, int chargeTypeId)
    {
        var module = (await rig.Items.GetAsync(flying.Module))!;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction, new NewItem(chargeTypeId, flying.Character, ShipIdOf(flying), module.Flag, 2));
        await transaction.CommitAsync();
    }
}
