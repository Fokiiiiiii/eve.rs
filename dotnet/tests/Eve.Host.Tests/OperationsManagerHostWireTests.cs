using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Operations_manager_progress_and_client_action_replies_keep_v24_host_wire_shapes()
    {
        const long characterId = 90_000_001;
        var character = JsonNode.Parse("""
            {
              "operationProgress": {
                "32": [[158, 0, [[347, 10, 7]]]]
              }
            }
            """)!.AsObject();
        var service = new OperationsManagerService(
            readCharacterProgress: (_, _) => ValueTask.FromResult<PyDict?>(
                LoginInitializationGameStoreProjection.ReadOperationsProgress(character)));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyLong(characterId)));
        var (front, open) = await OpenFrontAsync();

        var progress = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 1, "operationsManager", "get_character_progress"));
        var category = Assert.IsType<PyList>(
            progress.Entries.Single(pair => pair.Key.Equals(new PyInt(32))).Value);
        var operation = Assert.IsType<PyList>(Assert.Single(category.Items));
        Assert.Equal(new PyInt(158), operation.Items[0]);
        var tasks = Assert.IsType<PyList>(operation.Items[2]);
        var task = Assert.IsType<PyList>(Assert.Single(tasks.Items));
        Assert.Equal(new PyInt(347), task.Items[0]);
        Assert.Equal(new PyInt(7), task.Items[2]);

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "operationsManager", "get_active_category_id"));
        Assert.Empty(Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 3, "operationsManager", "get_operations_completed_at_least_once",
                new PyInt(32))).Items);
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 4, "operationsManager", "activate_operation_in_solar_system",
                new PyInt(32), new PyInt(158)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 5, "operationsManager", "block_task",
                new PyInt(32), new PyInt(158), new PyInt(347)));
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 6, "operationsManager", "skip_task",
                new PyInt(32), new PyInt(158), new PyInt(347)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 7, "operationsManager", "start_site",
                new PyInt(32), new PyInt(158)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 8, "operationsManager", "unblock_task",
                new PyInt(32), new PyInt(158), new PyInt(347)));

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("extra_data"), new PyDict([])),
            new KeyValuePair<PyValue, PyValue>(new PyStr("amount"), new PyInt(3)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 9, "operationsManager", "process_client_event",
                new PyTuple([new PyInt(42)]), kwargs))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        Assert.Same(PyNone.Instance, Result(decoded));
    }
}
