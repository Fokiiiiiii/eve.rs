using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Skill_manager_reads_and_injector_mutations_use_the_composed_host_routes()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var skillMgr2 = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillMgr2"));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var largeInjector = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var smallInjector = catalog.ItemTypeId("Small Skill Injector")!.Value;

        Start(
            configure: registry =>
            {
                registry.Register(skillMgr2);
                registry.Register(skillHandler);
            },
            sessionOpened: session =>
            {
                session.CharacterId = character;
                session.UserId = 1;
                session.Set("charid", new PyLong(character));
                session.Set("stationid", new PyLong(rig.Station));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 0;

        async Task<PyValue> Call(string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, new PyTuple(args), kwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }

        var moniker = Assert.IsType<PyObject>(Result(await Call("skillMgr2", "GetMySkillHandler")));
        var monikerArgs = Assert.IsType<PyTuple>(moniker.Args);
        Assert.Equal("skillHandler", JsSemantics.StringValue(monikerArgs.Items[0]));
        Assert.Equal(character, Assert.IsType<PyInt>(monikerArgs.Items[2]).Value);

        var attributes = Assert.IsType<PyDict>(Result(await Call("skillHandler", "GetAttributes")));
        Assert.Equal([164, 165, 166, 167, 168], attributes.Entries
            .Select(entry => Assert.IsType<PyInt>(entry.Key).Value)
            .Order().ToArray());
        Assert.IsType<PyDict>(Result(await Call("skillHandler", "GetBoosters")));
        Assert.IsType<PyDict>(Result(await Call("skillHandler", "GetImplants")));
        Assert.IsType<PyInt>(Result(await Call("skillHandler", "GetFreeSkillPoints")));
        Assert.Equal(new PyInt(0), Result(await Call("skillHandler", "GetSkillPoints")));
        Assert.IsType<PyList>(Result(await Call("skillHandler", "GetSkillChangesForISIS")));
        Assert.IsType<PyList>(Result(await Call("skillHandler", "GetSkillHistory", new PyInt(50))));

        var respecInfo = Result(await Call("skillHandler", "GetRespecInfo"));
        var respecFields = respecInfo switch
        {
            PyObject keyVal => Assert.IsType<PyDict>(keyVal.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException("Unexpected respec info"),
        };
        Assert.Equal(3, respecFields.Count);
        var bundleInfo = Assert.IsType<PyTuple>(Result(await Call("skillHandler", "GetSkillBundleInfo", new PyInt(1))));
        Assert.Equal(2, bundleInfo.Items.Count);

        var queueAndFree = Assert.IsType<PyTuple>(Result(await Call("skillHandler", "GetSkillQueueAndFreePoints")));
        Assert.IsType<PyList>(queueAndFree.Items[0]);
        Assert.IsType<PyInt>(queueAndFree.Items[1]);
        Assert.IsType<PyDict>(Result(await Call("skillHandler", "GetSkills")));
        Assert.IsType<PyDict>(Result(await Call("skillHandler", "GetAllSkills")));
        Assert.Equal(new PyInt(1_000_000), Result(await Call("skillHandler", "GetDiminishedSpFromInjectors",
            new PyInt(largeInjector), new PyInt(2), new PyInt(0))));

        var invalidInjection = await Call("skillHandler", "InjectSkillpoints", new PyLong(long.MaxValue), new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(invalidInjection)).Type);

        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var skills = new PgSkillStore(rig.Source, rig.SkillsSchema);
        long injectorId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await skills.GrantFreeSkillPointsAsync(connection, transaction, character, 4_501_000);
            injectorId = await items.CreateAsync(connection, transaction,
                new NewItem(largeInjector, character, rig.Station, 4, 2, Singleton: false));
            await transaction.CommitAsync();
        }

        Assert.Equal(new PyInt(900_000), Result(await Call("skillHandler", "InjectSkillpoints",
            new PyLong(injectorId), new PyInt(2))));
        Assert.Equal(5_401_000, await skills.FreeSkillPointsAsync(character));
        Assert.Null(await items.GetAsync(injectorId));

        long splitInjectorId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            splitInjectorId = await items.CreateAsync(connection, transaction,
                new NewItem(largeInjector, character, rig.Station, 4, 1, Singleton: true));
            await transaction.CommitAsync();
        }

        Assert.Equal(PyNone.Instance, Result(await Call("skillHandler", "SplitSkillInjector",
            new PyLong(splitInjectorId), new PyInt(1))));
        Assert.Null(await items.GetAsync(splitInjectorId));
        Assert.Contains(await items.InLocationAsync(rig.Station, character),
            item => item.TypeId == smallInjector && item.Quantity == 3);
    }
}