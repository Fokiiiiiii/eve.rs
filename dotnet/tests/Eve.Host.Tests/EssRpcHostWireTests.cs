using Eve.Host;
using Eve.Marshal;
using Eve.Services.Core;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ess_epic_arc_and_wreck_rpcs_are_dispatched_with_client_shapes()
    {
        const long systemId = 30_000_142;
        const long characterId = 90_000_001;
        var state = new EssMgrSystemData(
            EssId: 900_000_000,
            SolarSystemId: systemId,
            CurrentOutput: 0.42,
            MainValue: 1234.5,
            ReserveValue: 678.9,
            MainBankLink: new EssMgrMainBankLink("link", characterId, 1001, 2002),
            ReserveBankPulsesRemaining: 2,
            ReserveBankPulsesTotal: 9,
            ReserveLinkedCharacterIds: new HashSet<long> { characterId },
            MainBankThefts: [new PyStr("main-theft")],
            ReserveBankThefts: [new PyStr("reserve-theft")]);
        var operations = new List<EssMgrOperationRequest>();
        var launchedItems = new List<(long Source, long Item)>();
        Eve.Services.ClientSession? openedSession = null;
        var essMgr = new EssMgrService(new EssMgrCallbacks(
            ReadSystemStateAsync: (requestedSystem, _) =>
                ValueTask.FromResult(requestedSystem == systemId ? state : null),
            ExecuteAsync: (request, _) =>
            {
                operations.Add(request);
                return ValueTask.FromResult(new EssMgrOperationResult(false));
            }));
        var cacheMgr = new DynamicResourceCacheMgrService(
            readSystem: (requestedSystem, _) =>
                ValueTask.FromResult(requestedSystem == systemId ? state : null),
            listSystems: _ => ValueTask.FromResult<IReadOnlyList<EssMgrSystemData>>([state]),
            quadrantOf: _ => "Genesis");
        var eject = new EjectRpcService((_, sourceId, itemId, _) =>
        {
            launchedItems.Add((sourceId, itemId));
            return ValueTask.CompletedTask;
        });

        Start(
            configure: registry =>
            {
                registry.Register(essMgr);
                registry.Register(cacheMgr);
                registry.Register(eject);
            },
            sessionOpened: session =>
            {
                openedSession = session;
                session.Set("charid", new PyInt(characterId));
                session.Set("solarsystemid2", new PyInt(systemId));
                session.Set("solarsystemid", new PyInt(systemId));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        openedSession!.CharacterId = characterId;

        long callId = 0;
        async Task<PyValue?> Call(string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, ++callId, service, method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var details = Assert.IsType<PyDict>(await Call(
            "dynamicResourceCacheMgr", "GetESSSystemDetails", new PyInt(systemId)));
        Assert.Equal(new PyInt(2), Field(details, "activeLinks"));
        Assert.Equal(new PyInt(9), Field(details, "totalPulses"));

        var agencyData = Assert.IsType<PyDict>(await Call("dynamicResourceCacheMgr", "GetESSAgencyData"));
        var systemData = Assert.IsType<PyDict>(agencyData.Entries.Single().Value);
        Assert.Equal(new PyReal(0.42), Field(systemData, "currentOutput"));
        Assert.Equal(new PyReal(1234.5), Field(systemData, "mainValue"));
        Assert.Equal(new PyReal(678.9), Field(systemData, "reserveValue"));
        Assert.Same(PyBool.True, Field(systemData, "vaultOpen"));

        var clientState = Assert.IsType<PyDict>(await Call("essMgr", "GetDataForClientSolarSystem"));
        Assert.Equal(new PyInt(systemId), Field(clientState, "solarSystemID"));
        Assert.Same(PyBool.True, await Call("essMgr", "IsClientLinkedToReserveBank"));
        var mainThefts = Assert.IsType<PyList>(await Call("essMgr", "GetMainBankTheftsForClientSolarSystem"));
        var reserveThefts = Assert.IsType<PyList>(await Call("essMgr", "GetReserveBankTheftsForClientSolarSystem"));
        Assert.Equal("main-theft", T(mainThefts.Items.Single()));
        Assert.Equal("reserve-theft", T(reserveThefts.Items.Single()));

        foreach (var method in new[]
        {
            "AttemptLinkToMainBank",
            "AttemptLinkToReserveBank",
            "RequestMainBankUnlink",
            "RequestReserveBankUnlink",
        })
        {
            Assert.Same(PyNone.Instance, await Call("essMgr", method));
        }
        Assert.Same(PyNone.Instance,
            await Call("essMgr", "RequestUnlockReserveBank", new PyInt(55914)));
        Assert.Equal(
            new[]
            {
                EssMgrOperation.AttemptLinkToMainBank,
                EssMgrOperation.AttemptLinkToReserveBank,
                EssMgrOperation.RequestMainBankUnlink,
                EssMgrOperation.RequestReserveBankUnlink,
                EssMgrOperation.RequestUnlockReserveBank,
            },
            operations.Select(request => request.Operation));
        Assert.All(operations, request => Assert.Same(openedSession, request.Call.Session));
        Assert.Equal(55914, operations[^1].KeyTypeId);

        Assert.Equal(characterId, openedSession!.CharacterId);
        Assert.Same(PyBool.False,
            await Call("epicArcStatus", "AgentHasEpicMissionsForCharacter", new PyInt(700_001)));
        Assert.Same(PyBool.False,
            await Call("epicArcStatus", "AgentHasEpicMissionsForCharacter", new PyInt(700_002)));

        Assert.Same(PyNone.Instance, await Call(
            "eject", "LaunchShipOrContainerFromWreckOrContainer", new PyLong(900), new PyLong(901)));
        Assert.Same(PyNone.Instance, await Call(
            "eject", "LaunchShipOrContainerFromWreckOrContainer", new PyLong(902), new PyLong(903)));
        Assert.Equal(new[] { (900L, 901L), (902L, 903L) }, launchedItems);
    }

    private static PyValue Field(PyDict dict, string key) =>
        dict.Entries.Single(entry => T(entry.Key) == key).Value;
}
