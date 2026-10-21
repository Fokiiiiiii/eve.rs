using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Macho;

namespace Eve.Services.Core.Tests;

public sealed class MailServiceTests
{
    [Fact]
    public async Task Headers_preserve_client_recipient_shape_and_exact_filetime()
    {
        var data = JsonNode.Parse("""
            {
              "messages": {
                "42": { "messageID":42, "senderID":90000002,
                  "toCharacterIDs":[90000001,90000003], "toListID":null,
                  "toCorpOrAllianceID":98000001, "title":"Fleet <b>notice</b>",
                  "sentDate":"134100000000000001", "body":"Persisted body" }
              },
              "mailboxes": { "90000001": { "statuses": { "42": { "statusMask":0, "labelMask":1 } } } }
            }
            """)!.AsObject();
        var service = new MailService(_ => Task.FromResult(data), (_, _, _, _) => Task.CompletedTask);

        var result = Assert.IsType<PyList>(await service.CallAsync(Request("GetMailHeaders", new PyList([new PyInt(42)]))));

        var header = Assert.Single(result.Items);
        Assert.Equal(new PyStr("util.KeyVal"), Assert.IsType<PyObject>(header).Name);
        Assert.Equal(new PyInt(42), Field(header, "messageID"));
        Assert.Equal(new PyInt(90000002), Field(header, "senderID"));
        Assert.Equal(new PyStr("90000001,90000003"), Field(header, "toCharacterIDs"));
        Assert.Equal(PyNone.Instance, Field(header, "toListID"));
        Assert.Equal(new PyInt(98000001), Field(header, "toCorpOrAllianceID"));
        Assert.Equal(new PyStr("Fleet <b>notice</b>"), Field(header, "title"));
        Assert.Equal(new PyLong(134100000000000001), Field(header, "sentDate"));
        Assert.Equal(7, Assert.IsType<PyDict>(Assert.IsType<PyObject>(header).Args).Entries.Count);
    }

[Fact]
    public async Task Headers_preserve_corporation_or_alliance_recipient_arrays()
    {
        var data = JsonNode.Parse("""
            {
              "messages": {
                "43": { "messageID":43, "senderID":90000002, "toCharacterIDs":[],
                  "toCorpOrAllianceID":[98000001,98000002], "title":"Alliance",
                  "sentDate":"134100000000000001", "body":"Body" }
              },
              "mailboxes": { "90000001": { "statuses": { "43": { "statusMask":0, "labelMask":0 } } } }
            }
            """)!.AsObject();

        var result = Assert.IsType<PyList>(await Service(data).CallAsync(
            Request("GetMailHeaders", new PyList([new PyInt(43)]))));
        var header = Assert.Single(result.Items);
        var recipients = Assert.IsType<PyList>(Field(header, "toCorpOrAllianceID"));

        Assert.Equal(new PyValue[] { new PyInt(98000001), new PyInt(98000002) }, recipients.Items);
    }

    [Fact]
    public async Task Initial_sync_returns_only_mailbox_entries_newest_first_including_trash()
    {
        var data = Mailbox();
        var before = data.ToJsonString();
        var service = Service(data);

        var result = await service.CallAsync(Request("SyncMail"));

        Assert.Equal([30L, 21L, 20L, 10L], Ids(Field(result, "newMail")));
        Assert.Empty(Assert.IsType<PyList>(Field(result, "oldMail")).Items);
        var statuses = Assert.IsType<PyList>(Field(result, "mailStatus"));
        Assert.Equal([30L, 21L, 20L, 10L], Ids(statuses));
        Assert.Equal(new PyInt(18), Field(statuses.Items[1], "statusMask"));
        Assert.Equal(new PyInt(17), Field(statuses.Items[1], "labelMask"));
        Assert.Equal(before, data.ToJsonString());
    }

    [Fact]
    public async Task Incremental_sync_partitions_headers_but_returns_every_status()
    {
        var service = Service(Mailbox());

        var result = await service.CallAsync(Request("SyncMail", new PyInt(20), new PyInt(21)));

        Assert.Equal([30L], Ids(Field(result, "newMail")));
        Assert.Equal([10L], Ids(Field(result, "oldMail")));
        Assert.Equal([30L, 21L, 20L, 10L], Ids(Field(result, "mailStatus")));
    }

    [Fact]
    public async Task Null_first_or_zero_last_requests_a_full_sync()
    {
        var service = Service(Mailbox());
        foreach (var args in new PyValue[][] { [PyNone.Instance, new PyInt(21)], [new PyInt(20), new PyInt(0)] })
        {
            var result = await service.CallAsync(Request("SyncMail", args));
            Assert.Equal([30L, 21L, 20L, 10L], Ids(Field(result, "newMail")));
            Assert.Empty(Assert.IsType<PyList>(Field(result, "oldMail")).Items);
        }
    }

    [Fact]
    public async Task Requested_headers_keep_first_request_order_deduplicate_and_enforce_mailbox_membership()
    {
        var service = Service(Mailbox());

        var result = await service.CallAsync(Request("GetMailHeaders", new PyTuple(
            [new PyInt(20), new PyInt(10), new PyInt(20), new PyInt(40), new PyInt(-1), new PyInt(99)])));

        Assert.Equal([20L, 10L], Ids(result));
        var header = Assert.IsType<PyList>(result).Items[0];
        Assert.Equal(PyNone.Instance, Field(header, "toCharacterIDs"));
        Assert.Equal(new PyInt(1234), Field(header, "toListID"));
        Assert.Equal(PyNone.Instance, Field(header, "toCorpOrAllianceID"));
    }

    [Fact]
    public async Task Body_is_zlib_compressed_utf8_preserving_markup_unicode_and_empty_content()
    {
        var data = Mailbox();
        var expected = "<b>出撃 🚀</b><br><url=showinfo:587>Rifter</url>" + (char)0;
        data["messages"]!["20"]!["body"] = expected;
        var service = Service(data);

        var body = await service.CallAsync(Request("GetBody", new PyInt(20), PyBool.False));
        Assert.Equal(expected, Inflate(body));
        var encoded = MarshalCodec.Encode(body);
        Assert.Equal(expected, Inflate(MarshalCodec.Decode(encoded)));
        Assert.Equal("", Inflate(await service.CallAsync(Request("GetBody", new PyInt(30), new PyInt(0)))));
    }

    [Fact]
    public async Task Body_read_flag_waits_for_persistence_and_forwards_the_requesting_session()
    {
        var data = Mailbox();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = Request("GetBody", new PyInt(20), PyBool.True);
        var updated = false;
        var service = new MailService(_ => Task.FromResult(data), async (characterId, messageId, session, ct) =>
        {
            Assert.Equal(90000001, characterId);
            Assert.Equal(20, messageId);
            Assert.Same(call.Session, session);
            Assert.Equal(call.Cancellation, ct);
            await gate.Task;
            data["mailboxes"]!["90000001"]!["statuses"]!["20"]!["statusMask"] = 1;
            updated = true;
        });

        var response = service.CallAsync(call).AsTask();
        Assert.False(response.IsCompleted);
        gate.SetResult();

        Assert.Equal("Saved body", Inflate(await response));
        Assert.True(updated);
        var sync = await service.CallAsync(Request("SyncMail"));
        var status = Assert.IsType<PyList>(Field(sync, "mailStatus")).Items.Single(item => Field(item, "messageID") == new PyInt(20));
        Assert.Equal(new PyInt(1), Field(status, "statusMask"));
    }

    [Fact]
    public async Task Mark_as_read_forwards_each_distinct_mailbox_message_to_durable_host_callback()
    {
        var call = Request("MarkAsRead", new PyList([new PyInt(20), new PyInt(21), new PyInt(20)]));
        var marked = new List<long>();
        var service = new MailService(_ => Task.FromResult(Mailbox()), async (characterId, messageId, session, ct) =>
        {
            Assert.Equal(90000001, characterId);
            Assert.Same(call.Session, session);
            Assert.Equal(call.Cancellation, ct);
            marked.Add(messageId);
            await Task.Yield();
        });

        Assert.True(service.HasMethod("MarkAsRead"));
        Assert.Same(PyNone.Instance, await service.CallAsync(call));
        Assert.Equal([20L, 21L], marked);
    }

    [Fact]
    public async Task Failed_read_status_persistence_does_not_report_successful_body_retrieval()
    {
        var service = new MailService(_ => Task.FromResult(Mailbox()),
            (_, _, _, _) => Task.FromException(new IOException("Persistence failed")));

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            service.CallAsync(Request("GetBody", new PyInt(20), new PyInt(1))).AsTask());

        Assert.Equal("Persistence failed", failure.Message);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Missing_and_other_character_bodies_return_none_without_marking_read(long messageId)
    {
        var service = Service(Mailbox());

        Assert.Equal(PyNone.Instance, await service.CallAsync(Request("GetBody", new PyInt(messageId), PyBool.True)));
    }

    [Fact]
    public async Task Labels_return_only_persisted_custom_labels_keyed_by_numeric_mask()
    {
        var data = Mailbox();
        data["mailboxes"]!["90000001"]!["labels"] = JsonNode.Parse("""
            { "16": { "labelID":999, "name":"作戦", "color":7 }, "32": { "name":"Trade", "color":2 } }
            """);
        var service = Service(data);

        var result = Assert.IsType<PyDict>(await service.CallAsync(Request("GetLabels")));

        Assert.Equal(2, result.Entries.Count);
        var label = result.Entries.Single(pair => pair.Key == new PyInt(16)).Value;
        Assert.Equal(new PyInt(16), Field(label, "labelID"));
        Assert.Equal(new PyStr("作戦"), Field(label, "name"));
        Assert.Equal(new PyInt(7), Field(label, "color"));
    }

    [Fact]
    public async Task An_empty_persisted_table_has_empty_shapes_and_never_creates_mail()
    {
        var data = new JsonObject();
        var service = Service(data);

        var mailbox = await service.CallAsync(Request("SyncMail"));
        Assert.Empty(Ids(Field(mailbox, "newMail")));
        Assert.Empty(Ids(Field(mailbox, "oldMail")));
        Assert.Empty(Ids(Field(mailbox, "mailStatus")));
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetLabels"))).Entries);
        Assert.Empty(Ids(await service.CallAsync(Request("GetMailHeaders", new PyList([new PyInt(20)])))));
        Assert.Equal(PyNone.Instance, await service.CallAsync(Request("GetBody", new PyInt(20), PyBool.True)));
        Assert.Empty(data);
    }

    [Fact]
    public async Task Unselected_character_and_unknown_methods_do_not_access_storage()
    {
        var service = new MailService(_ => throw new InvalidOperationException("Unexpected table access"),
            (_, _, _, _) => throw new InvalidOperationException("Unexpected update"));
        var call = Request("SyncMail");
        call.Session.Set("charid", PyNone.Instance);

        var result = await service.CallAsync(call);
        Assert.Empty(Ids(Field(result, "newMail")));

        var sendCall = Request("SendMail");
        sendCall.Session.Set("charid", PyNone.Instance);
        Assert.Equal(PyNone.Instance, await service.CallAsync(sendCall));
        Assert.Equal(PyNone.Instance, await service.CallAsync(Request("UnlistedMethod")));
        Assert.Equal("mailMgr", service.Name);
        Assert.All(new[] { "SyncMail", "GetMailHeaders", "GetBody", "GetLabels" }, method => Assert.True(service.HasMethod(method)));
        Assert.True(service.HasMethod("SendMail"));
        Assert.False(service.HasMethod("UnlistedMethod"));
    }

    [Fact]
    public async Task Each_request_uses_the_current_snapshot_and_propagates_cancellation()
    {
        var data = Mailbox();
        var service = Service(data);
        var first = await service.CallAsync(Request("SyncMail"));
        Assert.Equal([30L, 21L, 20L, 10L], Ids(Field(first, "newMail")));
        data["mailboxes"]!["90000001"]!["statuses"]!.AsObject().Remove("20");
        var second = await service.CallAsync(Request("SyncMail"));
        Assert.Equal([30L, 21L, 10L], Ids(Field(second, "newMail")));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var call = Request("SyncMail");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CallAsync(new ServiceCall
        {
            Method = call.Method, Args = call.Args, Session = call.Session, Cancellation = canceled.Token,
        }).AsTask());
    }

    [Fact]
    public async Task Already_read_body_does_not_repeat_the_status_write()
    {
        var service = Service(Mailbox());
        Assert.Equal("", Inflate(await service.CallAsync(Request("GetBody", new PyInt(30), PyBool.True))));
    }

    [Fact]
    public async Task A_mailbox_entry_deleted_during_the_status_write_is_not_returned()
    {
        var data = Mailbox();
        var service = new MailService(_ => Task.FromResult(data), (_, _, _, _) =>
        {
            data["mailboxes"]!["90000001"]!["statuses"]!.AsObject().Remove("20");
            return Task.CompletedTask;
        });

        Assert.Equal(PyNone.Instance, await service.CallAsync(Request("GetBody", new PyInt(20), PyBool.True)));
    }

    [Fact]
    public async Task Chat_gm_poke_rpc_is_exposed_and_returns_none_without_its_server_mail_backend()
    {
        var service = new MailService(
            _ => throw new InvalidOperationException("A GM poke must not read a player mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("A GM poke must not mark mail read."));
        var call = new ServiceCall
        {
            Method = "PokePlayerAboutChatMsgGm",
            Args = [new PyInt(90000003), new PyStr("help-channel")],
            Session = new ClientSession(),
        };

        Assert.True(service.HasMethod(call.Method));
        Assert.Same(PyNone.Instance, await service.CallAsync(call));
    }

    [Fact]
    public async Task Chat_gm_poke_requires_gml_before_forwarding_the_reference_mail_payload()
    {
        GmChatPokeMailRequest? sent = null;
        ClientSession? sender = null;
        var service = new MailService(
            _ => throw new InvalidOperationException("A GM poke must not read a player mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("A GM poke must not mark mail read."),
            pokePlayerAboutChatMsgGm: (request, session, _) =>
            {
                sent = request;
                sender = session;
                return Task.FromResult<long?>(700);
            });
        var call = new ServiceCall
        {
            Method = "PokePlayerAboutChatMsgGm",
            Args = [new PyInt(90000003), new PyStr("help-channel")],
            Session = new ClientSession(),
        };

        Assert.Same(PyNone.Instance, await service.CallAsync(call));
        Assert.Null(sent);
        call.Session.Set("role", new PyLong(Eve.Services.GameMasterRoles.Gml));
        Assert.Same(PyNone.Instance, await service.CallAsync(call));
        Assert.Null(sent);
        call.Session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Gml));

        Assert.Equal(new PyLong(700), await service.CallAsync(call));
        Assert.NotNull(sent);
        Assert.Same(call.Session, sender);
        Assert.Equal(90000003, sent.CharacterId);
        Assert.Equal(140000004, sent.SenderCharacterId);
        Assert.Equal("help-channel", sent.ChannelName);
        Assert.Equal("GM chat request", sent.Title);
        Assert.Equal("A GM would like to speak with you.<br><br>Requested channel: help-channel", sent.Body);
        Assert.False(sent.SaveSenderCopy);

        var previous = sent;
        var oversized = new ServiceCall
        {
            Method = "PokePlayerAboutChatMsgGm",
            Args = [new PyInt(90000003), new PyStr(new string('x', 257))],
            Session = call.Session,
        };
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(oversized).AsTask());
        Assert.Equal("MAIL_CHANNEL_LIMIT_EXCEEDED", MailInfo(refusal));
        Assert.Same(previous, sent);
    }

    private static MailService Service(JsonObject data) => new(_ => Task.FromResult(data),
        (_, _, _, _) => throw new InvalidOperationException("This read must not update state"));

    private static JsonObject Mailbox() => JsonNode.Parse("""
        {
          "messages": {
            "10": { "messageID":10, "senderID":90000002, "title":"Old", "sentDate":"100", "body":"Old body" },
            "20": { "messageID":20, "senderID":90000002, "title":"Middle", "sentDate":"200",
                    "toCharacterIDs":[], "toListID":1234, "toCorpOrAllianceID":null, "body":"Saved body" },
            "21": { "messageID":21, "senderID":90000002, "title":"Trashed", "sentDate":"300", "body":"Trash body" },
            "30": { "messageID":30, "senderID":90000002, "title":"Newest", "sentDate":"300", "body":"" },
            "40": { "messageID":40, "senderID":90000002, "title":"Other character", "sentDate":"400", "body":"Private" }
          },
          "mailboxes": {
            "90000001": { "statuses": {
              "10": { "statusMask":0, "labelMask":1 }, "20": { "statusMask":0, "labelMask":0 },
              "21": { "statusMask":18, "labelMask":17 }, "30": { "statusMask":1, "labelMask":2 },
              "99": { "statusMask":0, "labelMask":1 }
            }, "labels": {} },
            "90000003": { "statuses": { "40": { "statusMask":0, "labelMask":1 } } }
          }
        }
        """)!.AsObject();

    private static long[] Ids(PyValue value) => Assert.IsType<PyList>(value).Items
        .Select(item => Assert.IsType<PyInt>(Field(item, "messageID")).Value).ToArray();

    private static string Inflate(PyValue value)
    {
        using var input = new MemoryStream(Assert.IsType<PyBuffer>(value).Value.ToArray());
        using var stream = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Sync_returns_only_persisted_mailbox_entries_in_exact_date_and_id_order()
    {
        var state = State();
        var before = state.ToJsonString();
        var result = await Service(state).CallAsync(Request("SyncMail"));

        Assert.Equal(new PyStr("util.KeyVal"), Assert.IsType<PyObject>(result).Name);
        Assert.Equal(["newMail", "oldMail", "mailStatus"], Keys(result));
        Assert.Equal([20L, 11L, 10L, 5L], Ids(Field(result, "newMail")));
        Assert.Empty(Assert.IsType<PyList>(Field(result, "oldMail")).Items);
        var statuses = Assert.IsType<PyList>(Field(result, "mailStatus"));
        Assert.Equal([20L, 11L, 10L, 5L], Ids(statuses));
        Assert.Equal(["messageID", "statusMask", "labelMask"], Keys(statuses.Items[2]));
        Assert.Equal(new PyInt(8), Field(statuses.Items[2], "statusMask"));
        Assert.Equal(new PyInt(4), Field(statuses.Items[2], "labelMask"));
        Assert.Equal(before, state.ToJsonString());
    }

    [Fact]
    public async Task Sync_splits_new_and_old_headers_but_includes_cached_and_trashed_statuses()
    {
        var result = await Service(State()).CallAsync(Request("SyncMail", new PyInt(10), new PyInt(11)));
        Assert.Equal([20L], Ids(Field(result, "newMail")));
        Assert.Equal([5L], Ids(Field(result, "oldMail")));
        Assert.Equal([20L, 11L, 10L, 5L], Ids(Field(result, "mailStatus")));
    }

    [Fact]
    public async Task Sync_without_a_last_id_returns_all_headers_even_with_a_first_id()
    {
        var service = Service(State());
        foreach (var last in new PyValue[] { PyNone.Instance, new PyInt(0), new PyInt(-1) })
        {
            var result = await service.CallAsync(Request("SyncMail", new PyInt(10), last));
            Assert.Equal([20L, 11L, 10L, 5L], Ids(Field(result, "newMail")));
            Assert.Empty(Ids(Field(result, "oldMail")));
        }
    }

    [Fact]
    public async Task Requested_headers_preserve_order_deduplicate_and_require_a_mailbox_status()
    {
        var service = Service(State());
        var result = await service.CallAsync(Request("GetMailHeaders",
            new PyTuple([new PyInt(20), new PyInt(10), new PyInt(20), new PyInt(99),
                new PyInt(50), new PyInt(999), new PyReal(5.9), new PyInt(-1)])));

        Assert.Equal([20L, 10L, 5L], Ids(result));
        var corporationMail = Assert.IsType<PyList>(result).Items[1];
        Assert.Equal(PyNone.Instance, Field(corporationMail, "toCharacterIDs"));
        Assert.Equal(PyNone.Instance, Field(corporationMail, "toListID"));
        Assert.Equal(new PyInt(98000001), Field(corporationMail, "toCorpOrAllianceID"));

        var listMail = Assert.Single(Assert.IsType<PyList>(await service.CallAsync(
            Request("GetMailHeaders", new PyList([new PyInt(11)])))).Items);
        Assert.Equal(new PyInt(500000001), Field(listMail, "toListID"));
        Assert.Equal(PyNone.Instance, Field(listMail, "toCorpOrAllianceID"));
        Assert.Empty(Ids(await service.CallAsync(Request("GetMailHeaders", new PyInt(20)))));
    }

    [Fact]
    public async Task Body_is_zlib_compressed_utf8_with_persisted_markup_and_whitespace_intact()
    {
        const string body = "  <b>募集🚀</b>\r\n<url=showinfo:34>鉱石</url>  ";
        var state = State();
        state["messages"]!["20"]!["body"] = body;
        var service = new MailService(_ => Task.FromResult(state),
            (_, _, _, _) => throw new InvalidOperationException("Read-only body lookup must not save."));
        var response = await service.CallAsync(Request("GetBody", new PyInt(20), PyBool.False));

        Assert.Equal(body, Uncompress(response));
        Assert.Equal(6, state["mailboxes"]!["90000001"]!["statuses"]!["20"]!["statusMask"]!.GetValue<int>());
    }

    [Fact]
    public async Task Empty_persisted_body_is_a_valid_compressed_stream()
    {
        var state = State();
        state["messages"]!["20"]!["body"] = "";
        Assert.Equal("", Uncompress(await Service(state).CallAsync(Request("GetBody", new PyInt(20)))));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(50)]
    [InlineData(999)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Missing_or_foreign_body_is_none_and_does_not_mark_read(long id)
    {
        var service = new MailService(_ => Task.FromResult(State()),
            (_, _, _, _) => throw new InvalidOperationException("Invisible message must not be marked."));
        Assert.IsType<PyNone>(await service.CallAsync(Request("GetBody", new PyInt(id), PyBool.True)));
    }

    [Fact]
    public async Task Body_read_waits_for_persistence_and_later_sync_observes_only_the_read_bit_change()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"eve-mail-{Guid.NewGuid():N}.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        ClientSession? origin = null;
        async Task<JsonObject> Read(CancellationToken ct) =>
            JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
        try
        {
            await File.WriteAllTextAsync(path, State().ToJsonString());
            var service = new MailService(Read, async (characterId, messageId, session, ct) =>
            {
                Assert.Equal(90000001, characterId);
                Assert.Equal(20, messageId);
                origin = session;
                entered.SetResult();
                await commit.Task.WaitAsync(ct);
                var data = await Read(ct);
                var status = data["mailboxes"]!["90000001"]!["statuses"]!["20"]!;
                status["statusMask"] = status["statusMask"]!.GetValue<int>() | 1;
                await File.WriteAllTextAsync(path, data.ToJsonString(), ct);
                writes++;
            });
            var call = Request("GetBody", new PyInt(20), PyBool.True);
            var pending = service.CallAsync(call).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Equal(6, (await Read(default))["mailboxes"]!["90000001"]!["statuses"]!["20"]!["statusMask"]!.GetValue<int>());
            commit.SetResult();
            Assert.Equal("Stored body", Uncompress(await pending));
            Assert.Same(call.Session, origin);

            // A fresh service instance reads the file, not an in-memory receipt or body cache.
            var sync = await new MailService(Read, (_, _, _, _) => Task.CompletedTask).CallAsync(Request("SyncMail"));
            var row = Assert.IsType<PyList>(Field(sync, "mailStatus")).Items[0];
            Assert.Equal(new PyInt(7), Field(row, "statusMask"));
            Assert.Equal(new PyInt(18), Field(row, "labelMask"));
            Assert.Equal("Stored body", Uncompress(await service.CallAsync(Request("GetBody", new PyInt(20), PyBool.True))));
            Assert.Equal(1, writes);
        }
        finally
        {
            commit.TrySetResult();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Failed_read_bit_persistence_does_not_return_a_successful_body()
    {
        var failure = new IOException("mail persistence unavailable");
        var service = new MailService(_ => Task.FromResult(State()), (_, _, _, _) => Task.FromException(failure));
        var observed = await Assert.ThrowsAsync<IOException>(() =>
            service.CallAsync(Request("GetBody", new PyInt(20), PyBool.True)).AsTask());
        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task Concurrent_mailbox_removal_during_mark_read_does_not_return_a_stale_body()
    {
        var state = State();
        var service = new MailService(_ => Task.FromResult(state), (_, _, _, _) =>
        {
            state["mailboxes"]!["90000001"]!["statuses"]!.AsObject().Remove("20");
            return Task.CompletedTask;
        });
        Assert.IsType<PyNone>(await service.CallAsync(Request("GetBody", new PyInt(20), PyBool.True)));
    }

    [Fact]
    public async Task Labels_are_integer_keyed_keyvals_containing_only_saved_labels()
    {
        var result = Assert.IsType<PyDict>(await Service(State()).CallAsync(Request("GetLabels")));
        Assert.Equal([16L, 32L], result.Entries.Select(pair => Assert.IsType<PyInt>(pair.Key).Value));
        var label = result.Entries[0].Value;
        Assert.Equal(["labelID", "name", "color"], Keys(label));
        Assert.Equal(new PyInt(16), Field(label, "labelID"));
        Assert.Equal(new PyStr("Fleet"), Field(label, "name"));
        Assert.Equal(new PyInt(3), Field(label, "color"));
    }

    [Fact]
    public async Task Empty_persisted_mail_and_absent_session_character_produce_empty_read_shapes()
    {
        var empty = new JsonObject();
        var service = Service(empty);
        var sync = await service.CallAsync(Request("SyncMail"));
        Assert.All(new[] { "newMail", "oldMail", "mailStatus" }, key => Assert.Empty(Ids(Field(sync, key))));
        Assert.Empty(Ids(await service.CallAsync(Request("GetMailHeaders", new PyList([new PyInt(42)])))));
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Request("GetLabels"))).Entries);
        Assert.IsType<PyNone>(await service.CallAsync(Request("GetBody", new PyInt(42))));
        Assert.Empty(empty);

        var unauthenticated = new MailService(_ => throw new InvalidOperationException("No character."),
            (_, _, _, _) => throw new InvalidOperationException("No character."));
        var request = new ServiceCall { Method = "SyncMail", Args = [], Session = new ClientSession() };
        Assert.Empty(Ids(Field(await unauthenticated.CallAsync(request), "newMail")));
    }

[Fact]
    public async Task SendMail_refuses_recipient_subject_and_body_over_client_limits()
    {
        var sendCount = 0;
        var service = new MailService(
            _ => throw new InvalidOperationException("SendMail must not read the mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("SendMail must not mark a message read."),
            (_, _, _) =>
            {
                sendCount++;
                return Task.FromResult<long?>(701);
            });
        var recipients = new PyList(Enumerable.Range(1, 51).Select(id => (PyValue)new PyInt(id)).ToArray());

        var recipientRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Request("SendMail", recipients, PyNone.Instance, PyNone.Instance, new PyStr("Title"), new PyStr("Body"))).AsTask());
        var subjectRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Request("SendMail", new PyList([]), PyNone.Instance, PyNone.Instance, new PyStr(new string('x', 151)), new PyStr("Body"))).AsTask());
        var bodyRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Request("SendMail", new PyList([]), PyNone.Instance, PyNone.Instance, new PyStr("Title"), new PyStr(new string('x', 8001)))).AsTask());

        Assert.Equal("MAIL_RECIPIENT_LIMIT_EXCEEDED", MailInfo(recipientRefusal));
        Assert.Equal("MAIL_SUBJECT_LIMIT_EXCEEDED", MailInfo(subjectRefusal));
        Assert.Equal("MAIL_BODY_LIMIT_EXCEEDED", MailInfo(bodyRefusal));
        Assert.Equal(0, sendCount);
    }

[Fact]
    public async Task Mail_header_and_mark_read_batches_refuse_more_than_500_ids()
    {
        var service = Service(new JsonObject());
        var ids = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id)).ToArray());

        var headersRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Request("GetMailHeaders", ids)).AsTask());
        var markRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Request("MarkAsRead", ids)).AsTask());

        Assert.Equal("MAIL_BULK_LIMIT_EXCEEDED", MailInfo(headersRefusal));
        Assert.Equal("MAIL_BULK_LIMIT_EXCEEDED", MailInfo(markRefusal));
    }

[Fact]
    public async Task SendMail_decodes_UTF8_client_byte_strings_for_title_and_body()
    {
        MailSendRequest? sent = null;
        var service = new MailService(
            _ => throw new InvalidOperationException("SendMail must not read the mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("SendMail must not mark a message read."),
            (request, _, _) =>
            {
                sent = request;
                return Task.FromResult<long?>(705);
            });
        var request = Request("SendMail",
            new PyList([new PyInt(90000002)]), PyNone.Instance, PyNone.Instance,
            new PyBytes(Encoding.UTF8.GetBytes("Náme")), new PyBytes(Encoding.UTF8.GetBytes("Body — ✓")),
            new PyInt(0), new PyInt(0));

        Assert.Equal(new PyLong(705), await service.CallAsync(request));
        Assert.Equal("Náme", sent?.Title);
        Assert.Equal("Body — ✓", sent?.Body);
    }

[Fact]
    public async Task SendMail_forwards_each_client_recipient_route_and_reply_forward_reference()
    {
        var sent = new List<MailSendRequest>();
        var service = new MailService(
            _ => throw new InvalidOperationException("SendMail must not read the mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("SendMail must not mark a message read."),
            (request, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                sent.Add(request);
                return Task.FromResult<long?>((long)(700 + sent.Count));
            });

        var direct = Request("SendMail",
            new PyList([new PyInt(90000003), new PyInt(90000002), new PyInt(90000003)]),
            PyNone.Instance, PyNone.Instance, new PyStr("Direct"), new PyStr("<b>body</b>"),
            new PyInt(5), new PyInt(6));
        var mailingList = Request("SendMail",
            new PyList([]), new PyInt(500000001), new PyList([]),
            new PyStr("List"), new PyStr("List body"), new PyInt(0), new PyInt(0));
        var corporation = Request("SendMail",
            new PyList([]), PyNone.Instance, new PyList([new PyInt(98000001)]),
            new PyStr("Corp"), new PyStr("Corp body"), new PyInt(0), new PyInt(0));

        Assert.Equal(new PyLong(701), await service.CallAsync(direct));
        Assert.Equal(new PyLong(702), await service.CallAsync(mailingList));
        Assert.Equal(new PyLong(703), await service.CallAsync(corporation));

        Assert.Equal(90000001, sent[0].SenderCharacterId);
        Assert.Equal(new long[] { 90000003, 90000002 }, sent[0].CharacterRecipientIds);
        Assert.Null(sent[0].MailingListId);
        Assert.Null(sent[0].CorporationOrAllianceRecipientIds);
        Assert.Equal("Direct", sent[0].Title);
        Assert.Equal("<b>body</b>", sent[0].Body);
        Assert.Equal(5, sent[0].ReplyToMessageId);
        Assert.Equal(6, sent[0].ForwardedFromMessageId);
        Assert.Equal(500000001, sent[1].MailingListId);
        Assert.Equal(new long[] { 98000001 }, sent[2].CorporationOrAllianceRecipientIds);
    }

    [Fact]
    public async Task SendMail_waits_for_durable_writer_before_returning_message_id()
    {
        var persisted = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MailService(
            _ => throw new InvalidOperationException("SendMail must not read the mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("SendMail must not mark a message read."),
            (_, _, _) => persisted.Task);

        var response = service.CallAsync(Request("SendMail",
            new PyList([]), PyNone.Instance, new PyList([]),
            new PyStr("Subject"), new PyStr("Body"),
            new PyInt(0), new PyInt(0))).AsTask();

        Assert.False(response.IsCompleted);
        persisted.SetResult(987654321);
        Assert.Equal(new PyLong(987654321), await response);
    }

    [Fact]
    public async Task SendMail_returns_none_when_writer_declines_and_propagates_persistence_errors()
    {
        var declined = new MailService(
            _ => throw new InvalidOperationException("SendMail must not read the mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("SendMail must not mark a message read."),
            (_, _, _) => Task.FromResult<long?>(null));
        Assert.IsType<PyNone>(await declined.CallAsync(Request("SendMail",
            new PyList([]), PyNone.Instance, new PyList([]),
            new PyStr("Subject"), new PyStr("Body"), new PyInt(0), new PyInt(0))));

        var failure = new IOException("mail persistence failed");
        var failing = new MailService(
            _ => throw new InvalidOperationException("SendMail must not read the mailbox."),
            (_, _, _, _) => throw new InvalidOperationException("SendMail must not mark a message read."),
            (_, _, _) => Task.FromException<long?>(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failing.CallAsync(
            Request("SendMail", new PyList([]), PyNone.Instance, new PyList([]),
                new PyStr("Subject"), new PyStr("Body"), new PyInt(0), new PyInt(0))).AsTask()));
    }

    [Fact]
    public async Task Service_exposes_read_contracts_and_passes_cancellation_and_storage_errors_through()
    {
        var service = Service(State());
        Assert.Equal("mailMgr", service.Name);
        Assert.All(new[] { "SyncMail", "GetMailHeaders", "GetBody", "GetLabels", "SendMail" },
            name => Assert.True(service.HasMethod(name)));
        var failure = new IOException("read unavailable");
        var failing = new MailService(_ => Task.FromException<JsonObject>(failure), (_, _, _, _) => Task.CompletedTask);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failing.CallAsync(Request("SyncMail")).AsTask()));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CallAsync(new ServiceCall
        {
            Method = "SyncMail", Args = [], Session = Request("SyncMail").Session,
            Cancellation = cancelled.Token,
        }).AsTask());
    }

    private static string[] Keys(PyValue row) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args)
        .Entries.Select(pair => Assert.IsType<PyStr>(pair.Key).Value).ToArray();

    private static string Uncompress(PyValue value)
    {
        var bytes = Assert.IsType<PyBuffer>(value).Value.ToArray();
        using var input = new MemoryStream(bytes);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonObject State() => JsonNode.Parse("""
        {
          "messages": {
            "5": { "messageID":5, "senderID":90000002, "toCharacterIDs":[90000001],
              "title":"Old", "sentDate":"134100000000000001", "body":"Old body" },
            "10": { "messageID":10, "senderID":90000002, "toCharacterIDs":[],
              "toCorpOrAllianceID":98000001, "title":"Corp", "sentDate":"134100000000000002", "body":"Corp body" },
            "11": { "messageID":11, "senderID":90000002, "toCharacterIDs":[],
              "toListID":500000001, "title":"List", "sentDate":"134100000000000002", "body":"List body" },
            "20": { "messageID":20, "senderID":90000001, "toCharacterIDs":[90000002],
              "title":"Sent", "sentDate":"134100000000000003", "body":"Stored body" },
            "50": { "messageID":50, "senderID":90000002, "toCharacterIDs":[90000001],
              "title":"Deleted status", "sentDate":"134100000000000004", "body":"Deleted body" },
            "99": { "messageID":99, "senderID":90000003, "toCharacterIDs":[90000004],
              "title":"Foreign", "sentDate":"134100000000000005", "body":"Foreign body" }
          },
          "mailboxes": {
            "90000001": {
              "statuses": {
                "5": { "statusMask":0, "labelMask":1 },
                "10": { "statusMask":8, "labelMask":4 },
                "11": { "statusMask":1, "labelMask":0 },
                "20": { "statusMask":6, "labelMask":18 },
                "999": { "statusMask":0, "labelMask":1 }
              },
              "labels": {
                "32": { "name":"Later", "color":4 },
                "16": { "name":"Fleet", "color":3 }
              }
            },
            "90000004": { "statuses": { "99": { "statusMask":0, "labelMask":1 } } }
          }
        }
        """)!.AsObject();

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(pair => pair.Key == new PyStr(name)).Value;

    private static string MailInfo(MachoWrappedException error)
    {
        var payload = Assert.IsType<PyObjectEx>(error.Error.Payload);
        var header = Assert.IsType<PyTuple>(payload.Header);
        var values = Assert.IsType<PyTuple>(header.Items[1]);
        var arguments = Assert.IsType<PyDict>(values.Items[1]);
        var info = arguments.Entries.Single(entry => entry.Key is PyStr key && key.Value == "info").Value;
        return Assert.IsType<PyStr>(info).Value;
    }

    private static ServiceCall Request(string method, params PyValue[] args)
    {
        var session = new ClientSession(new Sink());
        session.Set("charid", new PyInt(90000001));
        return new ServiceCall { Method = method, Args = args, Session = session };
    }

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }
}
