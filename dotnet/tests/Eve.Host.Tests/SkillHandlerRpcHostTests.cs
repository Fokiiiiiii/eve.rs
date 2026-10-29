using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Skill_rpcs_accept_transport_macho_version_and_keep_the_client_wire_shapes_on_the_host()
    {
        var catalog = new SkillCatalog([], new Dictionary<int, IReadOnlyList<StartingSkill>>());
        var training = new CharacterTraining(null!, catalog, null!, null!, new TrainingSettings(), () => 0);
        var handler = new SkillHandlerService(null!, catalog, training, null!, null!, null!, null!, () => 0, null!);
        Start(strictRpc: true, configure: registry =>
        {
            registry.Register(new SkillMgr2Service());
            registry.Register(handler);
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);

        async Task<PyValue> WireCall(long id, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, service, method, new PyTuple(args), kwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }

        async Task<PyValue> Call(long id, string method, params PyValue[] args)
        {
            var decoded = await WireCall(id, "skillHandler", method, args);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
            return Result(decoded)!;
        }

        foreach (var id in new[] { 1L, 2L })
        {
            var moniker = Assert.IsType<PyObject>(Result(await WireCall(id, "skillMgr2", "GetMySkillHandler")));
            Assert.Equal("carbon.common.script.net.moniker.Moniker", T(moniker.Name));
            Assert.Equal("skillHandler", T(Assert.IsType<PyTuple>(moniker.Args).Items[0]));
        }

        Assert.IsType<PyList>(await Call(3, "GetSkillHistory"));
        Assert.IsType<PyNone>(await Call(4, "GetSkillPoints"));
        var queueAndFree = Assert.IsType<PyTuple>(await Call(5, "GetSkillQueueAndFreePoints"));
        Assert.Equal(2, queueAndFree.Items.Count);
        Assert.IsType<PyList>(queueAndFree.Items[0]);
        Assert.Equal(new PyInt(0), queueAndFree.Items[1]);
        Assert.IsType<PyNone>(await Call(6, "GetSkills"));
        Assert.IsType<PyNone>(await Call(7, "InjectSkillpoints", new PyLong(1), new PyInt(1)));
        Assert.IsType<PyNone>(await Call(8, "SplitSkillInjector", new PyLong(1), new PyInt(1)));
        Assert.IsType<PyNone>(await Call(9, "AbortTraining"));
        Assert.IsType<PyNone>(await Call(10, "ApplyFreeSkillPoints", new PyInt(3300), new PyInt(1000)));
        Assert.IsType<PyNone>(await Call(11, "CharStartTrainingSkill", new PyLong(330000000), PyNone.Instance));

        var invalidConstraint = await WireCall(12, "skillHandler", "CheckInjectionConstraints", new PyLong(1), new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(invalidConstraint)).Type);

        Assert.IsType<PyNone>(await Call(13, "CombineSkillInjector", new PyLong(1), new PyInt(3)));
        var invalidExtraction = await WireCall(14, "skillHandler", "ExtractSkills",
            new PyDict([]), new PyLong(1), new PyStr("token"));
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(invalidExtraction)).Type);

        Assert.IsType<PyNone>(await Call(15, "GetAllSkills"));
        Assert.Equal(5, Assert.IsType<PyDict>(await Call(16, "GetAttributes")).Entries.Count);
        Assert.Empty(Assert.IsType<PyDict>(await Call(17, "GetBoosters")).Entries);
        Assert.Equal(new PyInt(0), await Call(18, "GetDiminishedSpFromInjectors", new PyInt(40520), new PyInt(1), new PyInt(0)));
        Assert.Equal(new PyInt(0), await Call(19, "GetFreeSkillPoints"));
        Assert.Empty(Assert.IsType<PyDict>(await Call(20, "GetImplants")).Entries);
        Assert.IsType<PyList>(await Call(21, "GetSkillChangesForISIS"));
        Assert.IsType<PyObject>(await Call(22, "GetRespecInfo"));

        var bundle = Assert.IsType<PyTuple>(await Call(23, "GetSkillBundleInfo", new PyInt(1)));
        Assert.Equal(2, bundle.Items.Count);
        Assert.IsType<PyWString>(bundle.Items[0]);
        Assert.IsType<PyNone>(bundle.Items[1]);

        Assert.Equal(new PyInt(0), await Call(24, "GetFreeSkillPoints"));
        Assert.IsType<PyList>(await Call(25, "GetSkillHistory"));
    }

    [PgFact]
    public async Task Skill_manager_reads_and_injector_mutations_use_the_composed_host_routes()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var trainingSkill = catalog.Skills.First(skill => skill.Published && skill.Rank == 1 && !skill.Needs.Any());
        long capsule;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            capsule = await new Eve.Inventory.PgItemStore(rig.Source, rig.InventorySchema).CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(670, character, rig.Station, 4, 1, Singleton: true));
            await rig.Characters.SetShipAsync(connection, transaction, character, capsule);
            await transaction.CommitAsync();
        }
        await rig.SetSkillLevelAsync(character, trainingSkill.TypeId, 0);
        var skillMgr2 = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillMgr2"));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var largeInjector = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var smallInjector = catalog.ItemTypeId("Small Skill Injector")!.Value;
        var historyStore = new PgTrainingStore(rig.Source, rig.SkillsSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await historyStore.AppendHistoryAsync(connection, transaction, character,
                [new HistoryEntry(100, 36, 3300, 100, 1), new HistoryEntry(200, 38, 3300, 200, 2)]);
            await transaction.CommitAsync();
        }

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
                session.Set("shipid", new PyLong(capsule));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;
        var notifications = new List<string>();

        async Task<PyValue> Call(string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, new PyTuple(args), kwargs))));
            while (true)
            {
                var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = MachoPacket.Decode(decoded);
                if (packet?.Type == MachoMessage.Notification)
                {
                    notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                    continue;
                }
                return decoded;
            }
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
        var history = Assert.IsType<PyList>(Result(await Call("skillHandler", "GetSkillHistory", new PyInt(1))));
        Assert.Single(history.Items);
        var historyFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(history.Items[0]).Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyInt(38), historyFields["eventTypeID"]);

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
        var mySkillInfo = Assert.IsType<PyObject>(Result(await Call("skillHandler", "GetMySkillInfo")));
        var mySkillEntries = Assert.IsType<PyDict>(mySkillInfo.Args).Entries;
        Assert.All(mySkillEntries, entry => Assert.IsType<PyBytes>(entry.Key));
        var mySkillFields = mySkillEntries.ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new[] { "freeSkillPoints", "queue", "skillPoints", "skills" }, mySkillFields.Keys.Order());
        Assert.IsType<PyDict>(mySkillFields["skills"]);
        Assert.IsType<PyInt>(mySkillFields["skillPoints"]);
        Assert.IsType<PyInt>(mySkillFields["freeSkillPoints"]);
        Assert.IsType<PyList>(mySkillFields["queue"]);
        Assert.Equal(new PyInt(1_000_000), Result(await Call("skillHandler", "GetDiminishedSpFromInjectors",
            new PyInt(largeInjector), new PyInt(2), new PyInt(0))));

        var invalidInjection = await Call("skillHandler", "InjectSkillpoints", new PyLong(long.MaxValue), new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(invalidInjection)).Type);
        var cannotApplyFreePoints = await Call("skillHandler", "ApplyFreeSkillPoints", new PyInt(3300), new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(cannotApplyFreePoints)).Type);

        var items = new Eve.Inventory.PgItemStore(rig.Source, rig.InventorySchema);
        var skills = new PgSkillStore(rig.Source, rig.SkillsSchema);
        long injectorId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await skills.GrantFreeSkillPointsAsync(connection, transaction, character, 4_501_000);
            injectorId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(largeInjector, character, rig.Station, 4, 2, Singleton: false));
            await transaction.CommitAsync();
        }

        Assert.Equal(new PyInt(900_000), Result(await Call("skillHandler", "InjectSkillpoints",
            new PyLong(injectorId), new PyInt(2))));
        Assert.Equal(["OnItemChange", "OnFreeSkillPointsChanged"], notifications);
        notifications.Clear();
        Assert.Equal(5_401_000, await skills.FreeSkillPointsAsync(character));
        Assert.Null(await items.GetAsync(injectorId));

        long splitInjectorId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            splitInjectorId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(largeInjector, character, rig.Station, 4, 1, Singleton: true));
            await transaction.CommitAsync();
        }

        Assert.Equal(PyNone.Instance, Result(await Call("skillHandler", "SplitSkillInjector",
            new PyLong(splitInjectorId), new PyInt(1))));
        Assert.Null(await items.GetAsync(splitInjectorId));
        Assert.Contains(await items.InLocationAsync(rig.Station, character),
            item => item.TypeId == smallInjector && item.Quantity == 3);

        Assert.Equal(PyNone.Instance, Result(await Call("skillHandler", "CharStartTrainingSkill",
            new PyLong(SkillWire.ItemId(character, trainingSkill.TypeId)), new PyLong(character))));
        var queue = Assert.IsType<PyList>(Result(await Call("skillHandler", "GetMySkillQueue")));
        var firstLine = Assert.IsType<PyObject>(Assert.Single(queue.Items));
        var fields = Assert.IsType<PyDict>(firstLine.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyInt(1), fields["trainingToLevel"]);

        var skillTypes = catalog.Skills;
        var target = skillTypes.First(skill => skill.Rank >= 2
            && !skillTypes.Any(dependent => dependent.Needs.Any(requirement => requirement.TypeId == skill.TypeId)));
        var skillPoints = Eve.Skills.SkillPoints.ForTraining(target.Rank, 5);
        var trained = new List<CharacterSkill> { new(target.TypeId, 5, skillPoints) };
        var totalPoints = skillPoints;
        foreach (var skill in skillTypes.Where(skill => skill.TypeId != target.TypeId))
        {
            if (totalPoints >= 5_500_000) break;
            var points = Math.Min(Eve.Skills.SkillPoints.ForTraining(skill.Rank, 5), 5_500_000 - totalPoints);
            if (points <= 0) continue;
            trained.Add(new CharacterSkill(skill.TypeId, Eve.Skills.SkillPoints.LevelAt(skill.Rank, points), points));
            totalPoints += points;
        }
        Assert.True(totalPoints >= 5_500_000);
        var skillStore = new PgSkillStore(rig.Source, rig.SkillsSchema);
        var extractorType = catalog.ItemTypeId("Skill Extractor")!.Value;
        long extractorId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await skillStore.GrantAsync(connection, transaction, character, trained);
            extractorId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(extractorType, character, rig.Station, 4, 1, Singleton: true));
            await transaction.CommitAsync();
        }

        notifications.Clear();
        Assert.Equal(PyNone.Instance, Result(await Call("skillHandler", "ExtractSkills", new PyLong(extractorId),
            new PyList([Py.Tuple(new PyInt(target.TypeId), new PyLong(long.MaxValue))]))));
        Assert.Equal(["OnServerSkillsChanged", "OnSkillsChanged"], notifications.Take(2));
        Assert.Equal(2, notifications.Count(name => name == "OnItemChange"));
        Assert.Equal(["OnItemChange", "OnItemChange"], notifications.TakeLast(2));
        Assert.Null(await items.GetAsync(extractorId));
    }
}
