using Eve.Inventory;

namespace Eve.Ships.Tests;

public class CorporateCargoTests
{
    [PgFact]
    public async Task StackAll_uses_the_corporation_as_owner_for_corporation_hangar_stacks()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var corporationId = character.CharacterId + 1;
        const int flag = ItemFlags.CorpSAG1;
        long first;
        long second;

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            first = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(34, corporationId, Rig.Station, flag, 5));
            second = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(34, corporationId, Rig.Station, flag, 3));
            await transaction.CommitAsync();
        }

        var changes = await rig.Ships.StackAllAsync(
            character.CharacterId, Rig.Station, flag, ownerId: corporationId);

        Assert.Equal(2, changes.Count);
        Assert.Equal((first, corporationId, 8L), (changes[0].After.Id, changes[0].After.OwnerId, changes[0].After.Quantity));
        Assert.Equal((second, CharacterShips.JunkLocation, 3L),
            (changes[1].After.Id, changes[1].After.LocationId, changes[1].After.Quantity));
        Assert.Null(await rig.Items.GetAsync(second));
        Assert.Equal(8, (await rig.Items.GetAsync(first))!.Quantity);
    }
}
