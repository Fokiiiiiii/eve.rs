using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class HeatProfileTests
{
    private const int Rifter = 587, RigA = 31378, RigB = 31372;

    [PgFact]
    public async Task Fitted_rigs_remain_on_the_ship_but_are_not_heat_rack_modules()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(shipTypeId: Rifter);
        await rig.Statics.EnsureTypesAsync([Rifter, RigA, RigB]);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(RigA, character.CharacterId, character.ShipId, 92, 1, true));
            await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(RigA, character.CharacterId, character.ShipId, 93, 1, true));
            await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(RigB, character.CharacterId, character.ShipId, 94, 1, true));
            await transaction.CommitAsync();
        }

        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False);

        var enter = Assert.Single(rig.Sim.Heard.OfType<SimEnter>());
        var heat = Assert.Single(rig.Sim.Heard.OfType<SimSetHeatProfile>());
        var fittedRigs = enter.Ship.Modules.Where(module => module.Flag is >= 92 and <= 94).OrderBy(module => module.Flag).ToArray();
        Assert.Equal(new[] { 92, 93, 94 }, fittedRigs.Select(module => module.Flag));
        Assert.Equal(new[] { RigA, RigA, RigB }, fittedRigs.Select(module => module.TypeId));
        Assert.DoesNotContain(heat.Profile.Modules, module => module.Flag is >= 92 and <= 99);
        Assert.Equal(enter.Ship.Modules.Where(module => module.Flag is >= 11 and <= 34).Select(module => (module.ItemId, module.Flag)).OrderBy(module => module.ItemId),
            heat.Profile.Modules.Select(module => (module.ModuleId, module.Flag)).OrderBy(module => module.ModuleId));
    }
}
