using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core.Tests;

public class NotificationMgrServiceTests
{
    private const long CharacterId = 900001;
    private const long Created = 134000000000000123;
    private const long ClockUnixMs = 1755526400000;
    private const long ClockFileTime = 134000000000000000;

    [Theory]
    [InlineData("GetAllNotifications")]
    [InlineData("GetByGroupID")]
    [InlineData("GetUnprocessed")]
    public async Task Missing_mailbox_returns_an_empty_python_list(string method)
    {
        var source = new Mailbox();
        var service = Service(source);
        Assert.Equal("notificationMgr", service.Name);
        Assert.True(service.HasMethod(method));
        var result = Assert.IsType<PyList>(await service.CallAsync(Call(method)));
        Assert.Empty(result.Items);
        Assert.Equal(CharacterId, source.LastCharacterId);
        Assert.Equal(MarshalCodec.Encode(new PyList([])), MarshalCodec.Encode(result));
    }

    [Theory]
    [InlineData("GetAllNotifications")]
    [InlineData("GetByGroupID")]
    [InlineData("GetUnprocessed")]
    public async Task No_character_does_not_read_a_mailbox(string method)
    {
        var source = new Mailbox();
        var call = Call(method);
        call.Session.Set("charid", PyNone.Instance);
        Assert.Empty(Assert.IsType<PyList>(await Service(source).CallAsync(call)).Items);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Backed_rows_have_exact_reference_fields_types_and_descending_ids()
    {
        var source = new Mailbox
        {
            Box = JsonNode.Parse("""
                {"order":[2,2,99],"byID":{
                  "2":{"typeID":27,"senderID":-1,"receiverID":900001,"processed":true,
                       "created":"134000000000000123","groupID":8,"data":{"declaredByID":980001,"mutual":false}},
                  "5":{"typeID":35,"created":"134000000000000122","data":null},
                  "9":null
                }}
                """)!.AsObject(),
        };
        var before = source.Box.ToJsonString();
        var actual = Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications")));
        var expected = new PyList([
            Row(5, 35, 0, false, Created - 1, Py.Dict()),
            Row(2, 27, -1, true, Created, Py.Dict(("declaredByID", new PyInt(980001)), ("mutual", PyValue.Of(false)))),
        ]);
        Assert.Equal([5L, 2L], Ids(actual));
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(actual));
        Assert.Equal(before, source.Box.ToJsonString());
        Assert.IsType<PyLong>(Field(actual.Items[1], "created"));
        Assert.Equal(7, Assert.IsType<PyDict>(Assert.IsType<PyObject>(actual.Items[1]).Args).Entries.Count);
    }

    [Fact]
    public async Task From_id_is_exclusive_and_a_positive_keyword_overrides_position()
    {
        var source = new Mailbox { Box = Box() };
        var service = Service(source);
        Assert.Equal<long[]>([4L, 3L], Ids(await service.CallAsync(Call("GetAllNotifications", args: [new PyInt(2)]))));
        Assert.Equal<long[]>([4L], Ids(await service.CallAsync(Call("GetAllNotifications",
            Py.Dict(("fromID", new PyReal(3.9))), new PyInt(1)))));
        Assert.Equal<long[]>([4L, 3L], Ids(await service.CallAsync(Call("GetAllNotifications",
            Py.Dict(("fromID", new PyInt(-1))), new PyInt(2)))));
        Assert.Empty(Ids(await service.CallAsync(Call("GetAllNotifications", args: [new PyInt(4)]))));
    }

    [Fact]
    public async Task Groups_and_unprocessed_use_the_stored_group_and_strict_boolean_flag()
    {
        var source = new Mailbox { Box = Box() };
        var service = Service(source);
        Assert.Equal<long[]>([3L, 1L], Ids(await service.CallAsync(Call("GetByGroupID", args: [new PyInt(8)]))));
        Assert.Equal<long[]>([2L], Ids(await service.CallAsync(Call("GetByGroupID",
            Py.Dict(("groupID", new PyStr("3"))), new PyInt(8)))));
        Assert.Equal<long[]>([4L, 3L, 2L, 1L], Ids(await service.CallAsync(Call("GetByGroupID", args: [new PyInt(0)]))));
        Assert.Empty(Ids(await service.CallAsync(Call("GetByGroupID", args: [new PyInt(999)]))));
        Assert.Equal<long[]>([4L, 3L, 2L], Ids(await service.CallAsync(Call("GetUnprocessed"))));
    }

    [Fact]
    public async Task Each_call_reads_only_the_selected_character_and_sees_updated_data()
    {
        var source = new Mailbox { Box = Box() };
        var service = Service(source);
        Assert.Equal(4, Ids(await service.CallAsync(Call("GetAllNotifications"))).Length);
        source.Box["byID"]!.AsObject().Remove("4");
        var call = Call("GetAllNotifications", Py.Dict(("characterID", new PyInt(999999))));
        Assert.Equal(3, Ids(await service.CallAsync(call)).Length);
        Assert.Equal(CharacterId, source.LastCharacterId);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Nested_notification_data_keeps_its_python_value_shapes()
    {
        var source = new Mailbox { Box = JsonNode.Parse("""
            {"byID":{"1":{"typeID":27,"created":"134000000000000123",
                "data":{"name":"戦争","amount":12.5,"active":true,"missing":null,"ids":[4,5]}}}}
            """)!.AsObject() };
        var row = Assert.Single(Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications"))).Items);
        var data = Field(row, "data");
        var expected = Py.Dict(("name", new PyStr("戦争")), ("amount", new PyReal(12.5)),
            ("active", PyValue.Of(true)), ("missing", PyNone.Instance), ("ids", new PyList([new PyInt(4), new PyInt(5)])));
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(data));
    }

    [Fact]
    public async Task Current_domain_json_numbers_are_supported_and_foreign_recipients_are_filtered()
    {
        var source = new Mailbox
        {
            Box = new JsonObject
            {
                ["byID"] = new JsonObject
                {
                    ["5"] = new JsonObject
                    {
                        ["notificationID"] = 5L, ["receiverID"] = CharacterId, ["typeID"] = 27,
                        ["created"] = Created, ["senderID"] = (uint)1000001,
                        ["data"] = new JsonObject { ["amount"] = 1.25m, ["count"] = 2 },
                    },
                    ["6"] = new JsonObject { ["notificationID"] = 6, ["receiverID"] = CharacterId + 1, ["typeID"] = 27 },
                },
            },
        };
        var row = Assert.Single(Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications"))).Items);
        Assert.Equal(new PyInt(5), Field(row, "notificationID"));
        Assert.Equal(new PyInt(1000001), Field(row, "senderID"));
        Assert.Equal(new PyLong(Created), Field(row, "created"));
        Assert.Equal(Py.Dict(("amount", new PyReal(1.25)), ("count", new PyInt(2))), Field(row, "data"));
    }

    [Fact]
    public async Task Persisted_mailbox_is_reloaded_without_changing_the_saved_records()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"eve-notifications-{Guid.NewGuid():N}.json");
        try
        {
            var box = Box();
            await File.WriteAllTextAsync(path, box.ToJsonString());
            var service = new NotificationMgrService(async (id, ct) =>
                JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject(), () => ClockUnixMs);
            var first = Ids(await service.CallAsync(Call("GetAllNotifications")));
            Assert.Equal([4L, 3L, 2L, 1L], first);
            box["byID"]!.AsObject().Remove("4");
            await File.WriteAllTextAsync(path, box.ToJsonString());
            var second = Ids(await service.CallAsync(Call("GetAllNotifications")));
            Assert.Equal([3L, 2L, 1L], second);
            Assert.Equal(box.ToJsonString(), await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancelled_read_does_not_access_storage()
    {
        var source = new Mailbox();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var call = new ServiceCall { Method = "GetAllNotifications", Args = [], Session = Session(), Cancellation = cancellation.Token };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Service(source).CallAsync(call));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task New_mail_data_uses_a_keyval_message_and_exact_filetime()
    {
        var source = new Mailbox { Box = JsonNode.Parse("""
            {"byID":{"1":{"typeID":1004,"created":"134000000000000123","data":{
              "senderName":"Pilot","subject":"Orders","msg":{"messageID":10,"senderID":-1,
              "sentDate":"134000000000000121","toCharacterIDs":[900001,"900002",0,-1,3.8],
              "toListID":0,"toCorpOrAllianceID":980001,"statusMask":-2,"labelMask":3.9,
              "read":true,"trashed":1,"forwarded":false}}}}}
            """)!.AsObject() };
        var row = Assert.Single(Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications"))).Items);
        var data = Assert.IsType<PyDict>(Field(row, "data"));
        var msg = Value(data, "msg");
        var expected = Py.KeyVal(("messageID", new PyInt(10)), ("senderID", new PyInt(-1)),
            ("senderName", new PyStr("Pilot")), ("sentDate", new PyLong(Created - 2)),
            ("toCharacterIDs", new PyList([new PyInt(900001), new PyInt(900002), new PyInt(3)])),
            ("toListID", PyNone.Instance), ("toCorpOrAllianceID", new PyInt(980001)),
            ("subject", new PyStr("Orders")), ("statusMask", new PyInt(0)), ("labelMask", new PyInt(3)),
            ("read", PyValue.Of(true)), ("trashed", PyValue.Of(false)), ("replied", PyValue.Of(false)), ("forwarded", PyValue.Of(false)));
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(msg));
    }

    [Fact]
    public async Task Standing_decrease_data_uses_integer_owner_ids_and_real_standing_values()
    {
        var source = new Mailbox { Box = JsonNode.Parse("""
            {"byID":{"1":{"typeID":3001,"created":"134000000000000123","data":[[500001,900001,0,-2.5]]}}}
            """)!.AsObject() };
        var row = Assert.Single(Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications"))).Items);
        var data = Assert.IsType<PyList>(Field(row, "data"));
        var values = Assert.IsType<PyList>(Assert.Single(data.Items)).Items;
        Assert.IsType<PyInt>(values[0]);
        Assert.IsType<PyInt>(values[1]);
        Assert.Equal(0, Assert.IsType<PyReal>(values[2]).Value);
        Assert.Equal(-2.5, Assert.IsType<PyReal>(values[3]).Value);
    }

    [Fact]
    public async Task Missing_timestamp_uses_the_supplied_clock_without_modifying_the_mailbox()
    {
        var source = new Mailbox { Box = JsonNode.Parse("""{"byID":{"1":{"typeID":27}}}""")!.AsObject() };
        var row = Assert.Single(Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications"))).Items);
        Assert.Equal(MarshalCodec.Encode(new PyLong(ClockFileTime)), MarshalCodec.Encode(Field(row, "created")));
        Assert.Null(source.Box["byID"]!["1"]!["created"]);
    }

    [Fact]
    public async Task Reader_receives_cancellation_and_storage_failures_are_not_empty_success()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Mailbox();
        var call = new ServiceCall { Method = "GetAllNotifications", Args = [], Session = Session(), Cancellation = cancellation.Token };
        await Service(source).CallAsync(call);
        Assert.Equal(cancellation.Token, source.Cancellation);
        var service = new NotificationMgrService((_, _) => throw new IOException("notification storage unavailable"), () => ClockUnixMs);
        await Assert.ThrowsAsync<IOException>(async () => await service.CallAsync(call));
    }

[Fact]
    public async Task Client_used_mutators_forward_the_operation_and_ids_to_the_host()
    {
        var seen = new List<(string Operation, long? GroupId, long[] Ids)>();
        var service = new NotificationMgrService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            () => ClockUnixMs,
            mutate: (characterId, operation, groupId, ids, _, _) =>
            {
                Assert.Equal(CharacterId, characterId);
                seen.Add((operation, groupId, ids.ToArray()));
                return ValueTask.CompletedTask;
            });
        var calls = new[]
        {
            Call("MarkAllAsProcessed"),
            Call("MarkAsProcessed", null, new PyList([new PyInt(5), new PyInt(5), new PyInt(-1)])),
            Call("DeleteNotifications", null, new PyList([new PyInt(6), new PyInt(7)])),
            Call("MarkGroupAsProcessed", null, new PyInt(8)),
            Call("DeleteGroupNotifications", null, new PyInt(9)),
            Call("DeleteAllNotifications"),
        };
        foreach (var call in calls)
        {
            Assert.True(service.HasMethod(call.Method));
            Assert.IsType<PyNone>(await service.CallAsync(call));
        }
        Assert.Equal(new[]
        {
            ("MarkAllAsProcessed", (long?)null, Array.Empty<long>()),
            ("MarkAsProcessed", (long?)null, new long[] { 5 }),
            ("DeleteNotifications", (long?)null, new long[] { 6, 7 }),
            ("MarkGroupAsProcessed", (long?)8, Array.Empty<long>()),
            ("DeleteGroupNotifications", (long?)9, Array.Empty<long>()),
            ("DeleteAllNotifications", (long?)null, Array.Empty<long>()),
        }, seen);
    }

    [Fact]
    public async Task Unknown_mutators_are_not_exposed_and_interaction_log_is_a_noop()
    {
        var source = new Mailbox();
        var service = Service(source);
        foreach (var method in new[] { "MarkAsProcessed", "DeleteAllNotifications", "Unknown" })
        {
            Assert.False(service.HasMethod(method));
            Assert.IsType<PyNone>(await service.CallAsync(Call(method)));
        }
        Assert.True(service.HasMethod("LogNotificationInteraction"));
        Assert.IsType<PyNone>(await service.CallAsync(Call("LogNotificationInteraction", args: [new PyInt(55)])));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Interaction_log_accepts_positional_and_keyword_reference_ids_without_reading_the_mailbox()
    {
        var observed = new List<long>();
        var session = Session();
        var service = new NotificationMgrService(
            (_, _) => throw new InvalidOperationException("Interaction logging must not read notifications."),
            () => ClockUnixMs,
            (referenceId, caller, _) =>
            {
                Assert.Same(session, caller);
                observed.Add(referenceId);
                return ValueTask.CompletedTask;
            });

        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "LogNotificationInteraction",
            Args = [new PyInt(55)],
            Session = session,
        }));
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "LogNotificationInteraction",
            Args = [],
            Kwargs = Py.Dict(("referenceID", new PyInt(77))),
            Session = session,
        }));

        Assert.Equal(new long[] { 55, 77 }, observed);
    }

    [Fact]
    public async Task Zero_record_identifiers_fall_back_to_the_mailbox_key_and_receiver()
    {
        var source = new Mailbox { Box = JsonNode.Parse("""
            {"byID":{"7":{"notificationID":0,"receiverID":0,"typeID":27,"created":"134000000000000123"}}}
            """)!.AsObject() };
        var rows = Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications")));
        var row = Assert.Single(rows.Items);
        Assert.Equal(7, Assert.IsType<PyInt>(Field(row, "notificationID")).Value);
        Assert.Equal(CharacterId, Assert.IsType<PyInt>(Field(row, "receiverID")).Value);
    }

    [Fact]
    public async Task Null_data_inside_arrays_matches_reference_normalization()
    {
        var source = new Mailbox { Box = JsonNode.Parse("""
            {"byID":{"1":{"typeID":27,"created":"134000000000000123","data":[null,[null],{"target":null}]}}}
            """)!.AsObject() };
        var row = Assert.Single(Assert.IsType<PyList>(await Service(source).CallAsync(Call("GetAllNotifications"))).Items);
        var expected = new PyList([Py.Dict(), new PyList([Py.Dict()]), Py.Dict(("target", PyNone.Instance))]);
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(Field(row, "data")));
    }

    private static JsonObject Box() => JsonNode.Parse("""
        {"byID":{
          "1":{"typeID":27,"groupID":8,"processed":true,"created":"134000000000000123"},
          "2":{"typeID":16,"groupID":3,"processed":false,"created":"134000000000000123"},
          "3":{"typeID":28,"groupID":8,"processed":1,"created":"134000000000000123"},
          "4":{"typeID":34,"groupID":null,"created":"134000000000000123"}
        }}
        """)!.AsObject();

    private static NotificationMgrService Service(Mailbox mailbox) => new(mailbox.ReadAsync, () => ClockUnixMs);

    private static ServiceCall Call(string method, PyValue? kwargs = null, params PyValue[] args) =>
        new() { Method = method, Args = args, Kwargs = kwargs ?? PyNone.Instance, Session = Session() };

    private static ClientSession Session()
    {
        var session = new ClientSession(new Sink());
        session.Set("charid", new PyInt(CharacterId));
        return session;
    }

    private static PyValue Row(long id, long type, long sender, bool processed, long created, PyValue data) =>
        Py.KeyVal(("notificationID", new PyInt(id)), ("typeID", new PyInt(type)), ("senderID", new PyInt(sender)),
            ("receiverID", new PyInt(CharacterId)), ("processed", PyValue.Of(processed)), ("created", new PyLong(created)), ("data", data));

    private static long[] Ids(PyValue list) => Assert.IsType<PyList>(list).Items
        .Select(row => Assert.IsType<PyInt>(Field(row, "notificationID")).Value).ToArray();

    private static PyValue Field(PyValue row, string name) => Value(Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args), name);
    private static PyValue Value(PyDict dict, string name) => dict.Entries.Single(p => p.Key is PyStr s && s.Value == name).Value;

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) => throw new InvalidOperationException("A read must not send notifications.");
    }

    private sealed class Mailbox
    {
        public JsonObject? Box { get; set; }
        public long LastCharacterId { get; private set; }
        public int Reads { get; private set; }
        public CancellationToken Cancellation { get; private set; }

        public ValueTask<JsonObject?> ReadAsync(long characterId, CancellationToken cancellation)
        {
            LastCharacterId = characterId;
            Cancellation = cancellation;
            Reads++;
            return ValueTask.FromResult(Box);
        }
    }
}
