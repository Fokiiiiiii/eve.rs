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
    public async Task Unknown_and_writer_methods_are_not_advertised_or_sent_to_the_reader()
    {
        IService service = new MailingListService(_ => throw new InvalidOperationException("Reader must not run"));
        foreach (var method in new[]
        {
            "Create", "Join", "Leave", "Delete", "KickMembers", "SetEntitiesAccess",
            "ClearEntityAccess", "SetMembersMuted", "SetMembersOperator", "SetMembersClear",
            "SetDefaultAccess", "SaveWelcomeMail", "SendWelcomeMail", "ClearWelcomeMail",
            "Getjoinedlists", "Unknown",
        })
        {
            Assert.False(service.HasMethod(method));
            Assert.Same(PyNone.Instance, await service.CallAsync(Request(method)));
        }
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

    private static IService Service(JsonObject lists) => new MailingListService(_ => ValueTask.FromResult<JsonObject?>(lists));

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
}
