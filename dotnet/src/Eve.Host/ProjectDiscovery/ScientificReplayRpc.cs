using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.ProjectDiscovery;

// Thin adapter for the pinned COVID protocol; it does not invent live MMOS results.
public sealed class ScientificReplayRpc(ScientificReplayProvider provider, ProjectDiscoveryTaskPinStore pins)
{
    public ValueTask<Eve.Services.Core.ProjectDiscoveryPlayerStatistics?> ReadStatisticsAsync(long characterId, CancellationToken ct) => provider.ReadStatisticsAsync(characterId, ct);
    public async ValueTask<PyValue> GetTaskAsync(ServiceCall call)
    {
        try
        {
            await RequireNoUnknownPinAsync(call);
            return ToPy(await provider.GetTaskAsync(call.Session, call.Cancellation));
        }
        catch (ScientificReplayExhaustedException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_EXHAUSTED"); }
        catch (DiscoveryAssignmentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
    }
    public async ValueTask<PyValue> SubmitAsync(ServiceCall call)
    {
        try
        {
            await RequireNoUnknownPinAsync(call);
            if (call.Args.Count != 2 || call.Kwargs is not (PyNone or PyDict { Entries.Count: 0 })) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
            long id = call.CallId switch { PyInt value => value.Value, PyLong value => value.Value, _ => 0 };
            if (id <= 0 || call.Session.SessionId <= 0) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
            JsonElement result = JsonSerializer.SerializeToElement(ToObject(call.Arg(0), 0));
            JsonElement timing = JsonSerializer.SerializeToElement(ToObject(call.Arg(1), 0));
            // The current COVID result UI requires player.score; its UI does not emit skip=true.
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("skip", out var skip) || skip.ValueKind != JsonValueKind.False)
                throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
            return ToPy(await provider.SubmitAsync(call.Session, $"{call.Session.SessionId}:{id}", result, timing, call.Cancellation));
        }
        catch (ScientificReplayExhaustedException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_EXHAUSTED"); }
        catch (DiscoveryAssignmentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
        catch (ArgumentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID"); }
        catch (JsonException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID"); }
    }
    private async Task RequireNoUnknownPinAsync(ServiceCall call)
    {
        long actor = call.Session.CharacterId.GetValueOrDefault();
        if (actor <= 0 || call.Session.UserId.GetValueOrDefault() <= 0) throw new DiscoveryAssignmentException("Character and account required.");
        // A numeric live-task pin is not a scientific dataset identifier.
        if (await pins.ReadAsync(actor, call.Cancellation) is not null) throw new DiscoveryAssignmentException("Pinned task is not a scientific assignment.");
    }
    internal static object? ToObject(PyValue value, int depth)
    {
        if (depth > 16) throw new ArgumentException("Input nesting is too deep.");
        return value switch
        {
            PyNone => null,
            PyBool v => v.Value,
            PyInt v => v.Value,
            PyLong v => v.Value,
            PyReal v when double.IsFinite(v.Value) => v.Value,
            PyStr v => v.Value,
            PyWString v => v.Value,
            PyBytes v => new UTF8Encoding(false, true).GetString(v.Value.Span),
            PyList v when v.Items.Count <= 8192 => v.Items.Select(x => ToObject(x, depth + 1)).ToArray(),
            PyTuple v when v.Items.Count <= 8192 => v.Items.Select(x => ToObject(x, depth + 1)).ToArray(),
            PyDict v when v.Entries.Count <= 1024 => v.Entries.ToDictionary(x => Key(x.Key), x => ToObject(x.Value, depth + 1), StringComparer.Ordinal),
            _ => throw new ArgumentException("Invalid scientific input."),
        };
    }
    private static string Key(PyValue value) => value switch
    { PyStr v => v.Value, PyWString v => v.Value, PyBytes v => new UTF8Encoding(false, true).GetString(v.Value.Span), _ => throw new ArgumentException("Invalid key.") };
    internal static PyValue ToPy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new PyDict(value.EnumerateObject().Select(p => new KeyValuePair<PyValue,PyValue>(new PyStr(p.Name), ToPy(p.Value))).ToArray()),
        JsonValueKind.Array => new PyList(value.EnumerateArray().Select(ToPy).ToArray()),
        JsonValueKind.String => new PyStr(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out long n) => new PyLong(n),
        JsonValueKind.Number => new PyReal(value.GetDouble()),
        JsonValueKind.True => PyBool.True,
        JsonValueKind.False => PyBool.False,
        JsonValueKind.Null => PyNone.Instance,
        _ => throw new ArgumentException("Invalid scientific output."),
    };
}
