using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes()
    {
        var observed = new List<string>();
        var callbacks = new StructureRpcCallbacks
        {
            DropOffItemsAsync = (_, ids, shipId, structureId) =>
            {
                observed.Add($"drop:{shipId}:{structureId}:{ids.Count}");
                return ValueTask.FromResult<IReadOnlyList<long>>([ids[0], ids[^1]]);
            },
            GetStructurePilotAsync = (_, structureId) =>
            {
                observed.Add($"pilot:{structureId}");
                return ValueTask.FromResult<long?>(structureId == 7001 ? 9001 : null);
            },
            TakeControlAsync = (_, structureId) =>
            {
                observed.Add($"take:{structureId}");
                return ValueTask.CompletedTask;
            },
            BoardStructureAsync = (_, structureId) =>
            {
                observed.Add($"board:{structureId}");
                return ValueTask.CompletedTask;
            },
            EjectFromStructureAsync = (_, shipId) =>
            {
                observed.Add($"eject:{shipId}");
                return ValueTask.CompletedTask;
            },
            ReleaseControlAsync = _ =>
            {
                observed.Add("release");
                return ValueTask.CompletedTask;
            },
            DeliverAsync = (_, structureId, recipientId, ids) =>
            {
                observed.Add($"deliver:{structureId}:{recipientId}:{string.Join(',', ids)}");
                return ValueTask.CompletedTask;
            },
            AnchorAsync = (_, request) =>
            {
                observed.Add($"anchor:{request.ItemId}:{request.StructureName}:{request.ReinforceWeekday}:{request.ReinforceHour}");
                return ValueTask.CompletedTask;
            },
            UnanchorAsync = (_, structureId) =>
            {
                observed.Add($"unanchor:{structureId}");
                return ValueTask.CompletedTask;
            },
            CancelUnanchorAsync = (_, structureId) =>
            {
                observed.Add($"cancel-unanchor:{structureId}");
                return ValueTask.CompletedTask;
            },
            RenameStructureAsync = (_, structureId, name) =>
            {
                observed.Add($"rename:{structureId}:{name}");
                return ValueTask.CompletedTask;
            },
        };
        var services = new IService[]
        {
            new StructureRpcService("structureControl", callbacks),
            new StructureRpcService("structureCargoDelivery", callbacks),
            new StructureRpcService("structureDeliveries", callbacks),
            new StructureRpcService("structureDeployment", callbacks),
            new StructureTravelRpcService("structureCynoBeaconMgr", _ =>
            {
                observed.Add("beacon");
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            }),
        };

        Start(
            configure: registry =>
            {
                foreach (var service in services) registry.Register(service);
            },
            sessionOpened: session => session.Set("structureid", new PyInt(7001)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, (uint)(observed.Count + 1), service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        Assert.Equal(new PyInt(9001), await Call("structureControl", "GetStructurePilot", Py.Tuple(new PyInt(7001))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "GetStructurePilot", Py.Tuple(new PyInt(7002))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "TakeControl", Py.Tuple()));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "BoardStructure", Py.Tuple(new PyInt(7002))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "EjectFromStructure", Py.Tuple(new PyInt(7003))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "ReleaseControl", Py.Tuple()));

        var moved = Assert.IsType<PyList>(await Call(
            "structureCargoDelivery",
            "DropOffItems",
            Py.Tuple(new PyList(Enumerable.Range(1, 500).Select(id => (PyValue)new PyInt(id)).ToArray()), new PyInt(7004), new PyInt(7005))));
        Assert.Equal(new PyList([new PyInt(1), new PyInt(500)]), moved);

        Assert.Equal(PyNone.Instance, await Call(
            "structureDeliveries",
            "Deliver",
            Py.Tuple(new PyInt(7001), new PyInt(9002), new PyList([new PyInt(21), new PyInt(22)]))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "Anchor",
            Py.Tuple(
                new PyInt(8001), new PyReal(1), new PyReal(2), new PyReal(0.5), new PyInt(7001),
                new PyStr("Structure"), new PyStr("bio"), new PyInt(3), new PyInt(17), new PyDict([]))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "Unanchor",
            Py.Tuple(new PyInt(8001))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "CancelUnanchor",
            Py.Tuple(new PyInt(8001))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "RenameStructure",
            Py.Tuple(new PyInt(8001), new PyStr("Renamed"))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureCynoBeaconMgr",
            "CmdJumpToStructureBeacon",
            Py.Tuple(new PyInt(7001), new PyInt(30000142))));

        Assert.Equal(
            [
                "pilot:7001", "pilot:7002", "take:7001", "board:7002", "eject:7003", "release",
                "drop:7004:7005:500", "deliver:7001:9002:21,22",
                "anchor:8001:Structure:3:17",
                "unanchor:8001", "cancel-unanchor:8001", "rename:8001:Renamed", "beacon",
            ],
            observed);
    }

    [Fact]
    public async Task Structure_directory_and_guests_use_registered_host_routes()
    {
        var storedStructures = new System.Text.Json.Nodes.JsonObject
        {
            ["1001"] = new System.Text.Json.Nodes.JsonObject
            {
                ["structureID"] = 1001,
                ["typeID"] = 35841,
                ["solarSystemID"] = 30000142,
                ["ownerCorpID"] = 8000,
                ["description"] = "before",
            },
        };
        async Task<System.Text.Json.Nodes.JsonObject> ReadTable(string table, CancellationToken _)
        {
            await Task.Yield();
            if (table == "structures")
                return (System.Text.Json.Nodes.JsonObject)storedStructures.DeepClone();
            if (table == "structureProfiles")
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["profilesByID"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["2"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["corporationID"] = 8001,
                            ["isDefault"] = false,
                            ["settingsBySettingID"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["19"] = new System.Text.Json.Nodes.JsonArray(
                                    new System.Text.Json.Nodes.JsonObject { ["groupID"] = 101, ["value"] = 1 }),
                            },
                        },
                    },
                };
            if (table == "accessGroups")
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["101"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["creatorID"] = 9002,
                        ["members"] = new System.Text.Json.Nodes.JsonArray(
                            new System.Text.Json.Nodes.JsonObject { ["memberID"] = 0, ["membershipType"] = 0 }),
                    },
                };
            return new System.Text.Json.Nodes.JsonObject();
        }
        Task<Eve.Relations.Affiliation> Affiliation(long _, CancellationToken __) =>
            Task.FromResult(new Eve.Relations.Affiliation(8000, null, null));
        Task SetDescription(long structureId, string description, CancellationToken _)
        {
            storedStructures[structureId.ToString()]!["description"] = description;
            return Task.CompletedTask;
        }

        var directoryRead = new PersistenceStructureDirectoryReadModel(
            ReadTable, Affiliation, null!, SetDescription,
            stationManagerOfAsync: (_, _, _) => Task.FromResult(true));
        long? guestsStructure = null;
        IReadOnlySet<long>? observerGuests = null;
        var guests = new StructureGuestsService(
            (structureId, _) =>
            {
                guestsStructure = structureId;
                return ValueTask.FromResult<IReadOnlyList<StructureGuestSnapshot>>(
                    [new(9002, 8002, 7002, 6002), new(9001, 8001, 7001, 6001)]);
            },
            (_, structureId, characterIds) =>
            {
                guestsStructure = structureId;
                observerGuests = characterIds;
            });

        Start(
            configure: registry =>
            {
                registry.Register(new StructureDirectoryService(directoryRead));
                registry.Register(guests);
            },
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(9000));
                session.Set("corpid", new PyInt(8000));
                session.Set("structureid", new PyInt(7001));
                session.Set("solarsystemid2", new PyInt(30000142));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        uint callId = 1;
        async Task<PyValue> Call(string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        var bridgeAccess = Assert.IsType<PyTuple>(
            await Call("structureDirectory", "GetJumpBridgesWithMyAccess", Py.Tuple()));
        Assert.Equal(3, bridgeAccess.Items.Count);
        Assert.All(bridgeAccess.Items, value => Assert.Empty(Assert.IsType<PyList>(value).Items));
        Assert.Single(Assert.IsType<PyList>(
            await Call("structureDirectory", "GetNearbyJumpBridges", Py.Tuple())).Items);
        Assert.Equal("before", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(await Call(
            "structureDirectory", "GetStructureDescription", Py.Tuple(new PyInt(1001)))).Value.Span));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDirectory", "SetStructureDescription", Py.Tuple(new PyInt(1001), new PyStr("after"))));
        Assert.Equal("after", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(await Call(
            "structureDirectory", "GetStructureDescription", Py.Tuple(new PyInt(1001)))).Value.Span));

        var guestSnapshot = Assert.IsType<PyDict>(
            await Call("structureGuests", "GetGuests", Py.Tuple(new PyInt(7001))));
        Assert.Equal(2, guestSnapshot.Entries.Count);
        Assert.Equal(
            new PyList([new PyInt(8001), new PyInt(7001), new PyInt(6001)]),
            guestSnapshot.Entries.Single(entry => entry.Key == new PyInt(9001)).Value);
        Assert.Equal(7001, guestsStructure);
        Assert.Equal(new HashSet<long> { 9001, 9002 }, observerGuests);
    }


    [Fact]
    public async Task Structure_directory_all_client_methods_keep_wire_shapes_and_errors()
    {
        var storedStructures = new System.Text.Json.Nodes.JsonObject
        {
            ["1001"] = new System.Text.Json.Nodes.JsonObject
            {
                ["structureID"] = 1001,
                ["typeID"] = 35840,
                ["solarSystemID"] = 30000143,
                ["ownerCorpID"] = 8000,
                ["ownerID"] = 8000,
                ["description"] = "before",
                ["itemName"] = "Cyno Beacon",
                ["profileID"] = 0,
                ["upkeepState"] = 1,
                ["state"] = 2,
                ["stateEndsAt"] = 1800000000000,
                ["fuelExpiresAt"] = 1800000000000,
                ["reinforceWeekday"] = 3,
                ["reinforceHour"] = 17,
                ["nextReinforceApply"] = 1800000000000,
                ["nextReinforceWeekday"] = 4,
                ["nextReinforceHour"] = 18,
                ["unanchoring"] = 0,
                ["liquidOzoneQty"] = 25,
                ["wars"] = new System.Text.Json.Nodes.JsonArray(6001),
                ["position"] = new System.Text.Json.Nodes.JsonObject { ["x"] = 1d, ["y"] = 2d, ["z"] = 3d },
                ["serviceStates"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["1"] = 1,
                    ["5"] = 1,
                    ["16"] = 1,
                },
            },
            ["2001"] = Bridge(2001, 30000142, 30000143, 8000, 1),
            ["2002"] = Bridge(2002, 30000143, 30000142, 8000, 1),
            ["2003"] = Bridge(2003, 30000144, 30000142, 8001, 0),
        };
        static System.Text.Json.Nodes.JsonObject Bridge(long id, long system, long destination, long owner, int online) => new()
        {
            ["structureID"] = id,
            ["typeID"] = 35841,
            ["solarSystemID"] = system,
            ["ownerCorpID"] = owner,
            ["itemName"] = id == 2003 ? "" : $"Bridge {id}",
            ["profileID"] = id == 2003 ? 2 : 0,
            ["devFlags"] = new System.Text.Json.Nodes.JsonObject { ["destinationSolarsystemID"] = destination },
            ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1, ["15"] = online },
        };

        async Task<System.Text.Json.Nodes.JsonObject> ReadTable(string table, CancellationToken _)
        {
            await Task.Yield();
            if (table == "structures")
                return (System.Text.Json.Nodes.JsonObject)storedStructures.DeepClone();
            if (table == "structureProfiles")
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["profilesByID"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["2"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["corporationID"] = 8001,
                            ["isDefault"] = false,
                            ["settingsBySettingID"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["19"] = new System.Text.Json.Nodes.JsonArray(
                                    new System.Text.Json.Nodes.JsonObject { ["groupID"] = 101, ["value"] = 1 }),
                            },
                        },
                    },
                };
            if (table == "accessGroups")
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["101"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["creatorID"] = 9002,
                        ["members"] = new System.Text.Json.Nodes.JsonArray(
                            new System.Text.Json.Nodes.JsonObject { ["memberID"] = 0, ["membershipType"] = 0 }),
                    },
                };
            return new System.Text.Json.Nodes.JsonObject();
        }
        Task<Eve.Relations.Affiliation> Affiliation(long characterId, CancellationToken _) =>
            Task.FromResult(characterId == 9000
                ? new Eve.Relations.Affiliation(8000, 7000, null)
                : new Eve.Relations.Affiliation(8001, null, null));
        var canManageDescription = true;
        var hasCorporationDirectoryRole = true;
        Task SetDescription(long structureId, string description, CancellationToken _)
        {
            storedStructures[structureId.ToString()]!["description"] = description;
            return Task.CompletedTask;
        }

        var stationId = 60000142L;
        var station = new Eve.Universe.Station(
            stationId, 1531, "Test Station", 6000001, "Test Corporation", null, "",
            1, 1, 30000142, "Test System", 1, "Test Constellation", 10000064,
            "Test Region", 0.9, 0, 0);
        var stationCatalog = new Eve.Universe.StationCatalog(
            [station],
            new Dictionary<long, IReadOnlyList<int>> { [stationId] = [7, 22] });
        var directoryRead = new PersistenceStructureDirectoryReadModel(
            ReadTable,
            Affiliation,
            null!,
            SetDescription,
            stationManagerOfAsync: (_, _, _) => Task.FromResult(canManageDescription),
            corporationStructureDirectoryAccessAsync: (_, _, _) => Task.FromResult(hasCorporationDirectoryRole),
            stationCatalog: stationCatalog);

        Start(
            configure: registry => registry.Register(new StructureDirectoryService(directoryRead)),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(9000));
                session.Set("corpid", new PyInt(8000));
                session.Set("allianceid", new PyInt(7000));
                session.Set("regionid", new PyInt(10000064));
                session.Set("structureid", new PyInt(1001));
                session.Set("solarsystemid2", new PyInt(30000142));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 1;
        async Task<PyValue> RawCall(string method, params PyValue[] arguments)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, "structureDirectory", method, new PyTuple(arguments), kwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }
        async Task<PyValue> Call(string method, params PyValue[] arguments) =>
            Result(await RawCall(method, arguments)) ?? PyNone.Instance;
        static PyValue Field(PyValue value, string name)
        {
            var keyVal = Assert.IsType<PyObject>(value);
            Assert.Equal("util.KeyVal", TextValue(keyVal.Name));
            var fields = Assert.IsType<PyDict>(keyVal.Args);
            return fields.Entries.Single(entry => TextValue(entry.Key) == name).Value;
        }
        static long Number(PyValue value) => value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => throw new InvalidOperationException($"Expected an integer, received {value.GetType().Name}."),
        };
        static string TextValue(PyValue value) => value switch
        {
            PyStr text => text.Value,
            PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
            _ => throw new InvalidOperationException($"Expected text, received {value.GetType().Name}."),
        };
        static (string Code, PyDict Parameters) UserError(PyValue response)
        {
            var packet = MachoPacket.Decode(response);
            Assert.NotNull(packet);
            Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
            var errorState = Assert.IsType<PyTuple>(packet.Payload).Items;
            Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(errorState[1]).Value);
            var wrapped = Assert.IsType<PyTuple>(errorState[2]).Items;
            var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
            var header = Assert.IsType<PyTuple>(userError.Header).Items;
            Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
            var args = Assert.IsType<PyTuple>(header[1]).Items;
            return (TextValue(args[0]), Assert.IsType<PyDict>(args[1]));
        }
        static void AssertInsufficientRoles(PyValue response)
        {
            var error = UserError(response);
            Assert.Equal("CrpAccessDenied", error.Code);
            var reason = error.Parameters.Entries.Single(entry => TextValue(entry.Key) == "reason").Value;
            var parts = Assert.IsType<PyList>(reason).Items;
            Assert.Equal(101, Number(parts[0]));
            Assert.Equal("UI/Corporations/AccessRestrictions/InsufficientRoles", TextValue(parts[1]));
        }

        var nearby = Assert.IsType<PyList>(await Call("GetNearbyJumpBridges"));
        Assert.Equal(3, nearby.Items.Count);
        var offlineBridge = nearby.Items.Single(row => Number(Field(row, "structureID")) == 2003);
        Assert.Equal("Ansiblex Jump Bridge", TextValue(Field(offlineBridge, "structureName")));
        Assert.Equal(8001, Number(Field(offlineBridge, "ownerID")));
        Assert.Equal(PyBool.True, Field(offlineBridge, "alignedToCurrentSystem"));

        var jumpAccess = Assert.IsType<PyTuple>(await Call("GetJumpBridgesWithMyAccess"));
        Assert.Equal(3, jumpAccess.Items.Count);
        var pairs = Assert.IsType<PyList>(jumpAccess.Items[0]);
        Assert.Single(pairs.Items);
        Assert.Equal(2, Assert.IsType<PyList>(pairs.Items[0]).Items.Count);
        Assert.Contains(2001, Assert.IsType<PyList>(jumpAccess.Items[1]).Items.Select(Number));
        Assert.Contains(2002, Assert.IsType<PyList>(jumpAccess.Items[1]).Items.Select(Number));
        Assert.Empty(Assert.IsType<PyList>(jumpAccess.Items[2]).Items);

        Assert.Equal("before", TextValue(await Call("GetStructureDescription", new PyLong(1001))));
        canManageDescription = false;
        AssertInsufficientRoles(await RawCall(
            "SetStructureDescription", new PyLong(1001), new PyStr("denied")));
        canManageDescription = true;
        Assert.Equal(PyNone.Instance, await Call("SetStructureDescription", new PyLong(1001), new PyStr("after")));
        Assert.Equal("after", TextValue(await Call("GetStructureDescription", new PyInt(1001))));

        var fullInfo = await Call("GetStructureInfo", new PyLong(1001));
        Assert.Equal("Cyno Beacon", TextValue(Field(fullInfo, "itemName")));
        Assert.Equal(PyBool.True, Field(fullInfo, "inSpace"));
        Assert.Equal(new PyList([new PyInt(6001)]), Field(fullInfo, "wars"));
        foreach (var field in new[]
        {
            "itemID", "structureID", "solarSystemID", "locationID", "ownerID", "allianceID",
            "typeID", "groupID", "categoryID", "position", "profileID", "services", "fuelExpires",
            "upkeepState", "state", "timerEnd", "reinforce_weekday", "reinforce_hour",
            "next_reinforce_weekday", "next_reinforce_hour", "next_reinforce_apply",
            "unanchoring", "liquidOzoneQty", "wars",
        })
            _ = Field(fullInfo, field);
        var underscoreInfo = await Call("GetStructureInfo_", new PyLong(1001));
        Assert.Equal(30000143, Number(Field(underscoreInfo, "solarSystemID")));
        Assert.Equal(PyNone.Instance, await Call("GetStructureInfo", PyNone.Instance));
        var publicInfo = await Call("GetStructureInfo", new PyInt(2003));
        var publicFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(publicInfo).Args).Entries;
        Assert.Equal(
            new HashSet<string> { "typeID", "structureID", "upkeepState", "wars", "ownerID", "solarSystemID", "itemName", "inSpace" },
            publicFields.Select(entry => TextValue(entry.Key)).ToHashSet());
        Assert.Equal(new PyList([]), await Call("GetStructureMapData", new PyInt(30000142)));
        Assert.Equal(new PyList([]), await Call("GetStructureMapData", PyNone.Instance));

        var dockable = Assert.IsType<PyList>(await Call("GetMyDockableStructures", new PyLong(30000142)));
        Assert.Contains(2001, dockable.Items.Select(Number));
        var dockingIds = new PyList([new PyLong(2001), new PyInt(2003)]);
        var accessible = Assert.IsType<PyList>(await Call("CheckMyDockingAccessToStructures", dockingIds));
        Assert.Equal(new long[] { 2001, 2003 }, accessible.Items.Select(Number));
        var limitBatch = new PyList(
            new PyValue[] { new PyLong(2001), new PyInt(2003) }
                .Concat(Enumerable.Range(1, 498).Select(id => (PyValue)new PyInt(id)))
                .ToArray());
        Assert.Equal(new long[] { 2001, 2003 },
            Assert.IsType<PyList>(await Call("CheckMyDockingAccessToStructures", limitBatch)).Items.Select(Number));
        Assert.Empty(Assert.IsType<PyList>(await Call("CheckMyDockingAccessToStructures", PyNone.Instance)).Items);
        Assert.Equal("GenericStopSpamming", UserError(await RawCall(
            "CheckMyDockingAccessToStructures",
            new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id)).ToArray()))).Code);

        var characterStructures = Assert.IsType<PyDict>(await Call("GetMyCharacterStructures"));
        var stationRow = characterStructures.Entries.Single(entry => Number(entry.Key) == stationId).Value;
        var stationServices = Assert.IsType<PyDict>(Field(stationRow, "services"));
        Assert.Contains(1, stationServices.Entries.Select(entry => Number(entry.Key)));
        Assert.Contains(5, stationServices.Entries.Select(entry => Number(entry.Key)));
        Assert.Contains(8, stationServices.Entries.Select(entry => Number(entry.Key)));
        Assert.Contains(characterStructures.Entries, entry => Number(entry.Key) == 1001);
        var publicStructure = characterStructures.Entries.Single(entry => Number(entry.Key) == 2003).Value;
        Assert.Equal(6, Assert.IsType<PyDict>(Assert.IsType<PyObject>(publicStructure).Args).Entries.Count);

        var corporationStructures = Assert.IsType<PyDict>(await Call("GetMyCorporationStructures"));
        var corporationStructure = corporationStructures.Entries.Single(entry => Number(entry.Key) == 1001).Value;
        var corporationServices = Assert.IsType<PyDict>(Field(corporationStructure, "services"));
        Assert.Contains(16, corporationServices.Entries.Select(entry => Number(entry.Key)));
        hasCorporationDirectoryRole = false;
        AssertInsufficientRoles(await RawCall("GetMyCorporationStructures"));
        hasCorporationDirectoryRole = true;

        var beaconRows = Assert.IsType<PyList>(await Call("GetMyAccessibleOnlineCynoBeaconStructures"));
        var beacon = Assert.IsType<PyList>(beaconRows.Items.Single(row =>
            Number(Assert.IsType<PyList>(row).Items[0]) == 1001));
        Assert.Equal(6, beacon.Items.Count);
        Assert.Equal(1001, Number(beacon.Items[0]));
        Assert.Equal(35840, Number(beacon.Items[1]));
        Assert.Equal(8000, Number(beacon.Items[2]));
        Assert.Equal(30000143, Number(beacon.Items[3]));
        Assert.Equal("Cyno Beacon", TextValue(beacon.Items[5]));
        Assert.Contains(30000143, Assert.IsType<PyList>(await Call("GetSolarSystemsWithBeacons")).Items.Select(Number));

        Assert.Empty(Assert.IsType<PyList>(await Call("GetValidWarHQs", new PyLong(7000))).Items);
    }

}
