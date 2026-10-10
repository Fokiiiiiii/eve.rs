using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// The hidden modifier bucket (flag 156) is part of the ship's dogma world: the T3D stance's mode item is not a fitted module, and its passive effects act on
/// the hull (SF-5; EveJS <c>liveFittingState.js</c> keeps the stance item in the fit state).
/// </summary>
public class ShipStanceWorldTests
{
    private const int Confessor = 34317, DefenceMode = 34319, SharpshooterMode = 34321;

    /// <summary>The Sharpshooter mode's effect 6010 (<c>shipModeMaxTargetRangePostDiv</c>) divides the maximum target range (76) by the mode's attribute 1991.</summary>
    private const int MaxTargetRange = 76, ModeTargetRangeFactor = 1991;

    /// <summary>The Defence mode's effect 6014 (<c>modeSigRadiusPostDiv</c>) divides the signature radius (552) by the mode's attribute 2001.</summary>
    private const int SignatureRadius = 552, ModeSignatureFactor = 2001;

    [PgFact]
    public async Task A_stance_mode_item_is_in_the_ship_world_but_is_not_a_fitted_module_and_its_effect_reaches_the_hull()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Confessor);
        var plain = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.NotNull(plain);
        var modeId = await rig.GiveAsync(c.CharacterId, SharpshooterMode, singleton: true, location: c.ShipId, flag: ItemFlags.HiddenModifiers);

        var stanced = await rig.Ships.LoadAsync(c.CharacterId);

        Assert.NotNull(stanced);
        Assert.DoesNotContain(stanced.Fitted, module => module.Id == modeId);
        Assert.Equal(ItemFlags.HiddenModifiers, stanced.World.Item(modeId)?.FlagId);
        await rig.Statics.EnsureTypesAsync([SharpshooterMode]);
        var factor = rig.Statics.Type(SharpshooterMode)!.Attributes[ModeTargetRangeFactor];
        var baseline = plain.World.Get(plain.Ship.Id, MaxTargetRange);
        Assert.Equal(baseline / factor, stanced.World.Get(stanced.Ship.Id, MaxTargetRange), 6);
        Assert.Equal(plain.World.Get(plain.Ship.Id, SignatureRadius), stanced.World.Get(stanced.Ship.Id, SignatureRadius), 6);
    }

    [PgFact]
    public async Task Each_stance_moves_the_hull_numbers_it_owns_and_leaves_the_others_as_they_were()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Confessor);
        var plain = (await rig.Ships.LoadAsync(c.CharacterId))!;
        await rig.GiveAsync(c.CharacterId, DefenceMode, singleton: true, location: c.ShipId, flag: ItemFlags.HiddenModifiers);

        var defence = (await rig.Ships.LoadAsync(c.CharacterId))!;

        await rig.Statics.EnsureTypesAsync([DefenceMode]);
        var factor = rig.Statics.Type(DefenceMode)!.Attributes[ModeSignatureFactor];
        Assert.Equal(plain.World.Get(plain.Ship.Id, SignatureRadius) / factor, defence.World.Get(defence.Ship.Id, SignatureRadius), 6);
        Assert.Equal(plain.World.Get(plain.Ship.Id, MaxTargetRange), defence.World.Get(defence.Ship.Id, MaxTargetRange), 6);
    }
}
