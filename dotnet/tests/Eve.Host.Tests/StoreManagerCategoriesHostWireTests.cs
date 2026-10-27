using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Store_manager_categories_preserves_client_category_fields_through_host_wire()
    {
        static PyValue Field(PyDict value, string name) =>
            value.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

        static void AssertCategory(PyDict category, long id, string name, string href)
        {
            Assert.Equal(5, category.Entries.Count);
            Assert.Equal(id, Assert.IsType<PyInt>(Field(category, "id")).Value);
            Assert.Equal(name, JsSemantics.StringValue(Field(category, "name")));
            Assert.Equal(href, JsSemantics.StringValue(Field(category, "href")));
        }

        var storeState = JsonNode.Parse(
            """{"stores":{"4":{"categories":[{"id":9001,"name":"Ships","href":"/categories/ships","parent":null,"tags":[]},{"id":9002,"name":"Frigates","href":"/categories/frigates","parent":{"id":9001},"tags":["navy"]}]}}}""")!
            .AsObject();
        var service = new StoreOffersService(_ => ValueTask.FromResult<JsonObject?>(storeState));
        Start(configure: registry => registry.Register(service), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var request = Packets.CallWithKwargs(
            open, 1, "storeManager", "get_categories", new PyTuple([new PyInt(4)]),
            Py.Dict(("machoVersion", new PyInt(1))));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
        var categories = Assert.IsType<PyList>(
            Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));

        Assert.Equal(2, categories.Items.Count);
        var root = Assert.IsType<PyDict>(categories.Items[0]);
        var child = Assert.IsType<PyDict>(categories.Items[1]);
        AssertCategory(root, 9001, "Ships", "/categories/ships");
        Assert.IsType<PyNone>(Field(root, "parent"));
        Assert.Empty(Assert.IsType<PyList>(Field(root, "tags")).Items);
        AssertCategory(child, 9002, "Frigates", "/categories/frigates");
        var parent = Assert.IsType<PyDict>(Field(child, "parent"));
        Assert.Equal(9001, Assert.IsType<PyInt>(Field(parent, "id")).Value);
        Assert.Equal("navy", JsSemantics.StringValue(Assert.Single(Assert.IsType<PyList>(Field(child, "tags")).Items)));
    }
}
