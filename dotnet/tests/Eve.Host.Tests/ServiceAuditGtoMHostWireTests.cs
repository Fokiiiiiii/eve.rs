using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task In_space_compression_returns_the_six_values_consumed_by_the_client_over_host_wire()
    {
        const long pilot = 90_001;
        const long itemId = 55;
        const long facilityId = 900;
        var item = new Item(itemId, 123, pilot, 400, 0, 5, true, null);
        long? compressedAt = null;
        var service = new InSpaceCompressionMgrRpcService(
            (id, _) => ValueTask.FromResult<Item?>(id == itemId ? item : null),
            (_, _, facility, _) =>
            {
                compressedAt = facility;
                return ValueTask.FromResult<SpaceCompressionResult?>(
                    new(itemId, 123, 5, 56, 456, 2));
            });
        Start(configure: registry => registry.Register(service), sessionOpened: session =>
        {
            session.Set("charid", new PyInt(pilot));
            session.Set("shipid", new PyInt(400));
            session.Set("solarsystemid2", new PyInt(30_000_142));
        });

        var (front, open) = await OpenFrontAsync();
        var result = Assert.IsType<PyTuple>(await HostRpcAsync(front, open, 1, "inSpaceCompressionMgr",
            "CompressItemInSpace", new PyLong(itemId), new PyLong(facilityId)));

        Assert.Equal([itemId, 123, 5, 56, 456, 2], result.Items.Select(WireInteger));
        Assert.Equal(facilityId, compressedAt);
    }

    [Fact]
    public async Task Jump_clone_cooldown_reset_mutates_the_store_and_notifies_before_its_reply()
    {
        const long pilot = 90_001;
        var store = new ResetOnlyJumpCloneStore { LastJumpFileTime = 123_456 };
        Start(configure: registry => registry.Register(new JumpCloneService(() => FixedNowMs, store)),
            sessionOpened: session => session.Set("charid", new PyInt(pilot)));

        var (front, open) = await OpenFrontAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "jumpCloneSvc", "ResetLastCloneJumpTime", new PyTuple([]), TransportKwargs()))));
        var notification = Assert.IsType<Send>(await front.ReceiveAsync());
        var message = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
        Assert.Equal(MachoMessage.Notification, message.Type);
        Assert.Equal("OnJumpCloneCacheInvalidated", message.Dest.BroadcastId);
        var response = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload))).Type);
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(response.Payload)));
        Assert.Equal(pilot, store.ResetCharacterId);
        Assert.Equal(0, store.LastJumpFileTime);
    }

    [Fact]
    public async Task Keeper_operation_lookups_return_the_client_solar_system_id_and_site_bool_over_host_wire()
    {
        const long pilot = 90_001;
        const long site = 70_001;
        var service = new KeeperService(new KeeperRpcCallbacks(
            ReadOperationSpawnpointSolarSystemAsync: (characterId, siteId, _) =>
                ValueTask.FromResult<long?>(characterId == pilot && siteId == site ? 30_000_142 : null),
            IsOperationSiteAsync: (_, characterId, siteId, _) =>
                ValueTask.FromResult(characterId == pilot && siteId == site)));
        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(pilot)));

        var (front, open) = await OpenFrontAsync();
        Assert.Equal(new PyInt(30_000_142), await HostRpcAsync(front, open, 1, "keeper",
            "GetOperationSpawnpointSolarSystem", new PyLong(pilot), new PyLong(site)));
        Assert.Same(PyBool.True, await HostRpcAsync(front, open, 2, "keeper", "IsOperationSite", new PyLong(site)));
    }

    [Fact]
    public async Task Kiring_and_localization_replies_keep_the_client_wire_shapes_and_fail_closed_without_backends()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        static PyValue Field(PyDict dict, string name) =>
            dict.Entries.Single(entry => WireText(entry.Key) == name).Value;

        var config = Assert.IsType<PyDict>(await HostRpcAsync(front, open, 1, "kiringMgr", "GetKiringConfiguration"));
        Assert.Equal(new PyInt(1), Field(config, "mode"));
        Assert.False(string.IsNullOrWhiteSpace(WireText(Field(config, "client_id"))));
        var endpoints = Assert.IsType<PyDict>(Field(config, "endpoints"));
        foreach (var name in new[] { "mpay", "billing", "redirect_uri" })
            Assert.False(string.IsNullOrWhiteSpace(WireText(Field(endpoints, name))));
        var channels = Assert.IsType<PyDict>(Field(config, "channels"));
        foreach (var name in new[] { "login", "pay", "app" })
            Assert.False(string.IsNullOrWhiteSpace(WireText(Field(channels, name))));

        Assert.Equal(new PyInt(201), await HostRpcAsync(front, open, 2, "kiringMgr", "GetAntiAddictionCode",
            new PyStr("netease-account")));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 3, "kiringMgr", "GetEveGuardServerAddress"));
        Assert.Same(PyBool.False, await HostRpcAsync(front, open, 4, "kiringMgr", "ShouldKickCheater"));
        Assert.Same(PyBool.False, await HostRpcAsync(front, open, 5, "kiringMgr", "ActivateRedeemingCode",
            new PyStr("code"), new PyStr("account"), new PyInt(42), new PyStr("user"), new PyStr("pilot"),
            new PyStr("127.0.0.1"), new PyStr("device")));
        Assert.Same(PyBool.False, await HostRpcAsync(front, open, 6, "localizationServer", "ReloadFSDPickle"));

        long refusedCallId = 7;
        async Task AssertRefused(string method, PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, refusedCallId++, "kiringMgr", method, new PyTuple(args), TransportKwargs()))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.ErrorResponse,
                Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type);
        }

        await AssertRefused("PerformKiringServerSideAuthenticationFromCode",
            [new PyStr("code"), new PyStr("device"), new PyStr("client")]);
        await AssertRefused("GetSAuthAid", [new PyStr("user"), new PyStr("account"), new PyStr("device")]);
        await AssertRefused("PlaceKiringOrder",
        [
            new PyStr("device"), new PyStr("client"), new PyInt(42), new PyInt(100), new PyInt(1),
            new PyStr("user"), new PyStr("netease-account"), new PyStr("billing"), new PyStr("app"),
            new PyStr("pay"), new PyStr("login"), new PyDict([]), new PyInt(1),
        ]);
    }

    [Fact]
    public async Task Lookup_legacy_methods_return_the_fields_consumed_by_the_client_over_host_wire()
    {
        var sources = new LookupSearchSources(
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(100, "Shared", CorporationId: 200)]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(200, "Shared", FactionId: 400)]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(400, "Shared")]),
            (_, _) => Task.FromResult<IReadOnlyList<LookupLocation>>([new(30_000_142, "Jita", 5, 5)]));

        Start(configure: registry => registry.Register(CoreServices.CreateLookupService(sources)));
        var (front, open) = await OpenFrontAsync();

        var characters = Assert.IsType<PyList>(await HostRpcAsync(front, open, 1, "lookupSvc", "LookupCharacters",
            new PyStr("Shared"), new PyInt(1)));
        Assert.Equal(100, WireInteger(WireField(Assert.Single(characters.Items), "characterID")));

        var corporations = Assert.IsType<PyList>(await HostRpcAsync(front, open, 2, "lookupSvc", "LookupCorporations",
            new PyStr("Shared")));
        Assert.Equal(200, WireInteger(WireField(Assert.Single(corporations.Items), "corporationID")));

        var factions = Assert.IsType<PyList>(await HostRpcAsync(front, open, 3, "lookupSvc", "LookupFactions",
            new PyStr("Shared")));
        Assert.Equal(400, WireInteger(WireField(Assert.Single(factions.Items), "factionID")));

        var locations = Assert.IsType<PyList>(await HostRpcAsync(front, open, 4, "lookupSvc", "LookupLocationsByGroup",
            new PyInt(5), new PyStr("Jita")));
        Assert.Equal(30_000_142, WireInteger(WireField(Assert.Single(locations.Items), "itemID")));
    }

    [Fact]
    public async Task MachoNet_service_info_returns_a_service_to_optional_location_map_over_host_wire()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        var serviceInfo = Assert.IsType<PyDict>(await HostRpcAsync(front, open, 1, "machoNet", "GetServiceInfo"));
        var entries = serviceInfo.Entries.ToDictionary(entry => WireText(entry.Key), entry => entry.Value);
        Assert.Contains("machoNet", entries.Keys);
        Assert.Contains("kiringMgr", entries.Keys);
        Assert.True(entries["machoNet"] is PyNone or PyBytes or PyStr);
        Assert.True(entries["kiringMgr"] is PyNone or PyBytes or PyStr);
    }

    private sealed class ResetOnlyJumpCloneStore : IJumpCloneStore
    {
        public long LastJumpFileTime { get; set; }
        public long ResetCharacterId { get; private set; }

        public ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JumpCloneSnapshot([], LastJumpFileTime));
        public ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken)
        {
            ResetCharacterId = characterId;
            LastJumpFileTime = 0;
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<JumpCloneRecord?> InstallAsync(long characterId, JumpCloneInstall install, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<CloneJumpResult> JumpAsync(long characterId, long targetCloneId, long originLocationId, string originLocationKind,
            decimal fee, long ownerCorporationId, long nowFileTime, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
