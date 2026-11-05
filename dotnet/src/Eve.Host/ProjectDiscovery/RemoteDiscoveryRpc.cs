using System.Text.Json;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.ProjectDiscovery;

// One configured project profile per Host. Unknown delivery is never retried.
internal sealed class RemoteDiscoveryRpc(RemoteDiscoveryProvider provider, string protocol = "covid")
{
    public async ValueTask<PyValue> GetTaskAsync(ServiceCall call)
    {
        try { return ScientificReplayRpc.ToPy(await provider.GetTaskAsync(call.Session, call.Cancellation)); }
        catch (DiscoveryAssignmentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
        catch (MmosHttpException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
        catch (MmosResponseException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
        catch (HttpRequestException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
    }
    public async ValueTask<PyValue> SubmitAsync(ServiceCall call)
    {
        try
        {
            long id = call.CallId switch { PyInt v => v.Value, PyLong v => v.Value, _ => 0 };
            if (id <= 0 || call.Session.SessionId <= 0) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
            JsonElement result, timing; JsonElement? task = null;
            if (protocol == "covid")
            {
                if (call.Args.Count != 2 || call.Kwargs is not (PyNone or PyDict { Entries.Count: 0 })) throw new ArgumentException();
                result = JsonSerializer.SerializeToElement(ScientificReplayRpc.ToObject(call.Arg(0), 0));
                timing = JsonSerializer.SerializeToElement(ScientificReplayRpc.ToObject(call.Arg(1), 0));
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("skip", out var skip) || skip.ValueKind != JsonValueKind.False ||
                    !result.TryGetProperty("polygons", out var polygons) || polygons.ValueKind != JsonValueKind.Array ||
                    timing.ValueKind != JsonValueKind.Object || !timing.TryGetProperty("t", out var duration) || duration.ValueKind != JsonValueKind.Number) throw new ArgumentException();
            }
            else
            {
                if (call.Args.Count is < 3 or > 4 || call.Arg(0) is not PyDict) throw new ArgumentException();
                task = JsonSerializer.SerializeToElement(ScientificReplayRpc.ToObject(call.Arg(0), 0));
                result = JsonSerializer.SerializeToElement(ScientificReplayRpc.ToObject(call.Arg(1), 0));
                var seconds = JsonSerializer.SerializeToElement(ScientificReplayRpc.ToObject(call.Arg(2), 0));
                if (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetDouble(out var n) || !double.IsFinite(n) || n < 0) throw new ArgumentException();
                PyValue remark = call.Args.Count == 4 ? call.Arg(3) : call.Kwargs is PyDict { Entries.Count: 1 } kwargs && JsSemantics.StringValue(kwargs.Entries[0].Key) == "remark" ? kwargs.Entries[0].Value : PyBool.False;
                if (remark is not PyBool flag || (call.Kwargs is not (PyNone or PyDict { Entries.Count: 0 }) && !(call.Args.Count == 3 && call.Kwargs is PyDict { Entries.Count: 1 } one && JsSemantics.StringValue(one.Entries[0].Key) == "remark"))) throw new ArgumentException();
                if (protocol == "exoplanet")
                {
                    if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("transits", out var transits) || transits.ValueKind != JsonValueKind.Array || !result.TryGetProperty("stellarActivity", out var activity) || activity.ValueKind != JsonValueKind.Array) throw new ArgumentException();
                }
                else
                {
                    if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() is < 1 or > 5) throw new ArgumentException();
                    var categories = new HashSet<int>();
                    foreach (var category in result.EnumerateArray()) if (category.ValueKind != JsonValueKind.Number || !category.TryGetInt32(out var value) || value is < 9 or > 35 || !categories.Add(value)) throw new ArgumentException();
                }
                timing = JsonSerializer.SerializeToElement(new { duration = n, remark = flag.Value });
            }
            var response = await provider.SubmitAsync(call.Session, $"{call.Session.SessionId}:{id}", result, timing, call.Cancellation, task);
            return ToClient(response, protocol);
        }
        catch (DiscoverySubmissionUnknownException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_SUBMISSION_UNKNOWN"); }
        catch (DiscoveryAssignmentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
        catch (ArgumentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID"); }
        catch (JsonException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID"); }
    }
    private static PyValue ToClient(JsonElement response, string protocol)
    {
        if (protocol == "covid") return ScientificReplayRpc.ToPy(response);
        return new PyDict(response.EnumerateObject().Select(pair => new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Name), pair.Name == "playerState"
            ? Py.KeyVal(("rank", new PyLong(pair.Value.GetProperty("rank").GetInt64())), ("experience", new PyLong(pair.Value.GetProperty("experience").GetInt64())), ("analysisKredits", new PyInt(0)), ("finishedTutorial", pair.Value.GetProperty("finishedTutorial").GetBoolean() ? PyBool.True : PyBool.False), ("tutorialLevel", new PyLong(pair.Value.GetProperty("tutorialLevel").GetInt64())))
            : ScientificReplayRpc.ToPy(pair.Value))).ToArray());
    }
    public async ValueTask<ProjectDiscoveryPlayerStatistics?> ReadStatisticsAsync(long actor, CancellationToken ct)
    {
        try { return await provider.ReadStatisticsAsync(actor, ct); }
        catch (MmosHttpException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_STATISTICS_UNAVAILABLE"); }
        catch (MmosResponseException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_STATISTICS_UNAVAILABLE"); }
        catch (HttpRequestException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_STATISTICS_UNAVAILABLE"); }
    }
    public GameRpcCallbacks Attach(GameRpcCallbacks callbacks) => callbacks with
    { GetProjectDiscoveryTask = GetTaskAsync, SubmitProjectDiscoveryClassification = SubmitAsync, ReadProjectDiscoveryStatistics = ReadStatisticsAsync };
}
