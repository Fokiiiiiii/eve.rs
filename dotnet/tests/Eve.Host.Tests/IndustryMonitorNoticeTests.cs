using Eve.Corporations;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class IndustryMonitorNoticeTests
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
        public void Clear() => Packets.Clear();
    }

    private sealed class InventoryMutationService(Action<long> mutate) : IService
    {
        public string Name => "invbroker";
        public bool HasMethod(string method) => method == "Add";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            var quantity = Assert.IsType<PyInt>(call.Arg(0)).Value;
            mutate(quantity);
            call.Session.SendNotification("OnItemChange", "clientID", new PyInt(44), new PyDict([]), PyNone.Instance);
            return ValueTask.FromResult<PyValue>(new PyInt(44));
        }
    }

    private static ClientSession NewSession(long charId, long clientId, Capture capture)
    {
        var session = new ClientSession(capture) { ClientId = clientId, UserId = charId, NodeId = 65_450 };
        session.CharacterId = charId;
        return session;
    }

    private static PyValue MonitorRequest(long inputLocationId) => Py.KeyVal(
        ("blueprintID", new PyInt(10)), ("blueprintTypeID", new PyInt(681)),
        ("activityID", new PyInt(1)), ("facilityID", new PyInt(20)),
        ("inputLocation", Py.KeyVal(
            ("itemID", new PyInt(inputLocationId)), ("ownerID", new PyInt(20)),
            ("flagID", new PyInt(4)), ("canTake", PyBool.True))));

    private static PyValue MonitorRequest(long blueprintId, long facilityId, long inputLocationId, int flagId,
        long? ownerId, bool canTake, long clientCharacterId, long clientCorporationId)
    {
        var input = new List<(string, PyValue)>
        {
            ("itemID", new PyInt(inputLocationId)),
            ("flagID", new PyInt(flagId)),
            ("canTake", canTake ? PyBool.True : PyBool.False),
        };
        if (ownerId is { } suppliedOwnerId)
            input.Add(("ownerID", new PyInt(suppliedOwnerId)));
        return Py.KeyVal(
            ("blueprintID", new PyInt(blueprintId)), ("blueprintTypeID", new PyInt(681)),
            ("activityID", new PyInt(1)), ("facilityID", new PyInt(facilityId)),
            ("characterID", new PyInt(clientCharacterId)), ("corporationID", new PyInt(clientCorporationId)),
            ("inputLocation", Py.KeyVal(input.ToArray())));
    }

    private static PyValue Request(long callId, string service, string method, params PyValue[] args)
    {
        var body = new PyTuple([
            PyNone.Instance,
            new PyStr(method),
            new PyTuple(args),
            PyNone.Instance,
        ]);
        var payload = new PyTuple([
            new PyTuple([PyBool.True, new PySubStream(body)]),
        ]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: service,
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq),
                source.Encode(),
                destination.Encode(),
                new PyInt(7),
                payload,
                new PyDict([]),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
            ]));
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }

    private static bool IsNotification(PyValue packet) =>
        packet is PyObject { Args: PyTuple args } &&
        args.Items.Count > 0 &&
        args.Items[0] is PyInt { Value: MachoMessage.Notification };

    private static (string Name, PyValue IdType, PyValue[] Args, PyValue UserId) Notification(PyValue packet)
    {
        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
        var payload = Assert.IsType<PyTuple>(state[4]).Items[0];
        var buffer = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(payload).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(buffer.Value));
        var eventArgs = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(decoded.Items[1]).Items[1]);
        return (Assert.IsType<PyStr>(address[1]).Value, address[3], [.. eventArgs.Items], state[3]);
    }

    private static PacketDispatcher DispatcherFor(IndustryMonitorRpcService monitors, IService inventory)
    {
        var registry = new ServiceRegistry();
        registry.Register(monitors);
        registry.Register(inventory);
        return new PacketDispatcher(registry);
    }

    private static PacketDispatcher DispatcherFor(IndustryMonitorRpcService monitors)
    {
        var registry = new ServiceRegistry();
        registry.Register(monitors);
        return new PacketDispatcher(registry);
    }

    private static async Task<PyTuple> ConnectAsync(PacketDispatcher dispatcher, ClientSession session,
        Capture capture, PyValue request, long callId)
    {
        Assert.True(await dispatcher.DispatchAsync(Request(callId, "industryMonitor", "ConnectJob", request), session));
        return Assert.IsType<PyTuple>(ResponseResult(capture.Packets[^1]));
    }

    [Fact]
    public async Task Database_backed_personal_monitor_uses_session_owner_and_facility_for_materials_and_notices()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var anotherCharacter = await rig.Owner();
        var blueprintId = await rig.Give(owner, 681, 1, blueprint: true);
        await rig.Give(owner, 34, 5);
        await rig.CreateItem(new NewItem(34, owner, rig.Station.Id + 1, ItemFlags.Hangar, 13));
        await rig.CreateItem(new NewItem(35, owner, rig.Station.Id, ItemFlags.Cargo, 17));
        await rig.Sde.BuildAsync();
        var itemTypes = new ItemTypeCatalog(rig.Sde);
        var monitors = new IndustryMonitorRpcService(items: rig.ItemStore, itemTypes: itemTypes,
            authorizedInputLocation: HostInventoryServiceFactory.IndustryMonitorLocationOf(
                rig.ItemStore, itemTypes, rig.Corporations, rig.Offices));
        var dispatcher = DispatcherFor(monitors);
        var ownerCapture = new Capture();
        var zeroOwnerCapture = new Capture();
        var deniedCapture = new Capture();
        var wrongFacilityCapture = new Capture();
        var wrongLocationCapture = new Capture();
        var ownerSession = NewSession(owner, 3001, ownerCapture);
        var zeroOwnerSession = NewSession(owner, 3002, zeroOwnerCapture);
        var deniedSession = NewSession(anotherCharacter, 3003, deniedCapture);
        var wrongFacilitySession = NewSession(owner, 3004, wrongFacilityCapture);
        var wrongLocationSession = NewSession(owner, 3005, wrongLocationCapture);

        var missingOwner = await ConnectAsync(dispatcher, ownerSession, ownerCapture,
            MonitorRequest(blueprintId, rig.Station.Id, rig.Station.Id, ItemFlags.Hangar, null, false,
                anotherCharacter, 98_000_001), 101);
        var zeroOwner = await ConnectAsync(dispatcher, zeroOwnerSession, zeroOwnerCapture,
            MonitorRequest(blueprintId, rig.Station.Id, rig.Station.Id, ItemFlags.Hangar, 0, false,
                anotherCharacter, 98_000_001), 102);
        foreach (var response in new[] { missingOwner, zeroOwner })
        {
            Assert.Equal(2, response.Items.Count);
            Assert.Contains(Assert.IsType<PyDict>(response.Items[1]).Entries,
                entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(5));
        }

        var forgedOwner = await ConnectAsync(dispatcher, deniedSession, deniedCapture,
            MonitorRequest(blueprintId, rig.Station.Id, rig.Station.Id, ItemFlags.Hangar, 0, true,
                owner, 98_000_001), 103);
        Assert.Empty(Assert.IsType<PyDict>(forgedOwner.Items[1]).Entries);

        var wrongFacility = await ConnectAsync(dispatcher, wrongFacilitySession, wrongFacilityCapture,
            MonitorRequest(blueprintId, rig.Station.Id + 1, rig.Station.Id, ItemFlags.Hangar, owner, true,
                owner, 0), 104);
        var wrongLocation = await ConnectAsync(dispatcher, wrongLocationSession, wrongLocationCapture,
            MonitorRequest(blueprintId, rig.Station.Id, rig.Station.Id, ItemFlags.Cargo, owner, true,
                owner, 0), 105);
        Assert.Empty(Assert.IsType<PyDict>(wrongFacility.Items[1]).Entries);
        Assert.Empty(Assert.IsType<PyDict>(wrongLocation.Items[1]).Entries);

        foreach (var capture in new[] { ownerCapture, zeroOwnerCapture, deniedCapture, wrongFacilityCapture, wrongLocationCapture })
            capture.Clear();
        await rig.CreateItem(new NewItem(34, owner, rig.Station.Id, ItemFlags.Hangar, 2));
        await monitors.InventoryChangedAsync(ownerSession, CancellationToken.None);
        await monitors.InventoryChangedAsync(zeroOwnerSession, CancellationToken.None);
        await monitors.InventoryChangedAsync(deniedSession, CancellationToken.None);
        await monitors.InventoryChangedAsync(wrongFacilitySession, CancellationToken.None);
        await monitors.InventoryChangedAsync(wrongLocationSession, CancellationToken.None);

        foreach (var (capture, expectedId) in new[]
        {
            (ownerCapture, missingOwner.Items[0]),
            (zeroOwnerCapture, zeroOwner.Items[0]),
        })
        {
            var notice = Notification(Assert.Single(capture.Packets, IsNotification));
            Assert.Equal("OnIndustryMaterials", notice.Name);
            Assert.Equal(new PyStr("clientID"), notice.IdType);
            Assert.Equal(new PyInt(owner), notice.UserId);
            Assert.Equal(2, notice.Args.Length);
            Assert.Equal(expectedId, notice.Args[0]);
            Assert.Contains(Assert.IsType<PyDict>(notice.Args[1]).Entries,
                entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(7));
        }
        Assert.DoesNotContain(deniedCapture.Packets, IsNotification);
        Assert.DoesNotContain(wrongFacilityCapture.Packets, IsNotification);
        Assert.DoesNotContain(wrongLocationCapture.Packets, IsNotification);
    }

    [Fact]
    public async Task Database_backed_corporation_monitor_requires_factory_manager_and_query_take_for_the_selected_division()
    {
        await using var rig = await IndustryRig.CreateAsync(includeOffices: true);
        var actor = await rig.Owner(5_000_000m);
        var anotherCharacter = await rig.Owner();
        var corporationId = await rig.Corporation(actor);
        var officeFolderId = await rig.CreateItem(new NewItem(CorporationOfficeStore.OfficeFolderTypeId,
            corporationId, rig.Station.Id, ItemFlags.Hangar, 1, true));
        var office = await rig.RentOffice(corporationId, officeFolderId);
        var blueprintId = await rig.CreateItem(new NewItem(681, corporationId, officeFolderId,
            ItemFlags.CorpSAG1, 1, true));
        await rig.CreateItem(new NewItem(34, corporationId, officeFolderId, ItemFlags.CorpSAG1, 5));
        await rig.CreateItem(new NewItem(35, corporationId, officeFolderId, ItemFlags.CorpSAG1, 3));
        await rig.CreateItem(new NewItem(34, corporationId, officeFolderId, ItemFlags.CorpSAG2, 99));
        await rig.Sde.BuildAsync();
        var itemTypes = new ItemTypeCatalog(rig.Sde);
        var monitors = new IndustryMonitorRpcService(items: rig.ItemStore, itemTypes: itemTypes,
            authorizedInputLocation: HostInventoryServiceFactory.IndustryMonitorLocationOf(
                rig.ItemStore, itemTypes, rig.Corporations, rig.Offices));
        var dispatcher = DispatcherFor(monitors);
        var queryOnlyCapture = new Capture();
        var takeOnlyCapture = new Capture();
        var noManagerCapture = new Capture();
        var allowedCapture = new Capture();
        var noMemberCapture = new Capture();
        var otherDivisionCapture = new Capture();
        var wrongFacilityCapture = new Capture();
        var queryOnlySession = NewSession(actor, 3101, queryOnlyCapture);
        var takeOnlySession = NewSession(actor, 3102, takeOnlyCapture);
        var noManagerSession = NewSession(actor, 3103, noManagerCapture);
        var allowedSession = NewSession(actor, 3104, allowedCapture);
        var noMemberSession = NewSession(anotherCharacter, 3105, noMemberCapture);
        var otherDivisionSession = NewSession(actor, 3106, otherDivisionCapture);
        var wrongFacilitySession = NewSession(actor, 3107, wrongFacilityCapture);
        const long factoryManager = CorporationRoles.FactoryManager;
        const long queryDivisionOne = 1L << 20;
        const long takeDivisionOne = 1L << 13;

        await rig.SetCorporationRoles(actor, factoryManager, queryDivisionOne);
        var queryOnly = await ConnectAsync(dispatcher, queryOnlySession, queryOnlyCapture,
            MonitorRequest(blueprintId, rig.Station.Id, office.OfficeId, ItemFlags.CorpSAG1,
                corporationId, true, anotherCharacter, 98_000_002), 201);
        Assert.Empty(Assert.IsType<PyDict>(queryOnly.Items[1]).Entries);

        await rig.SetCorporationRoles(actor, factoryManager, takeDivisionOne);
        var takeOnly = await ConnectAsync(dispatcher, takeOnlySession, takeOnlyCapture,
            MonitorRequest(blueprintId, rig.Station.Id, office.OfficeId, ItemFlags.CorpSAG1,
                corporationId, true, anotherCharacter, 98_000_002), 202);
        Assert.Empty(Assert.IsType<PyDict>(takeOnly.Items[1]).Entries);

        await rig.SetCorporationRoles(actor, 0, queryDivisionOne | takeDivisionOne);
        var noManager = await ConnectAsync(dispatcher, noManagerSession, noManagerCapture,
            MonitorRequest(blueprintId, rig.Station.Id, office.OfficeId, ItemFlags.CorpSAG1,
                0, true, anotherCharacter, 98_000_002), 203);
        Assert.Empty(Assert.IsType<PyDict>(noManager.Items[1]).Entries);

        await rig.SetCorporationRoles(actor, factoryManager, queryDivisionOne | takeDivisionOne);
        var allowed = await ConnectAsync(dispatcher, allowedSession, allowedCapture,
            MonitorRequest(blueprintId, rig.Station.Id, office.OfficeId, ItemFlags.CorpSAG1,
                0, true, anotherCharacter, 98_000_002), 204);
        var allowedMaterials = Assert.IsType<PyDict>(allowed.Items[1]);
        Assert.Equal(2, allowedMaterials.Entries.Count);
        Assert.Contains(allowedMaterials.Entries, entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(5));
        Assert.Contains(allowedMaterials.Entries, entry => entry.Key == new PyInt(35) && entry.Value == new PyInt(3));

        var noMember = await ConnectAsync(dispatcher, noMemberSession, noMemberCapture,
            MonitorRequest(blueprintId, rig.Station.Id, office.OfficeId, ItemFlags.CorpSAG1,
                corporationId, true, actor, corporationId), 205);
        var otherDivision = await ConnectAsync(dispatcher, otherDivisionSession, otherDivisionCapture,
            MonitorRequest(blueprintId, rig.Station.Id, office.OfficeId, ItemFlags.CorpSAG2,
                corporationId, true, actor, corporationId), 206);
        var wrongFacility = await ConnectAsync(dispatcher, wrongFacilitySession, wrongFacilityCapture,
            MonitorRequest(blueprintId, rig.Station.Id + 1, office.OfficeId, ItemFlags.CorpSAG1,
                corporationId, true, actor, corporationId), 207);
        Assert.Empty(Assert.IsType<PyDict>(noMember.Items[1]).Entries);
        Assert.Empty(Assert.IsType<PyDict>(otherDivision.Items[1]).Entries);
        Assert.Empty(Assert.IsType<PyDict>(wrongFacility.Items[1]).Entries);

        foreach (var capture in new[] { queryOnlyCapture, takeOnlyCapture, noManagerCapture, allowedCapture,
                     noMemberCapture, otherDivisionCapture, wrongFacilityCapture })
            capture.Clear();
        await rig.CreateItem(new NewItem(34, corporationId, officeFolderId, ItemFlags.CorpSAG1, 2));
        await monitors.InventoryChangedAsync(allowedSession, CancellationToken.None);
        await monitors.InventoryChangedAsync(noMemberSession, CancellationToken.None);
        var allowedNotice = Notification(Assert.Single(allowedCapture.Packets, IsNotification));
        Assert.Equal("OnIndustryMaterials", allowedNotice.Name);
        Assert.Equal(allowed.Items[0], allowedNotice.Args[0]);
        var notifiedMaterials = Assert.IsType<PyDict>(allowedNotice.Args[1]);
        Assert.Contains(notifiedMaterials.Entries, entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(7));
        Assert.Contains(notifiedMaterials.Entries, entry => entry.Key == new PyInt(35) && entry.Value == new PyInt(3));
        Assert.DoesNotContain(noMemberCapture.Packets, IsNotification);

        await rig.SetCorporationRoles(actor, factoryManager, queryDivisionOne);
        allowedCapture.Clear();
        await monitors.InventoryChangedAsync(allowedSession, CancellationToken.None);
        var revokedNotice = Notification(Assert.Single(allowedCapture.Packets, IsNotification));
        Assert.Empty(Assert.IsType<PyDict>(revokedNotice.Args[1]).Entries);
    }

    [Fact]
    public async Task Host_inventory_dispatch_publishes_changed_materials_once_after_item_change_and_isolates_sessions()
    {
        var quantities = new Dictionary<long, long> { [900] = 5, [901] = 7 };
        var monitors = new IndustryMonitorRpcService(availableMaterials: (request, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(
                new Dictionary<int, long> { [34] = quantities[request.InputLocation!.ItemId] }));
        var capture1 = new Capture();
        var capture2 = new Capture();
        var session1 = NewSession(20, 2001, capture1);
        var session2 = NewSession(21, 2002, capture2);
        var inventory = new IndustryInventoryChangePublisher(
            new InventoryMutationService(quantity => quantities[900] = quantity), monitors);
        var dispatcher = DispatcherFor(monitors, inventory);

        Assert.True(await dispatcher.DispatchAsync(Request(1, "industryMonitor", "ConnectJob", MonitorRequest(900)), session1));
        var first = Assert.IsType<PyTuple>(ResponseResult(capture1.Packets[^1]));
        Assert.True(await dispatcher.DispatchAsync(Request(2, "industryMonitor", "ConnectJob", MonitorRequest(901)), session2));
        var second = Assert.IsType<PyTuple>(ResponseResult(capture2.Packets[^1]));
        Assert.Equal(new PyInt(1), first.Items[0]);
        Assert.Equal(new PyInt(2), second.Items[0]);
        capture1.Clear();
        capture2.Clear();

        Assert.True(await dispatcher.DispatchAsync(Request(3, "invbroker", "Add", new PyInt(8)), session1));

        var notices = capture1.Packets.Where(IsNotification).Select(Notification).ToArray();
        Assert.Equal(["OnItemChange", "OnIndustryMaterials"], notices.Select(notice => notice.Name));
        var notice = notices[1];
        Assert.Equal(new PyStr("clientID"), notice.IdType);
        Assert.Equal(new PyInt(20), notice.UserId);
        Assert.Equal(first.Items[0], notice.Args[0]);
        Assert.Contains(Assert.IsType<PyDict>(notice.Args[1]).Entries,
            entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(8));
        Assert.DoesNotContain(capture2.Packets, IsNotification);

        capture1.Clear();
        await dispatcher.DispatchAsync(Request(4, "invbroker", "Add", new PyInt(8)), session1);
        Assert.Equal(["OnItemChange"], capture1.Packets.Where(IsNotification).Select(packet => Notification(packet).Name));
        Assert.DoesNotContain(capture2.Packets, IsNotification);
    }

    [Fact]
    public void Composition_resolves_the_inner_inventory_broker_through_the_monitor_publisher()
    {
        var inventory = new InventoryMutationService(_ => { });
        var monitors = new IndustryMonitorRpcService(availableMaterials: (_, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long>()));
        var publishedInventory = new IndustryInventoryChangePublisher(inventory, monitors);

        Assert.Same(inventory,
            CharacterServicesComposition.SingleRegisteredService<InventoryMutationService>([publishedInventory]));
    }

    [Fact]
    public async Task Disconnect_and_reconnect_replace_the_session_monitor_through_host_dispatch()
    {
        var quantity = 5L;
        var monitors = new IndustryMonitorRpcService(availableMaterials: (_, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long> { [34] = quantity }));
        var capture = new Capture();
        var session = NewSession(20, 2001, capture);
        var inventory = new IndustryInventoryChangePublisher(
            new InventoryMutationService(value => quantity = value), monitors);
        var dispatcher = DispatcherFor(monitors, inventory);

        await dispatcher.DispatchAsync(Request(1, "industryMonitor", "ConnectJob", MonitorRequest(900)), session);
        var first = Assert.IsType<PyTuple>(ResponseResult(capture.Packets[^1]));
        capture.Clear();
        await dispatcher.DispatchAsync(Request(2, "industryMonitor", "DisconnectJob", first.Items[0]), session);
        capture.Clear();
        await dispatcher.DispatchAsync(Request(3, "industryMonitor", "ConnectJob", MonitorRequest(901)), session);
        var second = Assert.IsType<PyTuple>(ResponseResult(capture.Packets[^1]));
        capture.Clear();

        await dispatcher.DispatchAsync(Request(4, "invbroker", "Add", new PyInt(9)), session);

        var notice = Notification(Assert.Single(capture.Packets,
            packet => IsNotification(packet) && Notification(packet).Name == "OnIndustryMaterials"));
        Assert.Equal(second.Items[0], notice.Args[0]);
        Assert.NotEqual(first.Items[0], notice.Args[0]);
        Assert.Equal(new PyInt(9), Assert.IsType<PyDict>(notice.Args[1]).Entries.Single().Value);
    }
}
