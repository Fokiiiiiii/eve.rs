using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class StructureDataRpcServicesTests
{
    [Theory]
    [InlineData("GetExtractionAndEventIDFromStructureID")]
    [InlineData("GetExtractionsForCorp")]
    [InlineData("GetMoonResourcesForStructure")]
    [InlineData("StartNewExtraction")]
    [InlineData("FractureChunkWithStructure")]
    [InlineData("CancelExtraction")]
    public void Moon_extractions_advertises_each_reference_rpc(string method) =>
        Assert.True(new MoonExtractionsRpcService().HasMethod(method));

    [Fact]
    public async Task Moon_extraction_reads_use_game_store_data_and_reference_wire_shapes()
    {
        const long structureId = 30_000_142;
        const long corporationId = 980001;
        const long startMs = 1_700_000_000_000;
        const long earlyChunkMs = startMs + 1_000;
        const long lateChunkMs = startMs + 2_000;
        var state = new JsonObject
        {
            ["resourcesByStructureID"] = new JsonObject
            {
                [structureId.ToString()] = new JsonObject
                {
                    ["resources"] = new JsonObject { ["34"] = 0.75, ["20"] = 0.25, ["0"] = 9.0 },
                },
            },
            ["extractions"] = new JsonObject
            {
                ["100"] = Extraction(100, structureId, corporationId, startMs, lateChunkMs, "active", 7654),
                ["101"] = Extraction(101, structureId + 1, corporationId, startMs, earlyChunkMs, "fracturing", 0),
                ["102"] = Extraction(102, structureId + 2, corporationId + 1, startMs, earlyChunkMs, "active", 0),
                ["103"] = Extraction(103, structureId + 3, corporationId, startMs, earlyChunkMs, "ready", 0),
            },
        };
        var session = new ClientSession();
        session.Set("structureid", new PyLong(structureId));
        session.Set("corpid", new PyInt(corporationId));
        var service = new MoonExtractionsRpcService(() => Task.FromResult<JsonObject?>(state));

        var pair = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetExtractionAndEventIDFromStructureID", session)));
        Assert.Equal(2, pair.Items.Count);
        var extraction = Assert.IsType<PyObject>(pair.Items[0]);
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(extraction.Name).Value);
        Assert.Equal(new PyInt(100), Field(extraction, "extractionID"));
        Assert.Equal(new PyInt(structureId), Field(extraction, "structureID"));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(startMs)), Field(extraction, "startMoveTime"));
        Assert.Equal(new PyInt(7654), pair.Items[1]);

        var rows = Assert.IsType<PyList>(await service.CallAsync(Call("GetExtractionsForCorp", session)));
        Assert.Equal(2, rows.Items.Count);
        Assert.Equal(new PyInt(101), Field(Assert.IsType<PyObject>(rows.Items[0]), "extractionID"));
        Assert.Equal(new PyInt(100), Field(Assert.IsType<PyObject>(rows.Items[1]), "extractionID"));

        var resources = Assert.IsType<PyDict>(await service.CallAsync(Call("GetMoonResourcesForStructure", session)));
        Assert.Equal(new PyInt(20), resources.Entries[0].Key);
        Assert.Equal(new PyReal(0.25), resources.Entries[0].Value);
        Assert.Equal(new PyInt(34), resources.Entries[1].Key);
        Assert.Equal(new PyReal(0.75), resources.Entries[1].Value);
    }

    [Theory]
    [InlineData("StartNewExtraction", true)]
    [InlineData("FractureChunkWithStructure", false)]
    [InlineData("CancelExtraction", false)]
    public async Task Moon_extraction_mutations_have_a_future_backend_callback(string method, bool returnsBackendValue)
    {
        ServiceCall? forwarded = null;
        var expected = new PyInt(7);
        var service = new MoonExtractionsRpcService(
            mutationBackend: call =>
            {
                forwarded = call;
                return ValueTask.FromResult<PyValue>(expected);
            });
        var call = Call(method, new ClientSession(), new PyInt(30_000_142), new PyInt(518_460), PyBool.True);

        Assert.Same(returnsBackendValue ? expected : PyNone.Instance, await service.CallAsync(call));
        Assert.Same(call, forwarded);
    }

    [Theory]
    [InlineData("StartNewExtraction")]
    [InlineData("FractureChunkWithStructure")]
    [InlineData("CancelExtraction")]
    public async Task Unwired_moon_mutations_refuse_with_a_named_custom_notify(string method)
    {
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            new MoonExtractionsRpcService().CallAsync(Call(method)).AsTask());
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    [Fact]
    public async Task Overview_preset_rpcs_hash_deduplicate_store_and_fetch_shared_profiles()
    {
        var registration = new OverviewPresetMgrService();
        Assert.True(registration.HasMethod("StoreLinkAndGetID"));
        Assert.True(registration.HasMethod("GetStoredPreset"));
        JsonObject state = new();
        var service = new OverviewPresetMgrService(
            () => (JsonObject)state.DeepClone(),
            next =>
            {
                state = (JsonObject)next.DeepClone();
                return true;
            });
        var session = new ClientSession { CharacterId = 880001 };
        var payload = Py.Dict(
            ("z", new PyInt(2)),
            ("settings", Py.Dict(("9", new PyReal(1e21)), ("2", new PyReal(1e-6)))),
            ("a", new PyStr("Ω")));

        var stored = Assert.IsType<PyObject>(await service.CallAsync(Call("StoreLinkAndGetID", session, payload)));
        var hash = Assert.IsType<PyStr>(Field(stored, "hashvalue")).Value;
        const string canonicalPayload = "[[\"a\",\"Ω\"],[\"settings\",{\"2\":0.000001,\"9\":1e+21}],[\"z\",2]]";
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(canonicalPayload))), hash);
        Assert.Equal(new PyInt(1), Field(stored, "sqID"));
        Assert.Equal(880001L, state["entries"]!.AsObject().First().Value!["ownerID"]!.GetValue<long>());

        var duplicate = Assert.IsType<PyObject>(await service.CallAsync(Call("StoreLinkAndGetID", session,
            Py.Dict(("a", new PyStr("Ω")),
                ("settings", Py.Dict(("2", new PyReal(1e-6)), ("9", new PyReal(1e21)))),
                ("z", new PyInt(2))))));
        Assert.Equal(Field(stored, "hashvalue"), Field(duplicate, "hashvalue"));
        Assert.Equal(Field(stored, "sqID"), Field(duplicate, "sqID"));
        Assert.Single(state["entries"]!.AsObject());

        var fetched = await service.CallAsync(Call("GetStoredPreset", null,
            new PyTuple([new PyStr(hash), new PyInt(1)])));
        Assert.Equal(new PyStr(canonicalPayload), fetched);
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("GetStoredPreset", null,
            new PyTuple([new PyStr(hash), new PyInt(2)]))));
    }

    private static JsonObject Extraction(long id, long structureId, long ownerId, long startMs, long chunkMs, string state, long eventId) => new()
    {
        ["extractionID"] = id,
        ["structureID"] = structureId,
        ["moonID"] = 400001,
        ["solarSystemID"] = 30_000_142,
        ["ownerID"] = ownerId,
        ["startMoveTimeMs"] = startMs,
        ["chunkAvailableTimeMs"] = chunkMs,
        ["naturalDecayTimeMs"] = chunkMs + 10_800_000,
        ["durationSeconds"] = 518_460,
        ["yieldMultiplier"] = 1.25,
        ["calendarEventID"] = eventId,
        ["state"] = state,
    };

    private static PyValue Field(PyObject value, string name) => Assert.IsType<PyDict>(value.Args).Entries
        .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    private static ServiceCall Call(string method, ClientSession? session = null, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session ?? new ClientSession(),
    };
}
