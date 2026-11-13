using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.ProjectDiscovery;

// Thin adapter for the pinned COVID protocol; it does not invent live MMOS results.
public sealed class ScientificReplayRpc
{
    private readonly ScientificReplayProvider provider;
    private readonly ProjectDiscoveryTaskPinStore pins;
    private readonly CovidTutorialProvider? tutorials;

    public ScientificReplayRpc(ScientificReplayProvider provider, ProjectDiscoveryTaskPinStore pins)
        : this(provider, pins, null) { }

    internal ScientificReplayRpc(ScientificReplayProvider provider, ProjectDiscoveryTaskPinStore pins, CovidTutorialProvider? tutorials)
    { this.provider = provider; this.pins = pins; this.tutorials = tutorials; }

    public async ValueTask<Eve.Services.Core.ProjectDiscoveryPlayerStatistics?> ReadStatisticsAsync(long characterId, CancellationToken ct) =>
        tutorials is null ? await provider.ReadStatisticsAsync(characterId, ct) : await tutorials.ReadStatisticsAsync(characterId, ct) ?? await provider.ReadStatisticsAsync(characterId, ct);

    public ValueTask<Eve.Services.Core.ProjectDiscoveryPlayerState?> ReadPlayerStateAsync(long characterId, CancellationToken ct) =>
        provider.ReadPlayerStateAsync(characterId, ct);

    public ValueTask<int> ReadBonusSamplesAsync(long characterId, string method, CancellationToken ct) =>
        provider.ReadBonusSamplesAsync(characterId, method, ct);

    public async ValueTask<PyValue> GetTaskAsync(ServiceCall call)
    {
        try
        {
            await RequireNoUnknownPinAsync(call);
            if (TutorialCode(call) is { } tutorialCode)
            {
                if (tutorials is null) throw new DiscoveryAssignmentException("COVID tutorial task provider is unavailable.");
                return ToPy(await tutorials.GetTaskAsync(call.Session, tutorialCode, call.Cancellation));
            }
            if (tutorials is not null) await tutorials.ClearActiveAsync(call.Session, call.Cancellation);
            return ToPy(await provider.GetTaskAsync(call.Session, call.Cancellation));
        }
        catch (ScientificReplayExhaustedException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_EXHAUSTED"); }
        catch (DiscoveryAssignmentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE"); }
        catch (ArgumentException) { throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID"); }
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
            if (tutorials is not null && await tutorials.SubmitIfActiveAsync(call.Session, $"{call.Session.SessionId}:{id}", result, call.Cancellation) is { } tutorialResponse)
                return ToClient(tutorialResponse);
            // The current COVID result UI requires player.score; its UI does not emit skip=true.
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("skip", out var skip) || skip.ValueKind != JsonValueKind.False)
                throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
            return ToClient(await provider.SubmitAsync(call.Session, $"{call.Session.SessionId}:{id}", result, timing, call.Cancellation));
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

    private static string? TutorialCode(ServiceCall call)
    {
        if (call.Args.Count != 0) throw new ArgumentException("Project Discovery task arguments must be keyword arguments.");
        if (call.Kwargs is PyNone or PyDict { Entries.Count: 0 }) return null;
        if (call.Kwargs is not PyDict { Entries.Count: 2 } kwargs) throw new ArgumentException("Invalid tutorial task arguments.");
        string? code = null;
        bool isTutorial = false;
        foreach (var (key, value) in kwargs.Entries)
        {
            switch (JsSemantics.StringValue(key))
            {
                case "original_code" when value is PyStr or PyWString or PyBytes:
                    code = JsSemantics.StringValue(value);
                    break;
                case "is_tutorial_task" when value is PyBool flag:
                    isTutorial = flag.Value;
                    break;
                default:
                    throw new ArgumentException("Invalid tutorial task arguments.");
            }
        }
        if (!isTutorial || string.IsNullOrWhiteSpace(code) || !CovidTutorialCorpus.Contains(code))
            throw new ArgumentException("Unknown tutorial task.");
        return code;
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

    private static PyValue ToClient(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return ToPy(value);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var property in value.EnumerateObject())
        {
            PyValue child = property.NameEquals("playerState") && property.Value.ValueKind == JsonValueKind.Object
                ? Py.KeyVal(property.Value.EnumerateObject().Select(field =>
                    new KeyValuePair<PyValue, PyValue>(new PyStr(field.Name), ToPy(field.Value))))
                : ToPy(property.Value);
            entries.Add(new(new PyStr(property.Name), child));
        }
        return new PyDict(entries);
    }
}
