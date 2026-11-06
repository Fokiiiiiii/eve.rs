using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class TournamentPotentialFitTests
{
    [Fact]
    public void Running_charge_requires_agreement_in_both_directions()
    {
        var charge = new Item(1, 246, 2, 3, 27, 10, true, null);
        var loaded = new Eve.Space.SimTournamentMetricsRunning(4, 10, 5, 246, 10);
        Assert.True(SpaceSessions.TournamentRunningChargeMatches(charge, loaded));
        Assert.False(SpaceSessions.TournamentRunningChargeMatches(null, loaded));
        Assert.False(SpaceSessions.TournamentRunningChargeMatches(charge, loaded with { ChargeTypeId = null, Rounds = null }));
        Assert.True(SpaceSessions.TournamentRunningChargeMatches(null, loaded with { ChargeTypeId = null, Rounds = null }));
    }

    [PgFact]
    public async Task Real_ewar_and_remote_rep_respect_running_and_friendly_target()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.Equal(3396210, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([587, 526, 11355]);
        const long character = 82001, shipId = 82002, webId = 82003, repId = 82004, friend = 82005;
        var ship = new Item(shipId, 587, character, 60003760, 0, 1, true, null);
        var web = new Item(webId, 526, character, shipId, 19, 1, true, null);
        var rep = new Item(repId, 11355, character, shipId, 27, 1, true, null);
        var world = new DogmaWorld(statics, character, shipId);
        world.Add(new DogmaItem(shipId, ship.TypeId, character, ship.LocationId, ship.Flag, ItemState.Online));
        world.Add(new DogmaItem(webId, web.TypeId, character, shipId, web.Flag, ItemState.Active));
        world.Add(new DogmaItem(repId, rep.TypeId, character, shipId, rep.Flag, ItemState.Active));
        var snapshot = new ShipSnapshot { Character = null!, Ship = ship, Fitted = [web, rep], Skills = [], World = world };
        var running = new Dictionary<long, string> { [webId] = "remoteWebifierFalloff", [repId] = "shipModuleRemoteArmorRepairer" };
        var controls = TournamentPotentialFit.Controls(snapshot, statics, running);
        Assert.Equal((1, 1), controls);
        var targets = new Dictionary<long, long> { [webId] = 999, [repId] = friend };
        var friendly = TournamentPotentialFit.Calculate(snapshot, statics, running, true, targets, new HashSet<long> { friend });
        Assert.Null(friendly.UnavailableReason);
        Assert.True(friendly.Totals!.Value.RunningRepairHpPerSecond > 0);
        var enemy = TournamentPotentialFit.Calculate(snapshot, statics, running, true, targets, new HashSet<long>());
        Assert.Null(enemy.UnavailableReason);
        Assert.Equal(0, enemy.Totals!.Value.RunningRepairHpPerSecond);
        Assert.Equal(friendly.Totals.Value.FittedRepairHpPerSecond, enemy.Totals.Value.FittedRepairHpPerSecond);
        running.Remove(webId);
        Assert.Equal((1, 0), TournamentPotentialFit.Controls(snapshot, statics, running));
    }
}
