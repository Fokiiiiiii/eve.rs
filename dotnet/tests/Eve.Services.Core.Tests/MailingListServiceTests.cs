using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Core.Tests;

public sealed class MailingListServiceTests
{
    [Fact]
    public async Task The_client_can_discover_its_lists_and_read_an_empty_store()
    {
        IService service = new MailingListService();
        Assert.Equal("mailingListsMgr", service.Name);
        foreach (var method in new[] { "GetJoinedLists", "GetInfo", "GetMembers", "GetSettings", "GetWelcomeMail" })
        {
            Assert.True(service.HasMethod(method));
        }
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists"))).Entries);
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("GetInfo", 500000000)));
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetMembers", 500000000))).Entries);
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("GetSettings", 500000000)));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetWelcomeMail", 500000000))).Items);
    }

    [Fact]
    public async Task Joined_lists_are_character_scoped_and_preserve_client_keyval_fields_and_roles()
    {
        var lists = Lists();
        var before = lists.ToJsonString();
        var service = Service(lists);
        foreach (var (character, muted, op, owner) in new[]
        {
            (100L, false, false, true), (101L, false, true, false),
            (102L, true, false, false), (103L, false, false, false),
        })
        {
            var result = Wire(await service.CallAsync(Request("GetJoinedLists", characterId: character)));
            var row = Assert.Single(Assert.IsType<PyDict>(result).Entries);
            Assert.Equal(new PyInt(500000000), row.Key);
            Assert.Equal(["id", "name", "displayName", "isMuted", "isOperator", "isOwner"], Fields(row.Value));
            Assert.Equal(new PyInt(500000000), Field(row.Value, "id"));
            Assert.Equal(new PyStr("fleetops"), Field(row.Value, "name"));
            Assert.Equal(new PyStr("Fleet Ops"), Field(row.Value, "displayName"));
            Assert.Equal(PyValue.Of(muted), Field(row.Value, "isMuted"));
            Assert.Equal(PyValue.Of(op), Field(row.Value, "isOperator"));
            Assert.Equal(PyValue.Of(owner), Field(row.Value, "isOwner"));
        }
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists", characterId: 555))).Entries);
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists", 100, characterId: 555))).Entries);
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists", characterId: null))).Entries);
        Assert.Equal(before, lists.ToJsonString());
    }

    [Fact]
    public async Task External_and_deleted_list_names_remain_resolvable_for_old_mail()
    {
        var lists = Lists();
        var service = Service(lists);
        var info = Wire(await service.CallAsync(Request("GetInfo", 500000000, characterId: 999)));
        Assert.Equal(new PyStr("Fleet Ops"), Field(info, "displayName"));
        Assert.Equal(PyBool.False, Field(info, "isOwner"));
        Assert.Equal(PyBool.False, Field(info, "isOperator"));
        Assert.Equal(PyBool.False, Field(info, "isMuted"));
        var withoutCharacter = await service.CallAsync(Request("GetInfo", 500000000, characterId: null));
        Assert.Equal(new PyInt(500000000), Field(withoutCharacter, "id"));

        lists["500000000"]!["deleted"] = true;
        Assert.Equal(new PyStr("Fleet Ops"), Field(await service.CallAsync(Request("GetInfo", 500000000)), "displayName"));
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists"))).Entries);
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetMembers", 500000000))).Entries);
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("GetSettings", 500000000)));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetWelcomeMail", 500000000))).Items);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(101L)]
    public async Task Owner_and_operator_can_read_members_access_settings_and_welcome_mail(long characterId)
    {
        var service = Service(Lists());
        var members = Assert.IsType<PyDict>(Wire(await service.CallAsync(Request("GetMembers", 500000000, characterId))));
        Assert.Equal(4, members.Entries.Count);
        Assert.Equal(new PyInt(3), members.Entries.Single(row => row.Key == new PyInt(100)).Value);
        Assert.Equal(new PyInt(2), members.Entries.Single(row => row.Key == new PyInt(101)).Value);
        Assert.Equal(new PyInt(0), members.Entries.Single(row => row.Key == new PyInt(102)).Value);
        Assert.Equal(new PyInt(1), members.Entries.Single(row => row.Key == new PyInt(103)).Value);

        var settings = Wire(await service.CallAsync(Request("GetSettings", 500000000, characterId)));
        Assert.Equal(["defaultAccess", "defaultMemberAccess", "cost", "access"], Fields(settings));
        Assert.Equal(new PyInt(0), Field(settings, "defaultAccess"));
        Assert.Equal(new PyInt(1), Field(settings, "defaultMemberAccess"));
        Assert.Equal(new PyInt(2500), Field(settings, "cost"));
        var access = Assert.IsType<PyDict>(Field(settings, "access"));
        Assert.Equal(2, access.Entries.Count);
        Assert.Equal(new PyInt(1), access.Entries.Single(row => row.Key == new PyInt(800)).Value);
        Assert.Equal(new PyInt(0), access.Entries.Single(row => row.Key == new PyInt(900)).Value);

        var welcome = Assert.Single(Assert.IsType<PyList>(Wire(await service.CallAsync(Request("GetWelcomeMail", 500000000, characterId)))).Items);
        Assert.Equal(["title", "body"], Fields(welcome));
        Assert.Equal(new PyStr("Welcome"), Field(welcome, "title"));
        Assert.Equal(new PyStr("<b>Fleet briefing</b>"), Field(welcome, "body"));
    }

    [Theory]
    [InlineData(102L)]
    [InlineData(103L)]
    [InlineData(999L)]
    [InlineData(0L)]
    public async Task Management_reads_use_membership_roles_not_public_join_access(long characterId)
    {
        var lists = Lists();
        lists["500000000"]!["defaultAccess"] = 1;
        lists["500000000"]!["access"]!["999"] = 1;
        var service = Service(lists);
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetMembers", 500000000, characterId))).Entries);
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("GetSettings", 500000000, characterId)));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetWelcomeMail", 500000000, characterId))).Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(999999999L)]
    public async Task Missing_and_invalid_list_ids_have_the_reference_no_result_shapes(long? listId)
    {
        var service = Service(Lists());
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("GetInfo", listId)));
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetMembers", listId))).Entries);
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("GetSettings", listId)));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetWelcomeMail", listId))).Items);
    }

    [Fact]
    public async Task Defaults_and_bounded_welcome_text_match_the_persisted_reference()
    {
        var lists = Lists();
        var list = (JsonObject)lists["500000000"]!;
        list.Remove("defaultAccess");
        list["defaultMemberAccess"] = 99;
        list["cost"] = -10;
        list["access"] = new JsonObject();
        list["welcomeMail"] = null;
        var service = Service(lists);
        var settings = await service.CallAsync(Request("GetSettings", 500000000));
        Assert.Equal(new PyInt(1), Field(settings, "defaultAccess"));
        Assert.Equal(new PyInt(1), Field(settings, "defaultMemberAccess"));
        Assert.Equal(new PyInt(0), Field(settings, "cost"));
        Assert.Empty(Assert.IsType<PyDict>(Field(settings, "access")).Entries);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Request("GetWelcomeMail", 500000000))).Items);

        list["welcomeMail"] = new JsonObject
        {
            ["title"] = "  " + new string('t', 151) + "  ",
            ["body"] = "  " + new string('b', 8001) + "  ",
        };
        var welcome = Assert.Single(Assert.IsType<PyList>(await service.CallAsync(Request("GetWelcomeMail", 500000000))).Items);
        Assert.Equal(150, Assert.IsType<PyStr>(Field(welcome, "title")).Value.Length);
        Assert.Equal(8000, Assert.IsType<PyStr>(Field(welcome, "body")).Value.Length);
    }

    [Fact]
    public async Task Reads_refresh_the_backing_snapshot_and_do_not_mutate_it()
    {
        var lists = Lists();
        var before = lists.ToJsonString();
        var reads = 0;
        IService service = new MailingListService(_ =>
        {
            reads++;
            return ValueTask.FromResult<JsonObject?>(lists);
        });
        Assert.Single(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists", characterId: 101))).Entries);
        Assert.Equal(before, lists.ToJsonString());
        ((JsonObject)lists["500000000"]!["members"]!).Remove("101");
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetJoinedLists", characterId: 101))).Entries);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Unknown_methods_are_not_advertised_or_sent_to_the_reader()
    {
        IService service = new MailingListService(_ => throw new InvalidOperationException("Reader must not run"));
        Assert.False(service.HasMethod("Unknown"));
        Assert.Same(PyNone.Instance, await service.CallAsync(Request("Unknown")));
    }

    [Fact]
    public async Task Cancellation_and_reader_errors_are_not_disguised_as_an_empty_store()
    {
        using var source = new CancellationTokenSource();
        var calls = 0;
        IService service = new MailingListService(token =>
        {
            Assert.Equal(source.Token, token);
            calls++;
            throw new IOException("read failed");
        });
        var call = new ServiceCall
        {
            Method = "GetJoinedLists", Args = [],
            Session = new ClientSession { CharacterId = 100 }, Cancellation = source.Token,
        };
        await Assert.ThrowsAsync<IOException>(async () => await service.CallAsync(call));
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.CallAsync(call));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Empty_numeric_text_uses_the_reference_default_access_and_role()
    {
        var lists = Lists();
        lists["500000000"]!["defaultAccess"] = " ";
        lists["500000000"]!["defaultMemberAccess"] = "";
        lists["500000000"]!["members"]!["103"] = " ";
        var service = Service(lists);
        var settings = await service.CallAsync(Request("GetSettings", 500000000));
        Assert.Equal(new PyInt(1), Field(settings, "defaultAccess"));
        Assert.Equal(new PyInt(1), Field(settings, "defaultMemberAccess"));
        var info = await service.CallAsync(Request("GetInfo", 500000000, 103));
        Assert.Equal(PyBool.False, Field(info, "isMuted"));
    }

    [Fact]
    public async Task Server_write_methods_are_advertised_and_forwarded_with_session_identity()
    {
        var accessByEntity = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(800), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyInt(900), new PyInt(0)),
        ]);
        var cases = new (string Method, MailingListManagerMethod ManagerMethod, PyValue[] Arguments)[]
        {
            ("Create", MailingListManagerMethod.Create, [new PyStr("fleetops"), new PyInt(1), new PyInt(1), new PyInt(0)]),
            ("Join", MailingListManagerMethod.Join, [new PyStr("fleetops")]),
            ("Leave", MailingListManagerMethod.Leave, [new PyInt(500000000)]),
            ("Delete", MailingListManagerMethod.Delete, [new PyInt(500000000)]),
            ("KickMembers", MailingListManagerMethod.KickMembers, [new PyInt(500000000), new PyList([new PyInt(102), new PyInt(103)])]),
            ("SetEntitiesAccess", MailingListManagerMethod.SetEntitiesAccess, [new PyInt(500000000), accessByEntity]),
            ("ClearEntityAccess", MailingListManagerMethod.ClearEntityAccess, [new PyInt(500000000), new PyInt(800)]),
            ("SetMembersMuted", MailingListManagerMethod.SetMembersMuted, [new PyInt(500000000), new PyList([new PyInt(102)])]),
            ("SetMembersOperator", MailingListManagerMethod.SetMembersOperator, [new PyInt(500000000), new PyList([new PyInt(103)])]),
            ("SetMembersClear", MailingListManagerMethod.SetMembersClear, [new PyInt(500000000), new PyList([new PyInt(102)])]),
            ("SetDefaultAccess", MailingListManagerMethod.SetDefaultAccess, [new PyInt(500000000), new PyInt(0), new PyInt(1), new PyInt(0)]),
            ("SaveWelcomeMail", MailingListManagerMethod.SaveWelcomeMail, [new PyInt(500000000), new PyStr("Welcome"), new PyStr("Body")]),
            ("SendWelcomeMail", MailingListManagerMethod.SendWelcomeMail, [new PyInt(500000000), new PyStr("Welcome"), new PyStr("Body")]),
            ("ClearWelcomeMail", MailingListManagerMethod.ClearWelcomeMail, [new PyInt(500000000)]),
        };
        MailingListMutation? observed = null;
        MailingListMutationCallback callback = (mutation, _) =>
        {
            observed = mutation;
            PyValue result = mutation.Method switch
            {
                MailingListManagerMethod.KickMembers or
                MailingListManagerMethod.SetEntitiesAccess or
                MailingListManagerMethod.ClearEntityAccess or
                MailingListManagerMethod.SetMembersMuted or
                MailingListManagerMethod.SetMembersOperator or
                MailingListManagerMethod.SetMembersClear or
                MailingListManagerMethod.SetDefaultAccess or
                MailingListManagerMethod.SaveWelcomeMail or
                MailingListManagerMethod.ClearWelcomeMail => PyNone.Instance,
                _ => new PyLong(77),
            };
            return ValueTask.FromResult(result);
        };
        IService service = new MailingListService(
            _ => throw new InvalidOperationException("Writes must not read a mailing-list snapshot."),
            callback);

        foreach (var item in cases)
        {
            observed = null;
            Assert.True(service.HasMethod(item.Method));
            PyValue expectedResult = item.ManagerMethod switch
            {
                MailingListManagerMethod.KickMembers or
                MailingListManagerMethod.SetEntitiesAccess or
                MailingListManagerMethod.ClearEntityAccess or
                MailingListManagerMethod.SetMembersMuted or
                MailingListManagerMethod.SetMembersOperator or
                MailingListManagerMethod.SetMembersClear or
                MailingListManagerMethod.SetDefaultAccess or
                MailingListManagerMethod.SaveWelcomeMail or
                MailingListManagerMethod.ClearWelcomeMail => PyNone.Instance,
                _ => new PyLong(77),
            };
            Assert.Equal(expectedResult, await service.CallAsync(
                MutationRequest(item.Method, 42, item.Arguments)));

            var mutation = Assert.IsType<MailingListMutation>(observed);
            Assert.Equal(item.ManagerMethod, mutation.Method);
            Assert.Equal(new MailingListCaller(7001, 8002, 9003, 42), mutation.Caller);
            Assert.Equal(item.Arguments, mutation.PositionalArguments);
        }
    }

    [Fact]
    public async Task Mutation_callback_authorizes_current_roles_and_persists_to_the_shared_mail_store()
    {
        var mail = new JsonObject
        {
            ["mailingLists"] = Lists(),
            ["messages"] = new JsonObject(),
            ["mailboxes"] = new JsonObject(),
        };
        var readCount = 0;
        MailingListMutationCallback callback = (mutation, _) =>
        {
            var lists = (JsonObject)mail["mailingLists"]!;
            var list = (JsonObject)lists["500000000"]!;
            var members = (JsonObject)list["members"]!;
            var characterId = mutation.Caller.CharacterId.GetValueOrDefault();
            var ownerId = list["ownerID"]!.GetValue<long>();
            var role = characterId == ownerId
                ? 3L
                : members[characterId.ToString()]?.GetValue<long>() ?? -1L;
            if (role is not (2 or 3))
                return ValueTask.FromResult<PyValue>(PyNone.Instance);

            list["defaultAccess"] = Assert.IsType<PyInt>(mutation.PositionalArguments[1]).Value;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        };
        var service = new MailingListService(
            _ =>
            {
                readCount++;
                return ValueTask.FromResult<JsonObject?>(mail["mailingLists"] as JsonObject);
            },
            callback);

        async Task SetAccess(long characterId, long access) =>
            await service.CallAsync(MutationRequest(
                "SetDefaultAccess",
                characterId,
                new PyInt(500000000),
                new PyInt(access),
                new PyInt(1),
                new PyInt(0)));

        await SetAccess(102, 1);
        await SetAccess(103, 1);
        Assert.Equal(0L, ((JsonObject)mail["mailingLists"]!)["500000000"]!["defaultAccess"]!.GetValue<long>());

        await SetAccess(101, 1);
        Assert.Equal(1L, ((JsonObject)mail["mailingLists"]!)["500000000"]!["defaultAccess"]!.GetValue<long>());
        await SetAccess(100, 0);
        Assert.Equal(0L, ((JsonObject)mail["mailingLists"]!)["500000000"]!["defaultAccess"]!.GetValue<long>());

        ((JsonObject)((JsonObject)mail["mailingLists"]!)["500000000"]!["members"]!)["102"] = 2L;
        await SetAccess(102, 1);
        Assert.Equal(1L, ((JsonObject)mail["mailingLists"]!)["500000000"]!["defaultAccess"]!.GetValue<long>());
        Assert.Equal(0, readCount);

        var settings = Wire(await service.CallAsync(Request("GetSettings", 500000000, 102)));
        Assert.Equal(new PyInt(1), Field(settings, "defaultAccess"));
        Assert.Equal(1, readCount);
        Assert.IsType<JsonObject>(mail["messages"]);
        Assert.IsType<JsonObject>(mail["mailboxes"]);
    }

    [Fact]
    public async Task Writes_require_the_atomic_host_callback()
    {
        IService service = Service(Lists());
        Assert.True(service.HasMethod("Create"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CallAsync(MutationRequest(
                "Create", 100,
                new PyStr("fleetops"), new PyInt(1), new PyInt(1), new PyInt(0))));
    }

    [Fact]
    public async Task Create_uses_EveJS_defaults_and_normalizes_optional_numeric_arguments()
    {
        MailingListMutation? observed = null;
        MailingListMutationCallback callback = (mutation, _) =>
        {
            observed = mutation;
            return ValueTask.FromResult<PyValue>(new PyLong(500000123));
        };
        var service = Service(Lists(), callback);

        Assert.Equal(new PyLong(500000123), await service.CallAsync(
            MutationRequest("Create", 42, new PyStr("fleetops"))));
        Assert.Equal(new PyValue[] { new PyStr("fleetops"), new PyInt(1), new PyInt(1), new PyInt(0) },
            Assert.IsType<MailingListMutation>(observed).PositionalArguments);

        await service.CallAsync(MutationRequest("Create", 42,
            new PyStr("fleetops"), new PyStr("invalid"), PyNone.Instance, new PyInt(-4)));
        Assert.Equal(new PyValue[] { new PyStr("fleetops"), new PyInt(1), new PyInt(1), new PyInt(0) },
            Assert.IsType<MailingListMutation>(observed).PositionalArguments);
    }

    private static IService Service(
        JsonObject lists,
        MailingListMutationCallback? mutateMailingList = null) =>
        new MailingListService(
            _ => ValueTask.FromResult<JsonObject?>(lists),
            mutateMailingList);

    private static JsonObject Lists() => (JsonObject)JsonNode.Parse("""
        {
          "500000000": {
            "listID": 500000000, "name": "fleetops", "displayName": "  Fleet   Ops  ", "ownerID": 100,
            "members": { "100": 0, "101": 2, "102": 0, "103": 1 },
            "defaultAccess": 0, "defaultMemberAccess": 1, "cost": 2500.9,
            "access": { "800": 1, "900": 0 },
            "welcomeMail": { "title": " Welcome ", "body": " <b>Fleet briefing</b> ", "savedAt": "1234" }
          },
          "500000001": { "listID": 500000001, "name": "old", "displayName": "Old", "ownerID": 100, "deleted": true },
          "500000002": { "listID": 500000002, "name": "other", "displayName": "Other", "ownerID": 999 },
          "broken": null
        }
        """)!;

    private static PyValue Wire(PyValue value) => MarshalCodec.Decode(MarshalCodec.Encode(value));

    private static PyValue Field(PyValue value, string name)
    {
        var row = Assert.IsType<PyObject>(value);
        Assert.Equal("util.KeyVal", Eve.Macho.JsSemantics.StringValue(row.Name));
        var field = Assert.IsType<PyDict>(row.Args).Entries.Single(pair => Eve.Macho.JsSemantics.StringValue(pair.Key) == name).Value;
        return field is PyBytes ? new PyStr(Assert.IsType<string>(Eve.Macho.JsSemantics.StringValue(field))) : field;
    }

    private static string[] Fields(PyValue value) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Select(pair => Assert.IsType<string>(Eve.Macho.JsSemantics.StringValue(pair.Key))).ToArray();

    private static ServiceCall Request(string method, long? listId = null, long? characterId = 100) => new()
    {
        Method = method,
        Args = listId is { } id ? [new PyInt(id)] : [],
        Session = new ClientSession { CharacterId = characterId },
    };

    private static ServiceCall MutationRequest(string method, long characterId, params PyValue[] arguments) => new()
    {
        Method = method,
        Args = arguments,
        Session = new ClientSession
        {
            SessionId = 7001,
            ClientId = 8002,
            UserId = 9003,
            CharacterId = characterId,
        },
    };
}
