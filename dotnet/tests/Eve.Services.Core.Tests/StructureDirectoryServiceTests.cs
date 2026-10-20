using Eve.Marshal;

namespace Eve.Services.Core.Tests;

public sealed class StructureDirectoryServiceTests
{
    [Fact]
    public void Exposes_the_structure_directory_client_reads()
    {
        var service = new StructureDirectoryService(new TestReadCallbacks());

        Assert.Equal("structureDirectory", service.Name);
        Assert.True(service.HasMethod("GetJumpBridgesWithMyAccess"));
        Assert.True(service.HasMethod("GetNearbyJumpBridges"));
        Assert.True(service.HasMethod("GetStructureDescription"));
        Assert.True(service.HasMethod("GetStructureInfo"));
        Assert.True(service.HasMethod("CheckMyDockingAccessToStructures"));
        Assert.True(service.HasMethod("GetMyCharacterStructures"));
        Assert.True(service.HasMethod("GetMyCorporationStructures"));
        Assert.True(service.HasMethod("GetMyDockableStructures"));
        Assert.True(service.HasMethod("GetStructureMapData"));
        Assert.True(service.HasMethod("GetMyAccessibleOnlineCynoBeaconStructures"));
        Assert.True(service.HasMethod("GetSolarSystemsWithBeacons"));
        Assert.True(service.HasMethod("GetValidWarHQs"));
        Assert.False(service.HasMethod("CanContractFrom")); // The pinned client implements this locally via GetStructureInfo.
        Assert.True(service.HasMethod("SetStructureDescription"));
    }

    [Fact]
    public async Task Jump_bridge_rows_are_returned_unchanged_in_callback_order()
    {
        var callbacks = new TestReadCallbacks
        {
            JumpBridgesResult = Py.List(
                Py.KeyVal(("rowOrder", new PyInt(2)), ("label", new PyStr("second"))),
                Py.KeyVal(("rowOrder", new PyInt(1)), ("label", new PyStr("first")))),
        };
        var service = new StructureDirectoryService(callbacks);
        var call = Request("GetJumpBridgesWithMyAccess", new PyStr("jump-bridge-request"));

        var result = await service.CallAsync(call);

        Assert.Equal(callbacks.JumpBridgesResult, result);
        Assert.Same(call, callbacks.LastCall);
        Assert.Equal(new PyStr("jump-bridge-request"), callbacks.LastCall!.Arg(0));
        var rows = Assert.IsType<PyList>(result);
        Assert.Equal(new PyInt(2), Field(rows.Items[0], "rowOrder"));
        Assert.Equal(new PyStr("second"), Field(rows.Items[0], "label"));
        Assert.Equal(new PyInt(1), Field(rows.Items[1], "rowOrder"));
        Assert.Equal(new PyStr("first"), Field(rows.Items[1], "label"));
    }

    [Fact]
    public async Task Nearby_jump_bridge_empty_result_is_preserved_as_an_empty_list()
    {
        var callbacks = new TestReadCallbacks { NearbyJumpBridgesResult = Py.List() };
        var service = new StructureDirectoryService(callbacks);
        var call = Request("GetNearbyJumpBridges", new PyStr("nearby-request"));

        var result = await service.CallAsync(call);

        Assert.Same(call, callbacks.LastCall);
        Assert.Equal(new PyStr("nearby-request"), callbacks.LastCall!.Arg(0));
        Assert.Empty(Assert.IsType<PyList>(result).Items);
    }

    [Fact]
    public async Task Structure_description_result_is_returned_without_coercion()
    {
        var callbacks = new TestReadCallbacks { StructureDescriptionResult = new PyStr("description payload") };
        var service = new StructureDirectoryService(callbacks);
        var call = Request("GetStructureDescription", new PyStr("description-request"));

        var result = await service.CallAsync(call);

        Assert.Same(call, callbacks.LastCall);
        Assert.Equal(new PyStr("description-request"), callbacks.LastCall!.Arg(0));
        Assert.IsType<PyStr>(result);
        Assert.Equal(new PyStr("description payload"), result);
    }

    [Fact]
    public async Task Unknown_method_returns_none_without_calling_read_callbacks()
    {
        var callbacks = new TestReadCallbacks();
        var service = new StructureDirectoryService(callbacks);

        var result = await service.CallAsync(Request("UnknownStructureDirectoryMethod"));

        Assert.Same(PyNone.Instance, result);
        Assert.Null(callbacks.LastCall);
    }

    [Fact]
    public void Exposes_my_character_and_corporation_structure_map_reads()
    {
        var service = new StructureDirectoryService(new TestReadCallbacks());

        Assert.True(service.HasMethod("GetMyCharacterStructures"));
        Assert.True(service.HasMethod("GetMyCorporationStructures"));
    }

    [Fact]
    public async Task My_character_structures_returns_the_structure_id_keyed_service_map()
    {
        var info = Py.KeyVal(("services", Py.List(new PyInt(23))));
        var structures = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(90000001), info),
        ]);
        var callbacks = new TestReadCallbacks { CharacterStructuresResult = structures };
        var service = new StructureDirectoryService(callbacks);
        var call = new ServiceCall
        {
            Method = "GetMyCharacterStructures",
            Args = [],
            Session = new ClientSession { CharacterId = 42 },
        };

        var result = await service.CallAsync(call);

        Assert.Same(structures, result);
        Assert.Equal(42L, callbacks.CharacterStructuresCharacterId);
        var entry = Assert.Single(Assert.IsType<PyDict>(result).Entries);
        Assert.Equal(new PyInt(90000001), entry.Key);
        Assert.IsType<PyObject>(entry.Value);
        Assert.IsType<PyList>(Field(entry.Value, "services"));
    }

    [Fact]
    public async Task My_corporation_structures_uses_the_session_character_and_corporation()
    {
        const long corporationId = 98000001;
        var structures = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyInt(90000002),
                Py.KeyVal(("services", Py.List(new PyInt(24))))),
        ]);
        var callbacks = new TestReadCallbacks { CorporationStructuresResult = structures };
        var service = new StructureDirectoryService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(corporationId));
        var call = new ServiceCall
        {
            Method = "GetMyCorporationStructures",
            Args = [],
            Session = session,
        };

        var result = await service.CallAsync(call);

        Assert.Same(structures, result);
        Assert.Equal((42L, corporationId), callbacks.CorporationStructuresIdentity);
    }

    [Fact]
    public async Task Missing_character_or_corporation_identity_returns_an_empty_map_without_reading()
    {
        var callbacks = new TestReadCallbacks();
        var service = new StructureDirectoryService(callbacks);
        var calls = new[]
        {
            new ServiceCall { Method = "GetMyCharacterStructures", Args = [], Session = new ClientSession() },
            new ServiceCall { Method = "GetMyCorporationStructures", Args = [], Session = new ClientSession { CharacterId = 42 } },
        };

        foreach (var call in calls)
        {
            var result = await service.CallAsync(call);
            Assert.Empty(Assert.IsType<PyDict>(result).Entries);
        }

        Assert.Null(callbacks.CharacterStructuresCharacterId);
        Assert.Null(callbacks.CorporationStructuresIdentity);
    }

    [Fact]
    public void Asset_safety_service_exposes_the_client_remote_rpcs()
    {
        var service = new StructureAssetSafetyService(new TestAssetSafetyReadCallbacks());

        Assert.Equal("structureAssetSafety", service.Name);
        Assert.True(service.HasMethod("GetItemsInSafetyForCharacter"));
        Assert.True(service.HasMethod("GetItemsInSafetyForCorp"));
        Assert.True(service.HasMethod("MovePersonalAssetsToSafety"));
        Assert.True(service.HasMethod("MoveCorpAssetsToSafety"));
        Assert.True(service.HasMethod("GetWrapNames"));
        Assert.True(service.HasMethod("GetStructuresICanDeliverTo"));
        Assert.True(service.HasMethod("MoveSafetyWrapToStructure"));
        Assert.True(service.HasMethod("MoveEjectTimeGM"));
        Assert.False(service.HasMethod("MoveItemsInStructureToAssetSafetyForCharacter"));
        Assert.False(service.HasMethod("MoveItemsInStructureToAssetSafetyForCorp"));
    }

    [Fact]
    public async Task Character_asset_safety_read_returns_the_persisted_rows_for_the_session_character()
    {
        var nearestNpcStation = Py.KeyVal(
            ("itemID", new PyLong(60000001)),
            ("typeID", new PyInt(12242)));
        var record = Py.KeyVal(
            ("assetWrapID", new PyLong(90000001)),
            ("solarSystemID", new PyLong(30000142)),
            ("wrapName", new PyStr("Asset Safety Wrap")),
            ("ejectTime", new PyLong(134000000000000000)),
            ("daysUntilCanDeliverConst", new PyInt(5)),
            ("daysUntilAutoMoveConst", new PyInt(20)),
            ("nearestNPCStationInfo", nearestNpcStation));
        var rows = new PyList([record]);
        var callbacks = new TestAssetSafetyReadCallbacks { CharacterItems = rows };
        var service = new StructureAssetSafetyService(callbacks);

        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetItemsInSafetyForCharacter",
            Args = [],
            Session = new ClientSession { CharacterId = 42 },
        }));

        Assert.Same(rows, result);
        Assert.Same(record, Assert.Single(result.Items));
        Assert.Equal(new PyInt(5), Field(record, "daysUntilCanDeliverConst"));
        Assert.Equal(new PyInt(20), Field(record, "daysUntilAutoMoveConst"));
        Assert.Equal(42, callbacks.LastCharacterId);
        Assert.Equal(1, callbacks.CharacterListCalls);
        Assert.Equal(1, callbacks.DueDeliveryCalls);
    }

    [Fact]
    public async Task Missing_character_returns_an_empty_asset_safety_list_without_reading()
    {
        var callbacks = new TestAssetSafetyReadCallbacks
        {
            CharacterItems = new PyList([new PyDict([])]),
        };
        var service = new StructureAssetSafetyService(callbacks);

        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetItemsInSafetyForCharacter",
            Args = [],
            Session = new ClientSession(),
        }));

        Assert.Empty(result.Items);
        Assert.Equal(0, callbacks.CharacterListCalls);
        Assert.Equal(0, callbacks.DueDeliveryCalls);
    }

[Fact]
    public async Task Check_my_docking_access_applies_per_list_granularity_and_combines_acl_results()
    {
        const long characterId = 42;
        const long corporationId = 98000001;
        const long allianceId = 99000001;
        var snapshots = new StructureDockingAccessSnapshot[]
        {
            // A corp allow on one list wins over a character block on another list.
            new(1001, corporationId, allianceId, true, true, true,
            [
                new StructureAccessListSnapshot(70001, true, [new(corporationId, true)]),
                new StructureAccessListSnapshot(70002, true, [new(characterId, false)]),
            ]),
            // Within one list, the character match overrides the broader corp allow.
            new(1002, corporationId, allianceId, true, true, true,
            [
                new StructureAccessListSnapshot(70003, true,
                    [new(corporationId, true), new(characterId, false)]),
            ]),
            new(1003, corporationId, allianceId, true, false, true,
                [new StructureAccessListSnapshot(70004, true, [new(corporationId, true)])]),
            new(1004, corporationId, allianceId, true, true, true,
                [new StructureAccessListSnapshot(70005, false, [new(corporationId, true)])]),
            new(1005, corporationId, allianceId, false, true, true,
                [new StructureAccessListSnapshot(70006, true, [new(corporationId, true)])]),
            new(1006, corporationId, allianceId, true, true, true,
                [new StructureAccessListSnapshot(70007, true, [new(allianceId, true)])]),
            new(1007, corporationId, allianceId, true, true, true,
                [new StructureAccessListSnapshot(70008, true, [new(0, true)])]),
        };
        var callbacks = new TestReadCallbacks { DockingSnapshotsResult = snapshots };
        var service = new StructureDirectoryService(callbacks);
        var session = new ClientSession { CharacterId = characterId };
        session.Set("corpid", new PyInt(corporationId));
        session.Set("allianceid", new PyInt(allianceId));

        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "CheckMyDockingAccessToStructures",
            Args = [new PyList([
                new PyInt(1001), new PyInt(1001), new PyInt(1002),
                new PyInt(1003), new PyInt(1004), new PyInt(1005),
                new PyInt(1006), new PyInt(1007), new PyInt(0),
            ])],
            Session = session,
        }));

        Assert.Collection(result.Items,
            id => Assert.Equal(new PyInt(1001), id),
            id => Assert.Equal(new PyInt(1006), id),
            id => Assert.Equal(new PyInt(1007), id));
        Assert.NotNull(callbacks.DockingRequest);
        Assert.Equal((characterId, corporationId, allianceId),
            (callbacks.DockingRequest.Value.CharacterId, callbacks.DockingRequest.Value.CorporationId, callbacks.DockingRequest.Value.AllianceId));
        Assert.Equal(new long[] { 1001, 1002, 1003, 1004, 1005, 1006, 1007 },
            callbacks.DockingRequest.Value.StructureIds);
    }

    [Fact]
    public async Task Docking_access_refuses_more_than_500_structure_ids_before_the_callback()
    {
        var callbacks = new TestReadCallbacks();
        var service = new StructureDirectoryService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        var ids = new PyList([.. Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id))]);

        var error = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(async () =>
            await service.CallAsync(new ServiceCall
            {
                Method = "CheckMyDockingAccessToStructures", Args = [ids], Session = session,
            }));

        Assert.NotNull(error);
        Assert.Null(callbacks.DockingRequest);
    }

    [Fact]
    public async Task Get_structure_info_returns_only_the_persistent_callback_result_for_can_contract_from()
    {
        var info = Py.KeyVal(("structureID", new PyInt(1001)));
        var callbacks = new TestReadCallbacks
        {
            StructureInfoResult = new PersistedStructureInfo(1001, true, info),
        };
        var service = new StructureDirectoryService(callbacks);

        var result = await service.CallAsync(Request("GetStructureInfo", new PyInt(1001)));

        Assert.Same(info, result);
        Assert.Equal(1001, callbacks.StructureInfoId);

        callbacks.StructureInfoResult = new PersistedStructureInfo(1002, false, info);
        Assert.Same(PyNone.Instance,
            await service.CallAsync(Request("GetStructureInfo", new PyInt(1002))));
    }

    [Fact]
    public async Task Corporation_asset_safety_list_uses_the_authenticated_character_and_corporation()
    {
        const long corporationId = 98000001;
        var rows = new PyList([Py.Dict(("assetWrapID", new PyLong(90000001)))]);
        var callbacks = new TestAssetSafetyReadCallbacks { CorporationItems = rows };
        var service = new StructureAssetSafetyService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(corporationId));

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetItemsInSafetyForCorp",
            Args = [],
            Session = session,
        });

        Assert.Same(rows, result);
        Assert.Equal((42L, corporationId), callbacks.CorporationItemsIdentity);
        Assert.Equal(1, callbacks.DueDeliveryCalls);
    }

    [Fact]
    public async Task Manual_personal_asset_safety_is_rejected_while_docked_at_source_structure()
    {
        const long solarSystemId = 30000142;
        const long structureId = 88000001;
        var callbacks = new TestAssetSafetyReadCallbacks();
        var service = new StructureAssetSafetyService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("stationid", new PyInt(structureId));
        var call = new ServiceCall
        {
            Method = "MovePersonalAssetsToSafety",
            Args = [new PyInt(solarSystemId), new PyInt(structureId)],
            Session = session,
        };

        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
            () => service.CallAsync(call).AsTask());
        Assert.Null(callbacks.PersonalMoveRequest);
        Assert.Equal(0, callbacks.DueDeliveryCalls);
    }

    [Fact]
    public async Task Manual_asset_safety_trigger_flows_commit_or_eject_and_never_report_failed_moves_as_success()
    {
        const long solarSystemId = 30000142;
        const long structureId = 88000001;
        var callbacks = new TestAssetSafetyReadCallbacks
        {
            PersonalMoveResult = AssetSafetyActionState.ItemsEjected,
            CorporationMoveResult = AssetSafetyActionState.CorporationWrapCreated,
        };
        var service = new StructureAssetSafetyService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(98000001));

        var personal = new ServiceCall
        {
            Method = "MovePersonalAssetsToSafety",
            Args = [new PyInt(solarSystemId), new PyInt(structureId)],
            Session = session,
        };
        Assert.Same(PyNone.Instance, await service.CallAsync(personal));
        Assert.Equal((42L, solarSystemId, structureId), callbacks.PersonalMoveRequest);

        var corporationMove = new ServiceCall
        {
            Method = "MoveCorpAssetsToSafety",
            Args = [new PyInt(solarSystemId), new PyInt(structureId)],
            Session = session,
        };
        Assert.Same(PyNone.Instance, await service.CallAsync(corporationMove));
        Assert.Equal((42L, 98000001L, solarSystemId, structureId), callbacks.CorporationMoveRequest);
        Assert.Equal(2, callbacks.DueDeliveryCalls);

        callbacks.PersonalMoveResult = AssetSafetyActionState.NoItemsToMove;
        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
            () => service.CallAsync(personal).AsTask());
    }

    [Fact]
    public async Task Wrap_names_and_same_system_delivery_targets_are_read_from_callbacks()
    {
        const long corporationId = 98000001;
        const long solarSystemId = 30000142;
        var names = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(90000001), new PyStr("Persisted wrap"))]);
        var targetRows = new PyList([Py.Dict(("itemID", new PyLong(60000001)))]);
        var nearestStation = Py.Dict(("itemID", new PyLong(60000001)));
        var targets = new PyTuple([targetRows, nearestStation]);
        var callbacks = new TestAssetSafetyReadCallbacks
        {
            WrapNames = names,
            DeliveryTargets = targets,
        };
        var service = new StructureAssetSafetyService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(corporationId));

        var wrapNames = await service.CallAsync(new ServiceCall
        {
            Method = "GetWrapNames",
            Args = [new PyList([new PyInt(90000001), new PyInt(90000001)])],
            Session = session,
        });
        Assert.Same(names, wrapNames);
        Assert.Equal(new long[] { 90000001 }, callbacks.WrapNamesRequest?.WrapIds);

        var deliveryTargets = await service.CallAsync(new ServiceCall
        {
            Method = "GetStructuresICanDeliverTo",
            Args = [new PyInt(solarSystemId)],
            Session = session,
        });
        Assert.Same(targets, deliveryTargets);
        Assert.Equal((42L, corporationId, solarSystemId), callbacks.DeliveryTargetsRequest);
        Assert.Equal(2, callbacks.DueDeliveryCalls);
    }

    [Fact]
    public async Task Delivery_uses_the_client_destination_keyword_and_rejects_unready_wraps()
    {
        const long corporationId = 98000001;
        const long wrapId = 90000001;
        const long solarSystemId = 30000142;
        const long destinationId = 60000001;
        var callbacks = new TestAssetSafetyReadCallbacks();
        var service = new StructureAssetSafetyService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(corporationId));
        var call = new ServiceCall
        {
            Method = "MoveSafetyWrapToStructure",
            Args = [new PyInt(wrapId), new PyInt(solarSystemId)],
            Kwargs = Py.Dict(("destinationID", new PyInt(destinationId))),
            Session = session,
        };

        Assert.Same(PyNone.Instance, await service.CallAsync(call));
        Assert.Equal((42L, corporationId, wrapId, solarSystemId, destinationId), callbacks.WrapDeliveryRequest);

        callbacks.DeliveryResult = AssetSafetyActionState.DeliveryNotReady;
        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
            () => service.CallAsync(call).AsTask());
    }

    [Fact]
    public async Task My_dockable_structures_returns_only_structures_with_resolved_access()
    {
        const long systemId = 30000142;
        const long corporationId = 98000001;
        const long allianceId = 99000001;
        long[] candidates = [90000001, 90000002, 90000003];
        var callbacks = new TestReadCallbacks
        {
            StructureIdsInSolarSystemResult = candidates,
            DockingSnapshotsResult =
            [
                new(90000001, corporationId, allianceId, true, true, true,
                    [new StructureAccessListSnapshot(101, true, [new StructureAccessListMember(42, true)])]),
                new(90000002, corporationId + 1, allianceId, true, true, true,
                    [new StructureAccessListSnapshot(102, true, [new StructureAccessListMember(0, false)])]),
                new(90000003, corporationId, allianceId, true, true, true,
                    [new StructureAccessListSnapshot(103, true, [new StructureAccessListMember(42, true)])]),
            ],
        };
        var service = new StructureDirectoryService(callbacks);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(corporationId));
        session.Set("allianceid", new PyInt(allianceId));

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetMyDockableStructures",
            Args = [new PyInt(systemId)],
            Session = session,
        });

        Assert.Equal(new PyList([new PyInt(90000001), new PyInt(90000003)]), result);
        Assert.Equal(systemId, callbacks.SolarSystemStructuresRequest);
        Assert.Equal((42L, corporationId, allianceId, candidates), callbacks.DockingRequest);
    }

    [Fact]
    public async Task Structure_directory_data_methods_forward_client_arguments_and_preserve_wire_shapes()
    {
        var descriptor = Descriptor.FromHeader(new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple(
                Py.Tuple(new PyStr("groupID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("itemID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("itemName"), new PyInt(0x82)),
                Py.Tuple(new PyStr("locationID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("orbitID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("connector"), new PyInt(0x0b)),
                Py.Tuple(new PyStr("x"), new PyInt(0x05)),
                Py.Tuple(new PyStr("y"), new PyInt(0x05)),
                Py.Tuple(new PyStr("z"), new PyInt(0x05)),
                Py.Tuple(new PyStr("celestialIndex"), new PyInt(0x03)),
                Py.Tuple(new PyStr("orbitIndex"), new PyInt(0x03))))),
            [],
            []));
        var mapData = new PyObjectEx(
            ObjectExKind.Ex2,
            Py.Tuple(
                Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")),
                Py.Dict(("header", descriptor.Header))),
            [new PyPackedRow(descriptor, [
                new PyInt(2020), new PyInt(35840), new PyLong(90000001), new PyWString("Beacon"),
                new PyInt(30000142), new PyInt(98000001), PyBool.False,
                new PyReal(1), new PyReal(2), new PyReal(3), PyNone.Instance, PyNone.Instance,
            ])],
            []);
        var cynoStructures = new PyList([
            new PyList([
                new PyInt(90000001), new PyInt(35840), new PyInt(98000001),
                new PyInt(30000142), new PyInt(1), new PyStr("Beacon"),
            ]),
        ]);
        var systems = Py.List(new PyInt(30000142));
        var warHeadquarters = Py.List(Py.KeyVal(
            ("typeID", new PyInt(35840)),
            ("structureID", new PyInt(90000001)),
            ("upkeepState", new PyInt(1)),
            ("wars", new PyList([])),
            ("ownerID", new PyInt(98000001)),
            ("solarSystemID", new PyInt(30000142)),
            ("itemName", new PyStr("Beacon")),
            ("inSpace", new PyInt(1))));
        var callbacks = new TestReadCallbacks
        {
            StructureMapDataResult = mapData,
            AccessibleCynoBeaconStructuresResult = cynoStructures,
            SolarSystemsWithBeaconsResult = systems,
            ValidWarHeadquartersResult = warHeadquarters,
        };
        var service = new StructureDirectoryService(callbacks);
        var mapCall = Request("GetStructureMapData", new PyInt(30000142));
        var mapResult = await service.CallAsync(mapCall);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(98000001));
        session.Set("allianceid", new PyInt(99000001));
        var cynoCall = new ServiceCall
        {
            Method = "GetMyAccessibleOnlineCynoBeaconStructures",
            Args = [],
            Session = session,
        };
        var cynoResult = await service.CallAsync(cynoCall);
        var systemsResult = await service.CallAsync(Request("GetSolarSystemsWithBeacons"));
        var warHqsCall = new ServiceCall
        {
            Method = "GetValidWarHQs",
            Args = [new PyLong(98000001)],
            Session = session,
        };
        var warHqsResult = await service.CallAsync(warHqsCall);

        Assert.Same(mapData, mapResult);
        var rowset = Assert.IsType<PyObjectEx>(mapResult);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        Assert.IsType<PyPackedRow>(Assert.Single(rowset.List));
        Assert.Equal(30000142L, callbacks.StructureMapDataSystemId);
        Assert.Same(cynoStructures, cynoResult);
        var beacon = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(cynoResult).Items));
        Assert.Equal(6, beacon.Items.Count);
        Assert.Same(cynoCall, callbacks.AccessibleCynoBeaconCall);
        Assert.Same(systems, systemsResult);
        Assert.Same(warHeadquarters, warHqsResult);
        Assert.Equal(98000001L, callbacks.ValidWarHeadquartersOwnerId);
        Assert.Same(warHqsCall, callbacks.ValidWarHeadquartersCall);
    }

    [Fact]
    public async Task Set_structure_description_forwards_the_authenticated_request()
    {
        var callbacks = new TestReadCallbacks { SetStructureDescriptionAccepted = true };
        var service = new StructureDirectoryService(callbacks);
        var call = new ServiceCall
        {
            Method = "SetStructureDescription",
            Args = [new PyInt(90000001), new PyWString("A structure description")],
            Session = new ClientSession { CharacterId = 42 },
        };

        var result = await service.CallAsync(call);

        Assert.Same(PyNone.Instance, result);
        Assert.Same(call, callbacks.LastCall);
    }

    [Fact]
    public async Task Move_eject_time_gm_requires_gml_account_role_and_accepts_only_client_timer_options()
    {
        const long wrapId = 90000001;
        var gmlRole = Eve.Services.GameMasterRoles.Gml;
        var callbacks = new TestAssetSafetyReadCallbacks();
        var service = new StructureAssetSafetyService(callbacks);
        ServiceCall Request(int days, long accountRole, long sessionRole = 0)
        {
            var session = new ClientSession { CharacterId = 42 };
            session.Set("accountRole", new PyLong(accountRole));
            session.Set("role", new PyLong(sessionRole));
            return new ServiceCall
            {
                Method = "MoveEjectTimeGM",
                Args = [new PyLong(wrapId), new PyInt(days)],
                Session = session,
            };
        }

        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
            service.CallAsync(Request(5, 0, gmlRole)).AsTask());
        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
            service.CallAsync(Request(10, gmlRole)).AsTask());
        Assert.Empty(callbacks.GmTimerRequests);

        Assert.Same(PyNone.Instance, await service.CallAsync(Request(5, gmlRole)));
        Assert.Same(PyNone.Instance, await service.CallAsync(Request(30, gmlRole)));
        Assert.Equal(new (long AssetWrapId, int Days)[] { (wrapId, 5), (wrapId, 30) }, callbacks.GmTimerRequests);
    }

[Fact]
    public async Task Structure_guests_returns_the_client_identity_dictionary_and_uses_session_structure_fallback()
    {
        const long structureId = 90000001;
        var requestedStructureIds = new List<long>();
        StructureGuestSnapshot[] guests =
        [
            new(42, 98000001, 99000001, 500001),
            new(41, 98000002, 99000002, 500002),
        ];
        var service = new StructureGuestsService((requested, _) =>
        {
            requestedStructureIds.Add(requested);
            return ValueTask.FromResult<IReadOnlyList<StructureGuestSnapshot>>(guests);
        });
        var session = new ClientSession { CharacterId = 42 };
        session.Set("structureid", new PyInt(structureId));

        Assert.Equal("structureGuests", service.Name);
        Assert.True(service.HasMethod("GetGuests"));
        Assert.False(service.HasMethod("UnknownMethod"));

        var explicitResult = await service.CallAsync(new ServiceCall
        {
            Method = "GetGuests",
            Args = [new PyInt(structureId + 1)],
            Session = session,
        });
        var expected = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyInt(41), new PyList([new PyInt(98000002), new PyInt(99000002), new PyInt(500002)])),
            new KeyValuePair<PyValue, PyValue>(
                new PyInt(42), new PyList([new PyInt(98000001), new PyInt(99000001), new PyInt(500001)])),
        ]);
        Assert.Equal(expected, explicitResult);
        Assert.Equal(structureId + 1, requestedStructureIds[0]);

        var fallbackResult = await service.CallAsync(new ServiceCall
        {
            Method = "GetGuests",
            Args = [],
            Session = session,
        });
        Assert.Equal(expected, fallbackResult);
        Assert.Equal(structureId, requestedStructureIds[1]);

        var missing = await service.CallAsync(new ServiceCall
        {
            Method = "GetGuests",
            Args = [],
            Session = new ClientSession(),
        });
        Assert.Empty(Assert.IsType<PyDict>(missing).Entries);
        Assert.Equal(2, requestedStructureIds.Count);
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "UnknownMethod",
            Args = [],
            Session = session,
        }));
    }

    private static ServiceCall Request(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = null!,
    };

    private static PyValue Field(PyValue row, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries
            .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    private sealed class TestReadCallbacks : IStructureDirectoryReadCallbacks
    {
        public PyValue JumpBridgesResult { get; init; } = Py.List();
        public PyValue NearbyJumpBridgesResult { get; init; } = Py.List();
        public PyValue StructureDescriptionResult { get; init; } = PyNone.Instance;
        public PyValue StructureMapDataResult { get; init; } = Py.List();
        public PyValue AccessibleCynoBeaconStructuresResult { get; init; } = Py.List();
        public PyValue SolarSystemsWithBeaconsResult { get; init; } = Py.List();
        public PyValue ValidWarHeadquartersResult { get; init; } = Py.List();
        public bool SetStructureDescriptionAccepted { get; init; }
        public IReadOnlyList<long> StructureIdsInSolarSystemResult { get; init; } = [];
        public PersistedStructureInfo? StructureInfoResult { get; set; }
        public IReadOnlyList<StructureDockingAccessSnapshot> DockingSnapshotsResult { get; init; } = [];
        public PyDict CharacterStructuresResult { get; init; } = new([]);
        public PyDict CorporationStructuresResult { get; init; } = new([]);
        public long? StructureInfoId { get; private set; }
        public long? SolarSystemStructuresRequest { get; private set; }
        public long? StructureMapDataSystemId { get; private set; }
        public long? ValidWarHeadquartersOwnerId { get; private set; }
        public (long CharacterId, long CorporationId, long AllianceId, IReadOnlyList<long> StructureIds)? DockingRequest { get; private set; }
        public long? CharacterStructuresCharacterId { get; private set; }
        public (long CharacterId, long CorporationId)? CorporationStructuresIdentity { get; private set; }
        public ServiceCall? LastCall { get; private set; }
        public ServiceCall? AccessibleCynoBeaconCall { get; private set; }
        public ServiceCall? ValidWarHeadquartersCall { get; private set; }

        public ValueTask<PyValue> GetJumpBridgesWithMyAccessAsync(ServiceCall call) =>
            Complete(call, JumpBridgesResult);

        public ValueTask<PyValue> GetNearbyJumpBridgesAsync(ServiceCall call) =>
            Complete(call, NearbyJumpBridgesResult);

        public ValueTask<PyValue> GetStructureDescriptionAsync(ServiceCall call) =>
            Complete(call, StructureDescriptionResult);

        public ValueTask<PyValue> GetMyAccessibleOnlineCynoBeaconStructuresAsync(ServiceCall call)
        {
            AccessibleCynoBeaconCall = call;
            return Complete(call, AccessibleCynoBeaconStructuresResult);
        }

        public ValueTask<PyValue> GetSolarSystemsWithBeaconsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(SolarSystemsWithBeaconsResult);

        public ValueTask<PyValue> GetValidWarHQsAsync(
            ServiceCall call, long ownerId, CancellationToken cancellationToken)
        {
            ValidWarHeadquartersCall = call;
            ValidWarHeadquartersOwnerId = ownerId;
            return ValueTask.FromResult(ValidWarHeadquartersResult);
        }

        public ValueTask<PyValue> GetStructureMapDataAsync(long solarSystemId, CancellationToken cancellationToken)
        {
            StructureMapDataSystemId = solarSystemId;
            return ValueTask.FromResult(StructureMapDataResult);
        }

        public ValueTask<bool> SetStructureDescriptionAsync(ServiceCall call)
        {
            LastCall = call;
            return ValueTask.FromResult(SetStructureDescriptionAccepted);
        }

        public ValueTask<IReadOnlyList<long>> GetStructureIdsInSolarSystemAsync(
            long solarSystemId, CancellationToken cancellationToken)
        {
            SolarSystemStructuresRequest = solarSystemId;
            return ValueTask.FromResult(StructureIdsInSolarSystemResult);
        }

        public ValueTask<PersistedStructureInfo?> GetStructureInfoAsync(long structureId, CancellationToken cancellationToken)
        {
            StructureInfoId = structureId;
            return ValueTask.FromResult(StructureInfoResult);
        }

        public ValueTask<IReadOnlyList<StructureDockingAccessSnapshot>> GetDockingAccessSnapshotsAsync(
            long characterId,
            long corporationId,
            long allianceId,
            IReadOnlyList<long> structureIds,
            CancellationToken cancellationToken)
        {
            DockingRequest = (characterId, corporationId, allianceId, structureIds);
            return ValueTask.FromResult(DockingSnapshotsResult);
        }

        public ValueTask<PyDict> GetMyCharacterStructuresAsync(long characterId, CancellationToken cancellationToken)
        {
            CharacterStructuresCharacterId = characterId;
            return ValueTask.FromResult(CharacterStructuresResult);
        }

        public ValueTask<PyDict> GetMyCorporationStructuresAsync(
            long characterId,
            long corporationId,
            CancellationToken cancellationToken)
        {
            CorporationStructuresIdentity = (characterId, corporationId);
            return ValueTask.FromResult(CorporationStructuresResult);
        }

        private ValueTask<PyValue> Complete(ServiceCall call, PyValue result)
        {
            LastCall = call;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestAssetSafetyReadCallbacks : IStructureAssetSafetyReadCallbacks
    {
        public PyList CharacterItems { get; init; } = new([]);
        public PyList CorporationItems { get; init; } = new([]);
        public PyDict WrapNames { get; init; } = new([]);
        public PyTuple DeliveryTargets { get; init; } = new([new PyList([]), PyNone.Instance]);
        public AssetSafetyActionState PersonalMoveResult { get; set; } = AssetSafetyActionState.PersonalWrapCreated;
        public AssetSafetyActionState CorporationMoveResult { get; init; } = AssetSafetyActionState.CorporationWrapCreated;
        public AssetSafetyActionState DeliveryResult { get; set; } = AssetSafetyActionState.WrapDelivered;
        public long? LastCharacterId { get; private set; }
        public (long CharacterId, long CorporationId)? CorporationItemsIdentity { get; private set; }
        public (long CharacterId, long CorporationId, IReadOnlyList<long> WrapIds)? WrapNamesRequest { get; private set; }
        public (long CharacterId, long CorporationId, long SolarSystemId)? DeliveryTargetsRequest { get; private set; }
        public (long CharacterId, long SolarSystemId, long StructureId)? PersonalMoveRequest { get; private set; }
        public (long CharacterId, long CorporationId, long SolarSystemId, long StructureId)? CorporationMoveRequest { get; private set; }
        public (long CharacterId, long CorporationId, long WrapId, long SolarSystemId, long DestinationId)? WrapDeliveryRequest { get; private set; }
        public List<(long AssetWrapId, int Days)> GmTimerRequests { get; } = [];
        public int DueDeliveryCalls { get; private set; }
        public int CharacterListCalls { get; private set; }

        public ValueTask<PyList> GetItemsInSafetyForCharacterAsync(long characterId, CancellationToken cancellationToken)
        {
            LastCharacterId = characterId;
            CharacterListCalls++;
            return ValueTask.FromResult(CharacterItems);
        }

        public ValueTask<PyList> GetItemsInSafetyForCorpAsync(long characterId, long corporationId, CancellationToken cancellationToken)
        {
            CorporationItemsIdentity = (characterId, corporationId);
            return ValueTask.FromResult(CorporationItems);
        }

        public ValueTask<PyDict> GetWrapNamesAsync(
            long characterId, long corporationId, IReadOnlyList<long> wrapIds, CancellationToken cancellationToken)
        {
            WrapNamesRequest = (characterId, corporationId, wrapIds);
            return ValueTask.FromResult(WrapNames);
        }

        public ValueTask<PyTuple> GetStructuresICanDeliverToAsync(
            long characterId, long corporationId, long solarSystemId, CancellationToken cancellationToken)
        {
            DeliveryTargetsRequest = (characterId, corporationId, solarSystemId);
            return ValueTask.FromResult(DeliveryTargets);
        }

        public ValueTask<AssetSafetyActionState> MovePersonalAssetsToSafetyAsync(
            ServiceCall call, long solarSystemId, long structureId, CancellationToken cancellationToken)
        {
            PersonalMoveRequest = (call.Session.CharacterId ?? 0, solarSystemId, structureId);
            return ValueTask.FromResult(PersonalMoveResult);
        }

        public ValueTask<AssetSafetyActionState> MoveCorpAssetsToSafetyAsync(
            ServiceCall call, long solarSystemId, long structureId, CancellationToken cancellationToken)
        {
            CorporationMoveRequest = (
                call.Session.CharacterId ?? 0,
                call.Session.GetInt("corpid") ?? 0,
                solarSystemId,
                structureId);
            return ValueTask.FromResult(CorporationMoveResult);
        }

        public ValueTask<AssetSafetyActionState> MoveSafetyWrapToStructureAsync(
            ServiceCall call, long assetWrapId, long solarSystemId, long destinationId, CancellationToken cancellationToken)
        {
            WrapDeliveryRequest = (
                call.Session.CharacterId ?? 0,
                call.Session.GetInt("corpid") ?? 0,
                assetWrapId,
                solarSystemId,
                destinationId);
            return ValueTask.FromResult(DeliveryResult);
        }

        public ValueTask<bool> MoveEjectTimeGMAsync(
            ServiceCall call, long assetWrapId, int days, CancellationToken cancellationToken)
        {
            GmTimerRequests.Add((assetWrapId, days));
            return ValueTask.FromResult(true);
        }

        public ValueTask ProcessDueAutomaticDeliveriesAsync(CancellationToken cancellationToken)
        {
            DueDeliveryCalls++;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class StructureSettingsServiceTests
{
    [Fact]
    public void Exposes_only_the_client_structure_settings_reads()
    {
        var service = new StructureSettingsService(new TestReadCallbacks());

        Assert.Equal("structureSettings", service.Name);
        Assert.True(service.HasMethod("CharacterGetServices"));
        Assert.True(service.HasMethod("CharacterHasService"));
        Assert.True(service.HasMethod("CharacterHasSetting"));
        Assert.True(service.HasMethod("CharacterCheckService"));
        Assert.True(service.HasMethod("CharacterGetService"));
    }

    [Fact]
    public async Task Character_get_services_returns_the_service_id_map_for_the_session_character()
    {
        var services = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(501), PyBool.True),
        ]);
        var callbacks = new TestReadCallbacks { ServicesResult = services };
        var service = new StructureSettingsService(callbacks);

        var result = await service.CallAsync(Request("CharacterGetServices", 42, new PyInt(90000001)));

        Assert.Same(services, result);
        Assert.Equal((42L, 90000001L), callbacks.ServicesCall);
    }

    [Fact]
    public async Task Has_service_and_has_setting_forward_ids_and_map_results_to_python_bools()
    {
        var callbacks = new TestReadCallbacks { HasServiceResult = true, HasSettingResult = false };
        var service = new StructureSettingsService(callbacks);

        var hasService = await service.CallAsync(Request(
            "CharacterHasService", 42, new PyInt(90000001), new PyInt(-1)));
        var hasSetting = await service.CallAsync(Request(
            "CharacterHasSetting", 42, new PyInt(90000001), new PyInt(701)));

        Assert.Same(PyBool.True, hasService);
        Assert.Same(PyBool.False, hasSetting);
        Assert.Equal((42L, 90000001L, -1L), callbacks.HasServiceCall);
        Assert.Equal((42L, 90000001L, 701L), callbacks.HasSettingCall);
    }

    [Fact]
    public async Task Missing_character_or_invalid_ids_fail_closed_without_reading_structure_state()
    {
        var callbacks = new TestReadCallbacks();
        var service = new StructureSettingsService(callbacks);

        var services = await service.CallAsync(Request("CharacterGetServices", null, new PyInt(90000001)));
        var hasService = await service.CallAsync(Request(
            "CharacterHasService", 42, new PyInt(90000001), new PyInt(0)));
        var hasSetting = await service.CallAsync(Request(
            "CharacterHasSetting", 42, new PyInt(90000001), new PyInt(0)));

        Assert.Empty(Assert.IsType<PyDict>(services).Entries);
        Assert.Same(PyBool.False, hasService);
        Assert.Same(PyBool.False, hasSetting);
        Assert.Null(callbacks.ServicesCall);
        Assert.Null(callbacks.HasServiceCall);
        Assert.Null(callbacks.HasSettingCall);
    }

    [Fact]
    public async Task Unknown_methods_return_none_without_calling_read_callbacks()
    {
        var callbacks = new TestReadCallbacks();
        var service = new StructureSettingsService(callbacks);

        var result = await service.CallAsync(Request("UnknownStructureSettingsMethod", 42, new PyInt(90000001)));

        Assert.Same(PyNone.Instance, result);
        Assert.Null(callbacks.ServicesCall);
        Assert.Null(callbacks.HasServiceCall);
        Assert.Null(callbacks.HasSettingCall);
    }

    [Fact]
    public async Task Character_get_service_returns_the_requested_service_value()
    {
        var taxRate = new PyReal(0.025);
        var callbacks = new TestReadCallbacks { CharacterGetServiceResult = taxRate };
        var service = new StructureSettingsService(callbacks);

        var result = await service.CallAsync(Request(
            "CharacterGetService", 42, new PyInt(90000001), new PyInt(5)));

        Assert.Same(taxRate, result);
        Assert.Equal((42L, 90000001L, 5L), callbacks.CharacterGetServiceCall);
    }

    [Fact]
    public async Task Character_check_service_returns_true_and_errors_when_unavailable()
    {
        var availableCallbacks = new TestReadCallbacks { HasServiceResult = true };
        var availableService = new StructureSettingsService(availableCallbacks);

        var result = await availableService.CallAsync(Request(
            "CharacterCheckService", 42, new PyInt(90000001), new PyInt(5)));

        Assert.Same(PyBool.True, result);
        Assert.Equal((42L, 90000001L, 5L), availableCallbacks.HasServiceCall);

        var unavailableService = new StructureSettingsService(new TestReadCallbacks());
        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
            unavailableService.CallAsync(Request(
                "CharacterCheckService", 42, new PyInt(90000001), new PyInt(5))).AsTask());
    }

    private static ServiceCall Request(string method, long? characterId, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new ClientSession { CharacterId = characterId },
    };

    private sealed class TestReadCallbacks : IStructureSettingsReadCallbacks
    {
        public PyDict ServicesResult { get; init; } = new([]);
        public PyValue CharacterGetServiceResult { get; init; } = PyNone.Instance;
        public bool HasServiceResult { get; init; }
        public bool HasSettingResult { get; init; }
        public (long CharacterId, long StructureId)? ServicesCall { get; private set; }
        public (long CharacterId, long StructureId, long ServiceId)? CharacterGetServiceCall { get; private set; }
        public (long CharacterId, long StructureId, long ServiceId)? HasServiceCall { get; private set; }
        public (long CharacterId, long StructureId, long SettingId)? HasSettingCall { get; private set; }

        public ValueTask<PyDict> CharacterGetServicesAsync(long characterId, long structureId, CancellationToken cancellationToken)
        {
            ServicesCall = (characterId, structureId);
            return ValueTask.FromResult(ServicesResult);
        }

        public ValueTask<PyValue> CharacterGetServiceAsync(
            long characterId, long structureId, long serviceId, CancellationToken cancellationToken)
        {
            CharacterGetServiceCall = (characterId, structureId, serviceId);
            return ValueTask.FromResult(CharacterGetServiceResult);
        }

        public ValueTask<bool> CharacterHasServiceAsync(long characterId, long structureId, long serviceId, CancellationToken cancellationToken)
        {
            HasServiceCall = (characterId, structureId, serviceId);
            return ValueTask.FromResult(HasServiceResult);
        }

        public ValueTask<bool> CharacterHasSettingAsync(long characterId, long structureId, long settingId, CancellationToken cancellationToken)
        {
            HasSettingCall = (characterId, structureId, settingId);
            return ValueTask.FromResult(HasSettingResult);
        }
    }
}
