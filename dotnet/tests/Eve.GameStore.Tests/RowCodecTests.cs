using System.Text.Json.Nodes;

namespace Eve.GameStore.Tests;

/// <summary>Replays the row codec cases recorded from the reference server.</summary>
public class RowCodecTests
{
    private static readonly JsonArray Cases = (JsonArray)Fixtures.Read("rowcodec.json")!;

    public static IEnumerable<object[]> CaseIds() => Cases.Select(c => new object[] { (string)c!["id"]! });

    private static JsonObject Case(string id) => (JsonObject)Cases.Single(c => (string)c!["id"]! == id)!;

    private static JsonObject? Rows(JsonObject record) => (JsonObject?)record["rows"];

    [Fact]
    public void The_fixture_has_the_expected_shape()
    {
        Assert.True(Cases.Count >= 240);
        Assert.Equal(Cases.Count, Cases.Select(c => (string)c!["id"]!).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Explode_assemble_and_row_lookup_match_the_reference(string id)
    {
        var record = Case(id);
        var table = (string)record["table"]!;
        var isAssembleOnly = (string?)record["kind"] == "assemble";

        if (!isAssembleOnly)
        {
            var obj = record["object"] as JsonObject;
            if (record["explodeError"] is JsonObject error)
            {
                var thrown = Assert.Throws<InvalidOperationException>(() => RowCodec.Explode(table, obj));
                Assert.Equal((string)error["message"]!, thrown.Message);
                return;
            }

            var exploded = RowCodec.Explode(table, obj);
            var expectedRows = Rows(record)!;
            Assert.Equal(Fixtures.Canon(expectedRows), Fixtures.Canon(ToObject(exploded)));

            foreach (var probe in (JsonArray)record["valueForKey"]!)
            {
                var rowKey = (string)probe!["rowKey"]!;
                var expected = probe["value"];
                var found = RowCodec.TryGetRowValue(table, obj, rowKey, out var value);
                var isUndefined = expected is JsonObject marker && marker.ContainsKey("$undefined");
                Assert.True(found != isUndefined, $"{id} row {rowKey}: found={found}");
                if (found)
                {
                    Assert.Equal(Fixtures.Canon(expected), Fixtures.Canon(value));
                }
            }
        }

        var assembled = RowCodec.Assemble(table, Rows(record)!.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
        Assert.Equal(Fixtures.Canon(record["assembled"]), Fixtures.Canon(assembled));
    }

    private static JsonObject ToObject(IEnumerable<KeyValuePair<string, JsonNode?>> rows)
    {
        var result = new JsonObject();
        foreach (var (key, value) in rows)
        {
            result[key] = value?.DeepClone();
        }
        return result;
    }

    [Fact]
    public void Explode_copies_so_the_source_object_keeps_its_nodes()
    {
        var source = Fixtures.Obj("""{"messages":{"1":{"a":1}},"x":{"b":2}}""");
        var rows = RowCodec.Explode("mail", source);
        rows["messages\u001f1"]!["a"] = 99;
        Assert.Equal(1, (int)source["messages"]!["1"]!["a"]!);
    }

    [Fact]
    public void Assemble_puts_entity_rows_over_a_legacy_blob_whatever_the_row_order()
    {
        var blob = Fixtures.Obj("""{"a":1,"b":2}""");
        var entity = JsonNode.Parse("20");
        var forward = RowCodec.Assemble("mail", [new("messages", blob.DeepClone()), new("messages\u001fb", entity!.DeepClone())]);
        var backward = RowCodec.Assemble("mail", [new("messages\u001fb", entity.DeepClone()), new("messages", blob.DeepClone())]);
        Assert.Equal(Fixtures.Canon(forward), Fixtures.Canon(backward));
        Assert.Equal(20, (int)forward["messages"]!["b"]!);
        Assert.Equal(1, (int)forward["messages"]!["a"]!);
    }

    [Fact]
    public void A_flat_key_containing_the_separator_is_not_addressable_by_row_lookup()
    {
        // The reference has the same gap: the key is read as group/entity.
        var table = Fixtures.Obj("""{"a\u001fb":1}""");
        Assert.False(RowCodec.TryGetRowValue("characters", table, "a\u001fb", out _));
    }
}
