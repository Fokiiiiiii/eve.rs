using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=not-used;Username=not-used;Timeout=1");
        var charMgr = new CharMgrService(
            new PgCharacterStore(source, "not_used"),
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(0d),
            (_, _) => ValueTask.FromResult<long?>(null),
            readPersonalData: (_, _) => ValueTask.FromResult<CharMgrPersonalData?>(CharMgrPersonalData.Empty));

        Start(configure: registry => registry.Register(charMgr),
            sessionOpened: session => session.CharacterId = 1_001);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var result = Assert.IsType<PyObject>(await CallOverHost(front, open, 1, "charMgr", "GetContactList", Py.Tuple([])));
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(result.Name));
        var addresses = PersonalWireField(result, "addresses");
        var blocked = PersonalWireField(result, "blocked");
        Assert.Empty(Assert.IsType<PyList>(PersonalWireField(addresses, "lines")).Items);
        Assert.Empty(Assert.IsType<PyList>(PersonalWireField(blocked, "lines")).Items);
    }

    private static PyValue PersonalWireField(PyValue value, string name)
    {
        var fields = value switch
        {
            PyObject obj => Assert.IsType<PyDict>(obj.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a mapping, got {value.GetType().Name}"),
        };
        return fields.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    }
}
