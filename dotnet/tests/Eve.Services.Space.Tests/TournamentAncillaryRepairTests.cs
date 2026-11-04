using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class TournamentAncillaryRepairTests
{
    [PgFact]
    public async Task Unloaded_ancillary_repairs_use_existing_cycle_and_remote_team_filter()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.Equal(3396210, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([587, 32774, 33076, 41476, 41480, 28668]);
        const long character = 82301, shipId = 82302, moduleId = 82303, target = 82304;
        var ship = new Item(shipId, 587, character, 60003760, 0, 1, true, null);
        foreach (var (typeId, effect, remote) in new[] {
            (32774, "fueledShieldBoosting", false), (33076, "fueledArmorRepair", false),
            (41476, "shipModuleAncillaryRemoteArmorRepairer", true), (41480, "shipModuleAncillaryRemoteShieldBooster", true) })
        {
            var module = new Item(moduleId, typeId, character, shipId, 27, 1, true, null);
            ShipSnapshot Snapshot(bool loaded)
            {
                var world = new DogmaWorld(statics, character, shipId);
                world.Add(new DogmaItem(shipId, ship.TypeId, character, ship.LocationId, ship.Flag, ItemState.Online));
                world.Add(new DogmaItem(moduleId, typeId, character, shipId, module.Flag, ItemState.Active));
                var charge = new Item(82305, 28668, character, shipId, module.Flag, 10, true, null);
                if (loaded) world.Add(new DogmaItem(charge.Id, charge.TypeId, character, shipId, charge.Flag, ItemState.Online));
                return new ShipSnapshot { Character = null!, Ship = ship, Fitted = [module], Charges = loaded ? [charge] : [], Skills = [], World = world };
            }
            var snapshot = Snapshot(false);
            var running = new Dictionary<long, string> { [moduleId] = effect };
            var targets = new Dictionary<long, long> { [moduleId] = target };
            var friendly = TournamentPotentialFit.Calculate(snapshot, statics, running, true, targets, new HashSet<long> { target });
            Assert.Null(friendly.UnavailableReason);
            Assert.True(friendly.Totals!.Value.RunningRepairHpPerSecond > 0);
            var enemy = TournamentPotentialFit.Calculate(snapshot, statics, running, true, targets, new HashSet<long>());
            Assert.Null(enemy.UnavailableReason);
            Assert.Equal(remote ? 0 : friendly.Totals.Value.RunningRepairHpPerSecond, enemy.Totals!.Value.RunningRepairHpPerSecond);
            if (typeId is 33076 or 41476 or 41480)
                Assert.Equal("UNSUPPORTED_EFFECT", TournamentPotentialFit.Calculate(Snapshot(true), statics, running, true, targets, new HashSet<long> { target }).UnavailableReason);
        }
    }
}
