using Eve.FactionWarfare;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Faction_facility_and_fighter_rpcs_keep_the_registered_host_wire_contracts()
    {
        var facility = new FacilityManagerService();
        var fighters = new FighterMgrService();
        var callbacks = new FacWarMgrCallbacks(
            ReadCharacterAsync: (_, _) => ValueTask.FromResult<FwCharacterWarfareState?>(null),
            SaveCharacterAsync: (_, _, _, _) => ValueTask.FromResult(false),
            ReadStandingAsync: (_, _, _) => ValueTask.FromResult(0d),
            CanActAsCorporationDirectorAsync: (_, _, _) => ValueTask.FromResult(false),
            CanActAsAllianceExecutorDirectorAsync: (_, _, _) => ValueTask.FromResult(false));
        // These facWarMgr calls refuse before touching FwStore or return its reference None response.
        var facWar = new FacWarMgrService(null!, callbacks);

        StartComposed(registry =>
        {
            registry.Register(facWar);
            registry.Register(facility);
            registry.Register(fighters);
        }, session =>
        {
            session.CharacterId = 90_000_001;
            session.Set("charid", new PyInt(90_000_001));
            session.Set("corpid", new PyInt(98_000_001));
            session.Set("allianceid", new PyInt(99_000_001));
            session.Set("corprole", new PyInt(1));
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        long callId = 0;
        PyDict TransportKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        async Task<Send> Exchange(string service, string method, PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, new PyTuple(args), TransportKwargs()))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }
        async Task<PyValue> Call(string service, string method, params PyValue[] args) =>
            Result(MarshalCodec.Decode((await Exchange(service, method, args)).Payload))!;
        async Task Refused(string service, string method, params PyValue[] args)
        {
            var response = await Exchange(service, method, args);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        }

        // These fallback collection shapes and errors are consumed directly by facilitySvc.
        Assert.Equal(new PyList([]), await Call("facilityManager", "GetFacilities"));
        Assert.Equal(new PyList([]), await Call("facilityManager", "GetFacilitiesByID",
            new PyList([new PyInt(60_000_001), new PyInt(60_000_002)])));
        Assert.Equal(new PyList([]), await Call("facilityManager", "GetFacilityLocations", new PyInt(60_000_001), new PyInt(90_000_001)));
        Assert.IsType<PyDict>(await Call("facilityManager", "GetMaxActivityModifiers"));
        Assert.IsType<PyObject>(await Call("facilityManager", "GetFacilityTaxes", new PyInt(60_000_001)));
        await Refused("facilityManager", "GetFacility", new PyInt(60_000_001));
        await Refused("facilityManager", "SetFacilityTaxes", new PyInt(60_000_001), new PyInt(98_000_001), new PyDict([]));

        // Valid corporation/alliance requests reach the registered service and fail at the live authorization check.
        foreach (var (method, args) in new (string Method, PyValue[] Args)[]
        {
            ("JoinFactionAsAlliance", [new PyInt(500_001)]),
            ("JoinFactionAsCorporation", [new PyInt(500_001)]),
            ("LeaveFactionAsAlliance", [new PyInt(500_001)]),
            ("LeaveFactionAsCorporation", [new PyInt(500_001)]),
            ("WithdrawJoinFactionAsAlliance", [new PyInt(500_001)]),
            ("WithdrawJoinFactionAsCorporation", [new PyInt(500_001)]),
            ("WithdrawLeaveFactionAsAlliance", [new PyInt(500_001)]),
            ("WithdrawLeaveFactionAsCorporation", [new PyInt(500_001)]),
        })
            await Refused("facWarMgr", method, args);

        Assert.Same(PyNone.Instance, await Call("facWarMgr", "RefreshCorps"));

        // Exercise the normal multi-fighter batch and point-target activation transport shapes.
        var fighterIds = new PyList([new PyInt(90_001), new PyInt(90_002), new PyInt(90_003)]);
        var activate = Assert.IsType<PyDict>(await Call("fighterMgr", "CmdActivateAbilitySlots",
            fighterIds, new PyInt(1), new PyTuple([new PyReal(1), new PyReal(2), new PyReal(3)])));
        Assert.Equal(3, activate.Entries.Count);
        Assert.Equal(fighterIds.Items, activate.Entries.Select(entry => entry.Key));
        var deactivate = Assert.IsType<PyDict>(await Call("fighterMgr", "CmdDeactivateAbilitySlots", fighterIds, new PyInt(1)));
        Assert.Equal(3, deactivate.Entries.Count);
        Assert.Equal(fighterIds.Items, deactivate.Entries.Select(entry => entry.Key));
        await Refused("fighterMgr", "CmdAbandonFighter", new PyInt(90_001));
        await Refused("fighterMgr", "CmdScoopAbandonedFighterFromSpace", new PyInt(90_001), new PyInt(5));
    }
}
