using System.Threading;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-3 c: the structure hangar of a docked pilot (skipped without EVE_PG_URL; skipped is not passed). The ship and the items the character keeps in the
/// structure's hangar are the same rows a station's hangar keeps, at the structure's id, and the hangar lists them while the pilot is docked. A login
/// while docked (the restart path) gives the session the structure, not a station.
/// </summary>
public sealed class StructureHangarTests
{
    private const long Structure = 1_040_000_000_001;
    private const int Tritanium = 34;

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> DockedAtStructureAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        Events.Of(session);
        rig.Space.StructureExistsAsync = (id, _) => Task.FromResult(id == Structure);
        rig.Space.StructureDockingAllowedAsync = (_, _, _) => Task.FromResult(true);
        await rig.Space.DockStructureAsync(session, Structure, character.ShipId, CancellationToken.None);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Structure));
        await rig.Space.QuiesceAsync();
        await Rig.UntilAsync(() => session.GetInt("structureid") == Structure);
        Events.Of(session);
        return (rig, character, session);
    }

    [PgFact]
    public async Task The_structure_hangar_lists_the_docked_ship_and_the_items_kept_in_it_and_they_persist()
    {
        var (rig, character, session) = await DockedAtStructureAsync();
        await using var _ = rig;

        long stack;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            stack = await rig.Items.CreateAsync(connection, transaction, new NewItem(Tritanium, character.CharacterId, Structure, ItemFlags.Hangar, 5, false));
            await transaction.CommitAsync();
        }

        var hangar = Rig.OidOf(await rig.InventoryCallAsync(session, "GetInventory", null, [new PyInt(10004)]));
        var listed = Assert.IsType<PyList>(await rig.InventoryCallAsync(session, "List", hangar, []));
        var ids = listed.Items.Cast<PyPackedRow>().Select(row => Events.Number(row.Values[0])).Order().ToArray();
        Assert.Equal(new[] { character.ShipId, stack }.Order().ToArray(), ids);

        // what is in the hangar is in the database at the structure, in the hangar flag, as a station's hangar keeps it
        var kept = await rig.Items.InLocationAsync(Structure, character.CharacterId, CancellationToken.None);
        Assert.Equal(new[] { character.ShipId, stack }.Order().ToArray(), kept!.Select(item => item.Id).Order().ToArray());
        Assert.All(kept, item => Assert.Equal(ItemFlags.Hangar, item.Flag));
    }

    [PgFact]
    public async Task A_login_while_docked_gives_the_session_the_structure_and_the_solar_system()
    {
        var (rig, character, session) = await DockedAtStructureAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid")!.Value;

        var attributes = await rig.Space.GetSessionAttributesAsync(session, character.CharacterId, CancellationToken.None);
        var values = attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value);

        Assert.Equal(new PyLong(Structure), values["structureid"]);
        Assert.Equal(PyNone.Instance, values["stationid"]);
        Assert.Equal(new PyInt(system), values["solarsystemid"]);
        Assert.Equal(new PyInt(system), values["locationid"]);
    }
}
