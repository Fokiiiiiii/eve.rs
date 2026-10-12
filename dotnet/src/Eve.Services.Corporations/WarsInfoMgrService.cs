using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

// Wire behavior follows EveJS v0.12.9 standingMgrService.js / standingRuntime.js (828823a75f211fdecb945b087a26c225f111491f)
// and eve-logic V24.01 standingsvc.py (1dbb5711a072a146e1d228365668ac05655eefe1).
/// <summary>The client's standing manager, backed by shared affiliation and standing relations.</summary>
/// <summary>Read-only projection of war relations into the warsInfoMgr wire contract.</summary>
[Eve.Services.Service("warsInfoMgr")]
public sealed partial class WarsInfoMgrService
{
    private const string WarKind = "war";
    private const string WarAllyKind = "war.ally";
    private const string RowsetName = "carbon.common.script.sys.crowset.CRowset";
    private const long MaxSafeInteger = 9_007_199_254_740_991L;
    private const long FileTimeEpochThreshold = 100_000_000_000_000_000L;
    private static readonly (string Name, int Type)[] WarColumns =
    [
        ("warID", 0x03), ("declaredByID", 0x03), ("againstID", 0x03),
        ("timeDeclared", 0x40), ("timeFinished", 0x40), ("retracted", 0x40),
        ("retractedBy", 0x03), ("timeStarted", 0x40), ("billID", 0x03),
        ("mutual", 0x0b), ("createdFromWarID", 0x03), ("openForAllies", 0x0b),
        ("canBeRetracted", 0x0b), ("reasonEnded", 0x11), ("warHQ", 0x14),
        ("noOfAllies", 0x03), ("reasonStarted", 0x11)
    ];
    private static readonly System.Text.RegularExpressions.Regex PickledInt =
        new(@"(?:^|\n)I(-?\d+)\na", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex PickledLong =
        new(@"(?:^|\n)L(-?\d+)L\na", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly Eve.Relations.RelationStore _relations;
    private readonly Func<long> _nowUnixMs;

    public WarsInfoMgrService(Eve.Relations.RelationStore relations, Func<long> nowUnixMs)
    {
        _relations = relations;
        _nowUnixMs = nowUnixMs;
    }

    [Eve.Services.Method("GetWarsByOwnerID")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsByOwnerID(Eve.Services.ServiceCall call)
    {
        var ownerId = PositiveId(call.Arg(0)) ?? 0;
        var wars = await WarsAsync(call.Cancellation);
        WarRecord[] rows = ownerId == 0 ? [] : wars.Where(war => Owns(war, ownerId)).OrderBy(war => war.WarId).ToArray();
        return Eve.Services.ObjectCaching.MethodResult(Rowset(rows), _nowUnixMs(), "15 minutes");
    }

    [Eve.Services.Method("GetWarsByOwners")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsByOwners(Eve.Services.ServiceCall call)
    {
        var ownerIds = OwnerIds(call.Arg(0));
        var wars = await WarsAsync(call.Cancellation);
        return new Eve.Marshal.PyDict([.. ownerIds.Select(ownerId =>
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                Number(ownerId),
                new Eve.Marshal.PyDict([.. wars.Where(war => Owns(war, ownerId)).OrderBy(war => war.WarId)
                    .Select(war => new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(Number(war.WarId), Payload(war)))])))]);
    }

    [Eve.Services.Method("GetWarsRequiringAssistance")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsRequiringAssistance(Eve.Services.ServiceCall call)
    {
        var ownerId = IntegerValue(call.Arg(0));
        if (ownerId == 0)
        {
            ownerId = SessionOwnerId(call.Session);
        }
        if (ownerId == 0)
        {
            return new Eve.Marshal.PyList([]);
        }

        var wars = await WarsAsync(call.Cancellation);
        return new Eve.Marshal.PyList([.. wars
            .Where(war => war.AgainstId == ownerId && war.OpenForAllies)
            .OrderBy(war => war.WarId)
            .Select(Payload)]);
    }

    [Eve.Services.Method("GetTop50")]
    public async ValueTask<Eve.Marshal.PyValue> GetTop50(Eve.Services.ServiceCall call)
    {
        var maximum = IntegerValue(call.Arg(0));
        if (maximum == 0)
        {
            maximum = MaxSafeInteger;
        }
        var wars = await WarsAsync(call.Cancellation);
        var rows = wars.Where(war => war.WarId < maximum).OrderByDescending(war => war.WarId).Take(50).ToArray();
        return Eve.Services.ObjectCaching.MethodResult(Rowset(rows), _nowUnixMs(), "15 minutes");
    }

    [Eve.Services.Method("GetPublicWarInfo")]
    public async ValueTask<Eve.Marshal.PyValue> GetPublicWarInfo(Eve.Services.ServiceCall call)
    {
        var warId = PositiveId(call.Arg(0)) ?? 0;
        if (warId == 0)
        {
            return Eve.Marshal.PyNone.Instance;
        }

        var wars = await WarsAsync(call.Cancellation);
        var war = wars.FirstOrDefault(candidate => candidate.WarId == warId);
        return war is null ? Eve.Marshal.PyNone.Instance : Payload(war);
    }

    [Eve.Services.Method("GetWarsForStructure")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsForStructure(Eve.Services.ServiceCall call)
    {
        var structureId = PositiveId(call.Arg(0)) ?? 0;
        if (structureId == 0)
        {
            return new Eve.Marshal.PyList([]);
        }

        var wars = await WarsAsync(call.Cancellation);
        return new Eve.Marshal.PyList([.. wars.Where(war => war.WarHqId == structureId)
            .OrderByDescending(war => war.TimeDeclared)
            .ThenBy(war => war.WarId)
            .Select(Payload)]);
    }

    private Task<IReadOnlyList<WarRecord>> WarsAsync(CancellationToken token) => ReadWarsAsync(_relations, token);

    /// <summary>Every war with its allies, read from the war relations (shared with the war registry).</summary>
    internal static async Task<IReadOnlyList<WarRecord>> ReadWarsAsync(Eve.Relations.RelationStore relations, CancellationToken token)
    {
        var warsTask = relations.ListAsync(WarKind, token);
        var alliesTask = relations.ListAsync(WarAllyKind, token);
        await Task.WhenAll(warsTask, alliesTask);
        var allies = alliesTask.Result.GroupBy(row => row.Object)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Subject).ToArray());
        return [.. warsTask.Result.Select(war =>
        {
            allies.TryGetValue(war.Id, out var warAllies);
            return Record(war, warAllies ?? []);
        })];
    }

    private static WarRecord Record(Eve.Relations.Relation relation, IReadOnlyList<Eve.Relations.Relation> allies)
    {
        using var document = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(relation.AttrsJson) ? "{}" : relation.AttrsJson);
        var attrs = document.RootElement.Clone();
        return new WarRecord(
            relation.Id, relation.Subject, relation.Object,
            FileTime(attrs, "timeDeclared", relation.SinceMs),
            FileTime(attrs, "timeStarted", relation.SinceMs),
            OptionalFileTime(attrs, "timeFinished", relation.UntilMs),
            OptionalFileTime(attrs, "retracted", null),
            OptionalLong(attrs, "retractedBy"),
            OptionalLong(attrs, "billID"),
            Flag(attrs, "mutual"), Flag(attrs, "openForAllies"),
            OptionalLong(attrs, "createdFromWarID"),
            OptionalLong(attrs, "warHQID"),
            OptionalLong(attrs, "warHQ"),
            Integer(attrs, "reasonEnded"), Integer(attrs, "reasonStarted"),
            Number(attrs, "reward"),
            [.. allies.Select(ReadAlly)]);
    }

    private static AllyInfo ReadAlly(Eve.Relations.Relation relation)
    {
        using var document = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(relation.AttrsJson) ? "{}" : relation.AttrsJson);
        var attrs = document.RootElement.Clone();
        return new AllyInfo(relation.Subject, FileTime(attrs, "timeStarted", relation.SinceMs),
            OptionalFileTime(attrs, "timeFinished", relation.UntilMs));
    }

    internal static bool Owns(WarRecord war, long id) =>
        war.DeclaredById == id || war.AgainstId == id || war.Allies.Any(ally => ally.AllyId == id);

    private static Eve.Marshal.PyValue Payload(WarRecord war)
    {
        var allies = new Eve.Marshal.PyDict([.. war.Allies.Select(ally =>
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                Number(ally.AllyId),
                Eve.Services.Py.KeyVal(
                    ("allyID", Number(ally.AllyId)),
                    ("timeStarted", Eve.Services.Py.FileTimeLong(ally.TimeStarted)),
                    ("timeFinished", Time(ally.TimeFinished)))))]);

        return Eve.Services.Py.KeyVal(
            ("warID", Number(war.WarId)),
            ("declaredByID", Number(war.DeclaredById)),
            ("againstID", Number(war.AgainstId)),
            ("warHQID", OptionalNumber(war.WarHqId)),
            ("warHQ", OptionalNumber(war.WarHqId)),
            ("timeDeclared", Eve.Services.Py.FileTimeLong(war.TimeDeclared)),
            ("timeStarted", Eve.Services.Py.FileTimeLong(war.TimeStarted)),
            ("timeFinished", Time(war.TimeFinished)),
            ("retracted", Time(war.Retracted)),
            ("retractedBy", OptionalNumber(war.RetractedBy)),
            ("billID", OptionalNumber(war.BillId)),
            ("mutual", new Eve.Marshal.PyInt(war.Mutual ? 1 : 0)),
            ("openForAllies", new Eve.Marshal.PyInt(war.OpenForAllies ? 1 : 0)),
            ("createdFromWarID", OptionalNumber(war.CreatedFromWarId)),
            ("reward", Eve.Services.Py.Number(war.Reward)),
            ("allies", allies));
    }

    private static Eve.Marshal.PyValue Rowset(IReadOnlyList<WarRecord> wars)
    {
        var descriptor = Eve.Marshal.Descriptor.FromHeader(new Eve.Marshal.PyObjectEx(
            Eve.Marshal.ObjectExKind.Ex1,
            Eve.Services.Py.Tuple(new Eve.Marshal.PyToken("blue.DBRowDescriptor"),
                Eve.Services.Py.Tuple(Eve.Services.Py.Tuple([.. WarColumns.Select(column =>
                    (Eve.Marshal.PyValue)Eve.Services.Py.Tuple(new Eve.Marshal.PyStr(column.Name), new Eve.Marshal.PyInt(column.Type)))]))),
            [], []));
        var header = Eve.Services.Py.Tuple(
            Eve.Services.Py.Tuple(new Eve.Marshal.PyToken(RowsetName)),
            Eve.Services.Py.Dict(("header", descriptor.Header)));
        var rows = wars.Select(war => (Eve.Marshal.PyValue)new Eve.Marshal.PyPackedRow(descriptor,
        [
            Number(war.WarId), Number(war.DeclaredById), Number(war.AgainstId),
            Eve.Services.Py.FileTimeLong(war.TimeDeclared), Time(war.TimeFinished), Time(war.Retracted),
            OptionalNumber(war.RetractedBy), Eve.Services.Py.FileTimeLong(war.TimeStarted), OptionalNumber(war.BillId),
            new Eve.Marshal.PyInt(war.Mutual ? 1 : 0), OptionalNumber(war.CreatedFromWarId),
            new Eve.Marshal.PyInt(war.OpenForAllies ? 1 : 0),
            new Eve.Marshal.PyInt(war.TimeFinished is null && war.Retracted is null ? 1 : 0),
            new Eve.Marshal.PyInt(war.ReasonEnded), OptionalNumber(war.WarHqId ?? war.WarHq),
            new Eve.Marshal.PyInt(war.Allies.Count), new Eve.Marshal.PyInt(war.ReasonStarted)
        ]));
        return new Eve.Marshal.PyObjectEx(Eve.Marshal.ObjectExKind.Ex2, header, [.. rows], []);
    }

    private static long SessionOwnerId(Eve.Services.ClientSession session) =>
        PositiveId(session.Get("allianceID")) ?? PositiveId(session.Get("allianceid")) ??
        PositiveId(session.Get("corporationID")) ?? PositiveId(session.Get("corpid")) ?? 0;

    private static long? PositiveId(Eve.Marshal.PyValue? value)
    {
        var id = IntegerValue(value);
        return id > 0 ? id : null;
    }

    private static long IntegerValue(Eve.Marshal.PyValue? value) => value switch
    {
        Eve.Marshal.PyInt integer => integer.Value,
        Eve.Marshal.PyLong integer => integer.Value,
        Eve.Marshal.PyBool boolean => boolean.Value ? 1 : 0,
        Eve.Marshal.PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        Eve.Marshal.PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        Eve.Marshal.PyWString text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        Eve.Marshal.PyToken text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0
    };

    private static long[] OwnerIds(Eve.Marshal.PyValue raw)
    {
        IEnumerable<Eve.Marshal.PyValue> values = raw switch
        {
            Eve.Marshal.PyList list => list.Items,
            Eve.Marshal.PyTuple tuple => tuple.Items,
            Eve.Marshal.PyObjectEx { Kind: Eve.Marshal.ObjectExKind.Ex1, Header: Eve.Marshal.PyTuple header }
                when header.Items.Count > 1 && IsSetName(header.Items[0]) =>
                    header.Items[1] is Eve.Marshal.PyTuple args && args.Items.Count > 0 ? Sequence(args.Items[0]) : [],
            Eve.Marshal.PyCPickled pickle => PickledOwnerIds(pickle),
            _ => []
        };
        return [.. values.Select(IntegerValue).Where(id => id != 0).Distinct()];
    }

    private static bool IsSetName(Eve.Marshal.PyValue value) =>
        value is Eve.Marshal.PyToken token && token.Value is "__builtin__.set" or "builtins.set";

    private static IEnumerable<Eve.Marshal.PyValue> Sequence(Eve.Marshal.PyValue value) => value switch
    {
        Eve.Marshal.PyList list => list.Items,
        Eve.Marshal.PyTuple tuple => tuple.Items,
        _ => []
    };

    private static IEnumerable<Eve.Marshal.PyValue> PickledOwnerIds(Eve.Marshal.PyCPickled pickle)
    {
        var text = System.Text.Encoding.ASCII.GetString(pickle.Value.Span);
        if (!text.Contains("set\n", StringComparison.Ordinal) ||
            (!text.Contains("c__builtin__\nset\n", StringComparison.Ordinal) &&
             !text.Contains("cbuiltins\nset\n", StringComparison.Ordinal)))
        {
            return [];
        }

        var values = new List<Eve.Marshal.PyValue>();
        foreach (System.Text.RegularExpressions.Match match in PickledInt.Matches(text))
        {
            if (long.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(new Eve.Marshal.PyLong(value));
            }
        }
        foreach (System.Text.RegularExpressions.Match match in PickledLong.Matches(text))
        {
            if (long.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(new Eve.Marshal.PyLong(value));
            }
        }
        return values;
    }

    private static Eve.Marshal.PyValue Number(long value) => Eve.Services.Py.Number(value);
    private static Eve.Marshal.PyValue OptionalNumber(long? value) => value is { } id ? Number(id) : Eve.Marshal.PyNone.Instance;
    private static Eve.Marshal.PyValue Time(long? value) => value is { } filetime ? Eve.Services.Py.FileTimeLong(filetime) : Eve.Marshal.PyNone.Instance;

    private static long FileTime(System.Text.Json.JsonElement attrs, string key, long fallbackUnixMs) =>
        TryLong(attrs, key, out var value) ? NormalizeTime(value) : Eve.Services.Py.FileTimeFromUnixMs(fallbackUnixMs);

    private static long? OptionalFileTime(System.Text.Json.JsonElement attrs, string key, long? fallbackUnixMs)
    {
        if (TryLong(attrs, key, out var value))
        {
            return value == 0 ? null : NormalizeTime(value);
        }
        return fallbackUnixMs is { } unixMs ? Eve.Services.Py.FileTimeFromUnixMs(unixMs) : null;
    }

    private static long NormalizeTime(long value) =>
        value == 0 || value >= FileTimeEpochThreshold ? value : Eve.Services.Py.FileTimeFromUnixMs(value);

    private static long? OptionalLong(System.Text.Json.JsonElement attrs, string key) =>
        TryLong(attrs, key, out var value) && value != 0 ? value : null;

    private static int Integer(System.Text.Json.JsonElement attrs, string key) =>
        TryLong(attrs, key, out var value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;

    private static double Number(System.Text.Json.JsonElement attrs, string key)
    {
        if (TryProperty(attrs, key, out var value))
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                return number;
            }
            if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) &&
                double.IsFinite(number))
            {
                return number;
            }
        }
        return 0;
    }

    private static bool Flag(System.Text.Json.JsonElement attrs, string key)
    {
        if (!TryProperty(attrs, key, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => false,
            System.Text.Json.JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
            System.Text.Json.JsonValueKind.String => bool.TryParse(value.GetString(), out var boolean)
                ? boolean
                : long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer) && integer != 0,
            _ => false
        };
    }

    private static bool TryLong(System.Text.Json.JsonElement attrs, string key, out long value)
    {
        if (TryProperty(attrs, key, out var element))
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt64(out value))
            {
                return true;
            }
            if (element.ValueKind == System.Text.Json.JsonValueKind.String &&
                long.TryParse(element.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static bool TryProperty(System.Text.Json.JsonElement attrs, string key, out System.Text.Json.JsonElement value)
    {
        if (attrs.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in attrs.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    internal sealed record WarRecord(long WarId, long DeclaredById, long AgainstId, long TimeDeclared,
        long TimeStarted, long? TimeFinished, long? Retracted, long? RetractedBy, long? BillId,
        bool Mutual, bool OpenForAllies, long? CreatedFromWarId, long? WarHqId, long? WarHq,
        int ReasonEnded, int ReasonStarted, double Reward, IReadOnlyList<AllyInfo> Allies);

    internal sealed record AllyInfo(long AllyId, long TimeStarted, long? TimeFinished);
}
