using Eve.Inventory;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The rocks of a solar system (<c>SpaceContainers.RocksInSystemAsync</c>, behind <c>/asteroid rewards</c>): the space items of the system whose item is an
/// asteroid with units left. A rock with no units, and an item that is not an asteroid, are not listed.
/// </summary>
public sealed class AsteroidRockTests
{
    private const int Jita = 30000142;
    private const int Veldspar = 1230;

    [PgFact]
    public async Task Rocks_of_a_system_are_listed_with_their_ore_and_units_and_empty_ones_are_not()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([Veldspar]);
        var group = rig.Statics.Type(Veldspar)!.GroupId;
        var full = await MakeRockAsync(rig, Veldspar, 500, group, Jita);
        var empty = await MakeRockAsync(rig, Veldspar, 0, group, Jita);
        var elsewhere = await MakeRockAsync(rig, Veldspar, 900, group, Jita + 1);

        var rocks = await rig.Containers.RocksInSystemAsync(Jita, CancellationToken.None);

        var rock = Assert.Single(rocks);
        Assert.Equal(full, rock.ItemId);
        Assert.Equal(Veldspar, rock.TypeId);
        Assert.Equal(500, rock.Units);
        Assert.Equal("Veldspar", rock.Name);
        Assert.DoesNotContain(rocks, candidate => candidate.ItemId == empty || candidate.ItemId == elsewhere);
    }

    private static async Task<long> MakeRockAsync(Rig rig, int typeId, long units, int groupId, int system)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await rig.Items.CreateAsync(connection, transaction, new NewItem(typeId, 1, system, 0, units, false));
        await rig.SpaceItems.SaveAsync(connection, transaction, new SpaceItem(itemId, system, [0d, 0d, 0d], [1d, 0d, 0d], 500d, groupId, 0, 0, 0, 0, 0, long.MaxValue));
        await transaction.CommitAsync();
        return itemId;
    }
}
