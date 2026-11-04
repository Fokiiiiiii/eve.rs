using System.IO.Compression;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Alert_delivery_and_log_mode_rpcs_cross_host_wire_with_client_shapes()
    {
        Start(strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        var beans = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyLong(90_000_001),
                new PyTuple([new PyInt(3), new PyLong(1_790_646_377_000)])),
        ]);
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 1, "alert", "BeanDelivery", beans));

        var groupBeans = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyLong(90_000_001),
                new PyDict([
                    new KeyValuePair<PyValue, PyValue>(
                        new PyLong(80_000_001),
                        new PyTuple([
                            new PyInt(2),
                            new PyLong(1_790_646_377_000),
                            new PyLong(70_000_001),
                            new PyLong(30_000_142),
                            new PyLong(30_000_143),
                            new PyStr("{}"),
                            PyNone.Instance,
                        ])),
                ])),
        ]);
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(MarshalCodec.Encode(groupBeans));
            compressed = output.ToArray();
        }
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "alert", "GroupBeanDelivery", new PyBytes(compressed)));

        var modes = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("error-hash"),
                new PyTuple([new PyLong(90_000_001), new PyStr("D")])),
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("batch-hash"),
                new PyTuple([new PyLong(90_000_002), new PyStr("B")])),
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("pending-hash"),
                new PyTuple([PyNone.Instance, PyNone.Instance])),
        ]);
        var reply = await HostRpcAsync(front, open, 3, "alert", "GetLogModeForError", modes);
        var response = Assert.IsType<PyTuple>(reply);
        Assert.Equal(2, response.Items.Count);
        Assert.Empty(Assert.IsType<PyDict>(response.Items[0]).Entries);
        Assert.Empty(Assert.IsType<PyList>(response.Items[1]).Items);
    }

    [Fact]
    public async Task Alert_group_bean_delivery_rejects_non_payloads()
    {
        Start(strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        _ = await HostErrorRpcAsync(front, open, 1, "alert", "GroupBeanDelivery", new PyStr("not a bean payload"));
        var malformedGroup = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyLong(90_000_001),
                new PyDict([
                    new KeyValuePair<PyValue, PyValue>(new PyLong(80_000_001), new PyStr("not a count row")),
                ])),
        ]);
        _ = await HostErrorRpcAsync(front, open, 2, "alert", "GroupBeanDelivery", malformedGroup);
    }
}
