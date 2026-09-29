using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eve.Marshal.Tests;

/// <summary>A JavaScript value as the marshal reference sees it (what the golden fixtures describe).</summary>
internal abstract record Js
{
    public Js? Get(string name) => this is JsObj obj ? obj.Fields.FirstOrDefault(f => f.Key == name).Value : null;

    public string? AsString() => (this as JsStr)?.Value;
}

internal sealed record JsNull : Js
{
    public static readonly JsNull Instance = new();
}

internal sealed record JsBool(bool Value) : Js;

/// <summary>Any JS number (the IEEE-754 double itself).</summary>
internal sealed record JsNum(double Value) : Js;

/// <summary>A BigInt, as its decimal digits.</summary>
internal sealed record JsBig(string Digits) : Js;

internal sealed record JsStr(string Value) : Js;

/// <summary>A Buffer.</summary>
internal sealed record JsBuf(byte[] Value) : Js;

internal sealed record JsArr(List<Js> Items) : Js;

/// <summary>A plain object; <c>undefined</c> fields are absent.</summary>
internal sealed record JsObj(List<KeyValuePair<string, Js>> Fields) : Js;

/// <summary>EJSON: see <c>tools/oracle/ejson.js</c> for the format.</summary>
internal static class Ejson
{
    public static Js From(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return JsNull.Instance;
            case JsonArray array:
                return new JsArr([.. array.Select(From)]);
            case JsonObject obj:
                if (obj.Count != 1)
                {
                    throw new InvalidDataException("bad EJSON tag");
                }
                var (tag, body) = obj.Single();
                switch (tag)
                {
                    case "$int":
                        return new JsNum(long.Parse((string)body!, CultureInfo.InvariantCulture));
                    case "$f64":
                        return new JsNum(BitConverter.UInt64BitsToDouble(ulong.Parse((string)body!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                    case "$big":
                        return new JsBig((string)body!);
                    case "$buf":
                        return new JsBuf(Convert.FromHexString((string)body!));
                    case "$obj":
                        return new JsObj([.. ((JsonObject)body!).Select(p => new KeyValuePair<string, Js>(p.Key, From(p.Value)))]);
                    default:
                        throw new InvalidDataException($"unknown EJSON tag {tag}");
                }
            case JsonValue value:
                var element = value.GetValue<JsonElement>();
                return element.ValueKind switch
                {
                    JsonValueKind.True => new JsBool(true),
                    JsonValueKind.False => new JsBool(false),
                    JsonValueKind.String => new JsStr(element.GetString()!),
                    JsonValueKind.Number => throw new InvalidDataException("bare JSON number in EJSON"),
                    _ => JsNull.Instance,
                };
            default:
                throw new InvalidDataException("unexpected node");
        }
    }

    /// <summary>Safe-integer numbers as <c>$int</c>, every other number as the exact bits.</summary>
    public static JsonNode? To(Js value)
    {
        switch (value)
        {
            case JsNull:
                return null;
            case JsBool flag:
                return JsonValue.Create(flag.Value);
            case JsStr text:
                return JsonValue.Create(text.Value);
            case JsNum number:
                var v = number.Value;
                var safe = double.IsFinite(v) && Math.Truncate(v) == v && Math.Abs(v) <= 9_007_199_254_740_991.0
                    && !(v == 0.0 && double.IsNegative(v));
                return safe
                    ? new JsonObject { ["$int"] = ((long)v).ToString(CultureInfo.InvariantCulture) }
                    : new JsonObject { ["$f64"] = BitConverter.DoubleToUInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture) };
            case JsBig big:
                return new JsonObject { ["$big"] = big.Digits };
            case JsBuf buf:
                return new JsonObject { ["$buf"] = Convert.ToHexStringLower(buf.Value) };
            case JsArr array:
                return new JsonArray([.. array.Items.Select(To)]);
            case JsObj obj:
                var fields = new JsonObject();
                foreach (var (name, field) in obj.Fields)
                {
                    fields[name] = To(field);
                }
                return new JsonObject { ["$obj"] = fields };
            default:
                throw new InvalidOperationException();
        }
    }
}
