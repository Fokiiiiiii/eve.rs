using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>A wreck that is made in space with what it holds (the wreck of an NPC): the item and its contents, in one transaction.</summary>
public class WreckTests
{
    private const int FrigateWreck = 26506, Tritanium = 34, Scourge = 3296;
    private const int System = 30100032;

    [PgFact]
    public async Task A_wreck_is_an_item_in_space_and_what_it_holds_is_in_it_at_the_hangar_flag_all_of_one_owner()
    {
        await using var rig = await Rig.CreateAsync();

        var made = await rig.Ships.LeaveWreckAsync(FrigateWreck, 140_000_001, System, [new WreckContent(Tritanium, 30, false), new WreckContent(Scourge, 5, true)]);

        var wreck = (await rig.Items.GetAsync(made.Wreck.Id))!;
        Assert.Equal(made.Wreck, wreck);
        Assert.Equal((FrigateWreck, 140_000_001L, (long)System, CharacterShips.InSpaceFlag, true), (wreck.TypeId, wreck.OwnerId, wreck.LocationId, wreck.Flag, wreck.Singleton));
        var inside = await rig.Items.InContainerAsync(wreck.Id);
        // a stack is as many as it says, a unit of its own is one however many it says
        Assert.Equal(
            [(Tritanium, 30L, false), (Scourge, 1L, true)],
            inside.OrderBy(i => i.Id).Select(i => (i.TypeId, i.Quantity, i.Singleton)));
        Assert.All(inside, i => Assert.Equal((140_000_001L, wreck.Id, ItemFlags.Hangar), (i.OwnerId, i.LocationId, i.Flag)));
        Assert.Equal(inside.OrderBy(i => i.Id), made.Contents.OrderBy(i => i.Id));
    }

    [PgFact]
    public async Task A_wreck_with_nothing_in_it_is_just_the_item()
    {
        await using var rig = await Rig.CreateAsync();

        var made = await rig.Ships.LeaveWreckAsync(FrigateWreck, 1_000_125, System, []);

        Assert.Empty(made.Contents);
        Assert.Empty(await rig.Items.InContainerAsync(made.Wreck.Id));
        Assert.NotNull(await rig.Items.GetAsync(made.Wreck.Id));
    }
}
