using System.Text;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Crimewatch;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Keeper_and_localization_RPCs_return_the_EveJS_shapes_through_host_registration()
    {
        Start(sessionOpened: session => session.Set("accountRole", new PyLong(GameMasterRoles.Staff)));
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 1, "keeper", "GetCurrentDungeonForCharacter"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 2, "keeper", "GetLevelEditor"));

        var textChanges = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 3, "localizationServer", "GetAllTextChanges", new PyDict([])));
        Assert.Equal(3, textChanges.Items.Count);
        Assert.All(textChanges.Items, value => Assert.Empty(Assert.IsType<PyDict>(value).Entries));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 4, "localizationServer", "UpdateLocalizationQAWrap", PyBool.True));
    }

    [Fact]
    public async Task Lookup_RPCs_keep_the_client_consumed_rows_through_host_registration()
    {
        var source = new LookupSearchSources(
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(20, "Alpha"), new(21, "Shared")]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(30, "Shared")]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            (_, _) => Task.FromResult<IReadOnlyList<LookupLocation>>([
                new(30000142, "Jita", 5, 5, 30000142, 20000020, 10000002),
            ]));
        Start(configure: registry => registry.Register(CoreServices.CreateLookupService(source)));
        var (front, open) = await OpenFrontAsync();

        var characters = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "lookupSvc", "LookupEvePlayerCharacters", new PyStr("alpha"), new PyInt(0)));
        var character = Assert.IsType<PyObject>(Assert.Single(characters.Items));
        var characterFields = Assert.IsType<PyDict>(character.Args);
        Assert.Contains(characterFields.Entries, entry => entry.Key == new PyStr("characterID") && entry.Value == new PyInt(20));

        var locations = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 2, "lookupSvc", "LookupKnownLocationsByGroup", new PyInt(5), new PyStr("jita")));
        var location = Assert.IsType<PyObject>(Assert.Single(locations.Items));
        var locationFields = Assert.IsType<PyDict>(location.Args);
        Assert.Contains(locationFields.Entries, entry => entry.Key == new PyStr("itemID") && entry.Value == new PyInt(30000142));
        Assert.Contains(locationFields.Entries, entry => entry.Key == new PyStr("itemName") && entry.Value == new PyStr("Jita"));

        var owners = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 3, "lookupSvc", "LookupNoneNPCAccountOwners", new PyStr("Shared"), new PyInt(0)));
        Assert.Equal([21, 30], owners.Items.Select(row =>
            Assert.IsType<PyInt>(Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries
                .Single(entry => entry.Key == new PyStr("ownerID")).Value).Value));
    }

    [Fact]
    public async Task Kill_right_actions_reject_missing_session_owner_through_host_without_touching_storage()
    {
        Start(configure: registry => registry.Register(new KillRightMgrService(null!)));
        var (front, open) = await OpenFrontAsync();

        foreach (var (callId, method, args) in new[]
        {
            (1L, "ActivateKillRight", new PyValue[] { new PyLong(7001) }),
            (2L, "BuyKillRight", new PyValue[] { new PyLong(7001), new PyReal(10) }),
        })
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "killRightMgr", method, new PyTuple(args),
                    new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var reply = MarshalCodec.Decode(send.Payload);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
            Assert.Contains("NoValidKillRight", Encoding.ASCII.GetString(send.Payload.Span));
        }
    }
}
