using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Eve.Host.PublicGateway;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class LocalChatGatewayTests
{
    private const string Prefix = "eve_public.chat.local.api.";
    private const string MembersRequest = Prefix + "GetMembershipListRequest";
    private const string MembersResponse = Prefix + "GetMembershipListResponse";
    private const string BroadcastRequest = Prefix + "BroadcastMessageRequest";
    private const string BroadcastResponse = Prefix + "BroadcastMessageResponse";
    private const string MessageNotice = Prefix + "MessageBroadcastNotice";

    [Fact]
    public async Task K_space_lists_present_pilots_and_delivers_escaped_messages()
    {
        var sessions = new ConnectedSessions();
        var first = Session(1001, 2001, 3001, 0);
        var second = Session(1002, 2002, 3001, 0);
        sessions.Add(first);
        sessions.Add(second);
        var notices = new NoticeHub();
        var subscriber = notices.Subscribe(9001);
        subscriber.Character = 1002;
        var gateway = new LocalChatGateway(sessions, notices).Handlers();

        var list = await gateway[MembersRequest](Request(MembersRequest, 1002), CancellationToken.None);
        Assert.Equal(200U, list.StatusCode);
        Assert.Equal(MembersResponse, list.ResponseTypeName);
        Assert.Equal(new long[] { 1001, 1002 }, MemberIds(list.Payload));

        var body = new ProtoWriter();
        body.String(1, "<b>hello</b>");
        var sent = await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, body.ToArray()), CancellationToken.None);
        Assert.Equal(200U, sent.StatusCode);
        Assert.Equal(BroadcastResponse, sent.ResponseTypeName);

        var frameText = "";
        while (!frameText.Contains("&lt;b&gt;hello&lt;/b&gt;", StringComparison.Ordinal))
        {
            var frame = await subscriber.Frames.Reader.ReadAsync();
            frameText = Encoding.UTF8.GetString(frame);
        }
        Assert.Contains(GatewayEnvelope.TypeUrlPrefix + MessageNotice, frameText);
    }

    [Fact]
    public async Task Wormhole_local_lists_only_speakers_and_leaving_removes_membership()
    {
        const long systemId = 31_000_123;
        var sessions = new ConnectedSessions();
        var first = Session(1001, 2001, systemId, 0);
        var second = Session(1002, 2002, systemId, 0);
        sessions.Add(first);
        sessions.Add(second);
        var gateway = new LocalChatGateway(sessions, new NoticeHub()).Handlers();

        var initial = await gateway[MembersRequest](Request(MembersRequest, 1002), CancellationToken.None);
        Assert.Empty(MemberIds(initial.Payload));

        var body = new ProtoWriter();
        body.String(1, "hello from the hole");
        await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, body.ToArray()), CancellationToken.None);
        var afterSpeak = await gateway[MembersRequest](Request(MembersRequest, 1002), CancellationToken.None);
        Assert.Equal(new long[] { 1001 }, MemberIds(afterSpeak.Payload));

        first.Change(("solarsystemid2", new PyLong(31_000_124)));
        var afterLeave = await gateway[MembersRequest](Request(MembersRequest, 1002), CancellationToken.None);
        Assert.Empty(MemberIds(afterLeave.Payload));
    }

    [Fact]
    public async Task Wormhole_first_message_sends_membership_only_to_the_speaker_without_join_notice()
    {
        const long systemId = 31_000_123;
        var sessions = new ConnectedSessions();
        var notices = new NoticeHub();
        var firstNotices = notices.Subscribe(9001);
        firstNotices.Character = 1001;
        var secondNotices = notices.Subscribe(9002);
        secondNotices.Character = 1002;
        var gateway = new LocalChatGateway(sessions, notices).Handlers();
        sessions.Add(Session(1001, 0, systemId, 0));
        sessions.Add(Session(1002, 0, systemId, 0));
        var body = new ProtoWriter();
        body.String(1, "hello from the hole");

        var response = await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, body.ToArray()), CancellationToken.None);
        Assert.Equal(200U, response.StatusCode);

        var firstMembership = await ReadNoticeAsync(new MemoryStream(await firstNotices.Frames.Reader.ReadAsync()));
        Assert.Equal("eve_public.chat.local.api.MembershipListNotice", firstMembership.Type);
        Assert.Equal(new long[] { 1001 }, MemberIds(firstMembership.Payload));
        var firstMessage = await ReadNoticeAsync(new MemoryStream(await firstNotices.Frames.Reader.ReadAsync()));
        var secondMessage = await ReadNoticeAsync(new MemoryStream(await secondNotices.Frames.Reader.ReadAsync()));
        Assert.Equal("eve_public.chat.local.api.MessageBroadcastNotice", firstMessage.Type);
        Assert.Equal("eve_public.chat.local.api.MessageBroadcastNotice", secondMessage.Type);
    }

    [Fact]
    public async Task Broadcast_enforces_size_and_per_session_flood_limit()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Session(1001, 2001, 30_000_001, 0));
        var gateway = new LocalChatGateway(sessions, new NoticeHub()).Handlers();
        var oversized = new ProtoWriter();
        oversized.String(1, new string('x', 1025));
        var tooLarge = await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, oversized.ToArray()), CancellationToken.None);
        Assert.Equal(400U, tooLarge.StatusCode);

        var valid = new ProtoWriter();
        valid.String(1, "hello");
        for (var i = 0; i < 3; i++)
            Assert.Equal(200U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, valid.ToArray()), CancellationToken.None)).StatusCode);
        Assert.Equal(429U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, valid.ToArray()), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Broadcast_enforces_a_shared_per_system_flood_limit()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Session(1001, 2001, 30_000_001, 0));
        sessions.Add(Session(1002, 2002, 30_000_001, 0));
        var gateway = new LocalChatGateway(sessions, new NoticeHub()).Handlers();
        var body = new ProtoWriter();
        body.String(1, "hello");

        for (var i = 0; i < 3; i++)
            Assert.Equal(200U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, body.ToArray()), CancellationToken.None)).StatusCode);
        for (var i = 0; i < 3; i++)
            Assert.Equal(200U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1002, body.ToArray()), CancellationToken.None)).StatusCode);
        Assert.Equal(429U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, body.ToArray()), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Organization_and_fleet_channels_derive_membership_from_session_authority()
    {
        var sessions = new ConnectedSessions();
        var first = Session(1001, 2001, 30_000_001, 0);
        var second = Session(1002, 2001, 30_000_001, 0);
        var outsider = Session(1003, 2002, 30_000_001, 0);
        foreach (var session in new[] { first, second })
        {
            session.Set("allianceid", new PyLong(3001));
            session.Set("fleetid", new PyLong(4001));
            session.Set("wingid", new PyLong(5001));
            session.Set("squadid", new PyLong(6001));
        }
        outsider.Set("allianceid", new PyLong(3002));
        outsider.Set("fleetid", new PyLong(4002));
        outsider.Set("wingid", new PyLong(5001));
        outsider.Set("squadid", new PyLong(6001));
        sessions.Add(first);
        sessions.Add(second);
        sessions.Add(outsider);

        var notices = new NoticeHub();
        var recipient = notices.Subscribe(9002);
        recipient.Character = 1002;
        var gateway = new LocalChatGateway(sessions, notices).Handlers();
        foreach (var kind in new ulong[] { 1, 2, 3, 4, 5 })
        {
            var filter = new ProtoWriter();
            filter.Uint(1, kind);
            var members = await gateway[MembersRequest](Request(MembersRequest, 1001, filter.ToArray()), CancellationToken.None);
            Assert.Equal(200U, members.StatusCode);
            Assert.Equal(new long[] { 1001, 1002 }, MemberIds(members.Payload));
        }

        var body = new ProtoWriter();
        body.String(1, "corporation hello");
        body.Uint(2, 1);
        var sent = await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, body.ToArray()), CancellationToken.None);
        Assert.Equal(200U, sent.StatusCode);
        var frameText = "";
        while (!frameText.Contains("corporation hello", StringComparison.Ordinal))
            frameText = Encoding.UTF8.GetString(await recipient.Frames.Reader.ReadAsync());
        Assert.Contains(GatewayEnvelope.TypeUrlPrefix + MessageNotice, frameText);

        var channelMembers = Enumerable.Range(0, 11)
            .Select(index => Session(2000 + index, 2010, 30_000_001, 0))
            .ToArray();
        foreach (var member in channelMembers)
            sessions.Add(member);
        var groupMessage = new ProtoWriter();
        groupMessage.String(1, "limited group message");
        groupMessage.Uint(2, 1);
        for (var index = 0; index < 10; index++)
            for (var message = 0; message < 3; message++)
                Assert.Equal(200U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 2000 + index, groupMessage.ToArray()), CancellationToken.None)).StatusCode);
        Assert.Equal(429U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 2010, groupMessage.ToArray()), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Membership_uses_client_chat_classifications_and_hides_invisible_roles()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Session(1001, 2001, 30_000_001, 1L << 52)); // QA / developer
        sessions.Add(Session(1002, 2001, 30_000_001, 1L << 53)); // GMH / game master
        sessions.Add(Session(1003, 2001, 30_000_001, 1L << 11)); // centurion / volunteer
        sessions.Add(Session(1004, 2001, 30_000_001, 1L << 6)); // pink chat / NPC
        sessions.Add(Session(1005, 2001, 30_000_001, 1L << 20)); // chat invisible
        var gateway = new LocalChatGateway(sessions, new NoticeHub()).Handlers();

        var members = await gateway[MembersRequest](Request(MembersRequest, 1001), CancellationToken.None);
        var records = ProtoReader.Fields(members.Payload)
            .Where(field => field.Number == 1 && field.WireType == 2)
            .Select(field => ProtoReader.Fields(field.Bytes))
            .ToDictionary(fields => (long)ProtoReader.Fields(fields.First(f => f.Number == 1).Bytes).First(f => f.Number == 1).Scalar,
                fields => (int)fields.First(f => f.Number == 7).Scalar);

        Assert.Equal(new Dictionary<long, int> { [1001] = 2, [1002] = 4, [1003] = 5, [1004] = 6 }, records);
    }

    [Fact]
    public async Task Public_gateway_local_chat_wires_two_members_in_join_message_and_leave_order()
    {
        const long firstCharacterId = 1001;
        const long secondCharacterId = 1002;
        const long corporationId = 0;
        const long systemId = 30_000_001;
        var identified = new Dictionary<long, TaskCompletionSource>();
        foreach (var characterId in new[] { firstCharacterId, secondCharacterId })
            identified[characterId] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var sessions = new ConnectedSessions();
        var notices = new NoticeHub();
        var localChat = new LocalChatGateway(sessions, notices);
        var authority = new GatewayAuthority((characterId, _) =>
        {
            if (identified.TryGetValue(characterId, out var ready)) ready.TrySetResult();
            return Task.FromResult<long?>(identified.ContainsKey(characterId) ? 1 : null);
        }, () => DateTimeOffset.UtcNow);
        await using var server = await PublicGatewayServer.StartAsync(
            IPAddress.Loopback, 0, new PublicGatewayRoutes(localChat.Handlers(), notices, authority));
        using var handler = new SocketsHttpHandler();
        using var http = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };
        var token = GatewayTokens.Issue(1, null, DateTimeOffset.UtcNow);

        async Task<(CancellationTokenSource Cancellation, HttpRequestMessage Request,
            HttpResponseMessage Response, Stream Output)> StartNoticeStream(long characterId)
        {
            var cancellation = new CancellationTokenSource();
            var requestFrame = RequestFrame("eve_public.gateway.Notices.Consume", [], characterId);
            Assert.Equal(characterId, GatewayEnvelope.DecodeRequest(requestFrame.AsMemory(5)).CharacterId);
            var request = new HttpRequestMessage(
                HttpMethod.Post, $"http://127.0.0.1:{server.Port}/eve_public.gateway.Notices/Consume")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(requestFrame),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc+proto");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("Public gateway notice response headers timed out.", error);
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpVersion.Version20, response.Version);
            var output = await response.Content.ReadAsStreamAsync(cancellation.Token);
            try
            {
                await identified[characterId].Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("Public gateway did not authenticate the notice stream character.", error);
            }
            return (cancellation, request, response, output);
        }

        var firstStream = await StartNoticeStream(firstCharacterId);
        var secondStream = await StartNoticeStream(secondCharacterId);
        try
        {
            var first = Session(firstCharacterId, corporationId, systemId, 0);
            var second = Session(secondCharacterId, corporationId, systemId, 0);
            sessions.Add(first);
            var firstInitial = await ReadNoticeAsync(firstStream.Output);
            Assert.Equal("eve_public.chat.local.api.MembershipListNotice", firstInitial.Type);
            Assert.Equal(new long[] { firstCharacterId }, MemberIds(firstInitial.Payload));

            sessions.Add(second);
            var join = await ReadNoticeAsync(firstStream.Output);
            Assert.Equal("eve_public.chat.local.api.JoinNotice", join.Type);
            Assert.Equal(new[] { 1, 2 }, ProtoReader.Fields(join.Payload).Select(field => field.Number));
            var joinedMember = ProtoReader.Fields(join.Payload).Single(field => field.Number == 1).Bytes;
            Assert.Equal(secondCharacterId, IdentifierValue(
                ProtoReader.Fields(joinedMember).Single(field => field.Number == 1).Bytes));
            var secondMembers = await ReadNoticeAsync(secondStream.Output);
            Assert.Equal("eve_public.chat.local.api.MembershipListNotice", secondMembers.Type);
            Assert.Equal(new long[] { firstCharacterId, secondCharacterId }, MemberIds(secondMembers.Payload));

            var membership = await SendGatewayRequest(
                http, server.Port, token, "eve_public.chat.local.api.GetMembershipListRequest",
                [], firstCharacterId);
            Assert.Equal("eve_public.chat.local.api.GetMembershipListResponse", membership.Type);
            Assert.Equal(new long[] { firstCharacterId, secondCharacterId }, MemberIds(membership.Payload));

            var message = new ProtoWriter();
            message.String(1, "<b>hello</b>");
            var broadcast = await SendGatewayRequest(
                http, server.Port, token, "eve_public.chat.local.api.BroadcastMessageRequest",
                message.ToArray(), firstCharacterId, emptyResponse: true);
            Assert.Equal("eve_public.chat.local.api.BroadcastMessageResponse", broadcast.Type);

            foreach (var stream in new[] { firstStream.Output, secondStream.Output })
            {
                var delivered = await ReadNoticeAsync(stream);
                Assert.Equal("eve_public.chat.local.api.MessageBroadcastNotice", delivered.Type);
                var fields = ProtoReader.Fields(delivered.Payload);
                Assert.Equal(new[] { 1, 2, 3 }, fields.Select(field => field.Number));
                Assert.Equal(firstCharacterId, IdentifierValue(fields.Single(field => field.Number == 1).Bytes));
                Assert.Equal("&lt;b&gt;hello&lt;/b&gt;", Encoding.UTF8.GetString(fields.Single(field => field.Number == 3).Bytes.Span));
            }

            sessions.Remove(second);
            var leave = await ReadNoticeAsync(firstStream.Output);
            var updatedMembers = await ReadNoticeAsync(firstStream.Output);
            Assert.Equal("eve_public.chat.local.api.LeaveNotice", leave.Type);
            Assert.Equal(new[] { 1, 2 }, ProtoReader.Fields(leave.Payload).Select(field => field.Number));
            Assert.Equal(secondCharacterId, IdentifierValue(
                ProtoReader.Fields(leave.Payload).Single(field => field.Number == 1).Bytes));
            Assert.Equal("eve_public.chat.local.api.MembershipListNotice", updatedMembers.Type);
            Assert.Equal(new long[] { firstCharacterId }, MemberIds(updatedMembers.Payload));
        }
        finally
        {
            firstStream.Cancellation.Cancel();
            secondStream.Cancellation.Cancel();
            firstStream.Output.Dispose();
            secondStream.Output.Dispose();
            firstStream.Response.Dispose();
            secondStream.Response.Dispose();
            firstStream.Request.Dispose();
            secondStream.Request.Dispose();
            firstStream.Cancellation.Dispose();
            secondStream.Cancellation.Dispose();
        }
    }

    private static async Task<(string Type, byte[] Payload)> SendGatewayRequest(
        HttpClient http, int port, string token, string typeName, byte[] payload, long characterId, bool emptyResponse = false)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{port}/eve_public.gateway.Requests/Send")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(RequestFrame(typeName, payload, characterId)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc+proto");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        var decoded = ProtoReader.Fields(await ReadGrpcFrameAsync(await response.Content.ReadAsStreamAsync()));
        Assert.Equal(200UL, decoded.Single(field => field.Number == 3).Scalar);
        var any = ProtoReader.Fields(decoded.Single(field => field.Number == 5).Bytes);
        Assert.Equal(emptyResponse ? new[] { 1 } : new[] { 1, 2 }, any.Select(field => field.Number));
        return (Encoding.UTF8.GetString(any.Single(field => field.Number == 1).Bytes.Span)
            .Replace(GatewayEnvelope.TypeUrlPrefix, "", StringComparison.Ordinal),
            emptyResponse ? [] : any.Single(field => field.Number == 2).Bytes.ToArray());
    }

    private static byte[] RequestFrame(string typeName, byte[] payload, long characterId)
    {
        var identifier = new ProtoWriter();
        identifier.Uint(1, (ulong)characterId);
        var identity = new ProtoWriter();
        identity.Message(1, identifier.ToArray());
        var context = new ProtoWriter();
        context.Message(10, identity.ToArray());
        var any = new ProtoWriter();
        any.String(1, GatewayEnvelope.TypeUrlPrefix + typeName);
        any.Bytes(2, payload);
        var request = new ProtoWriter();
        request.Bytes(2, Guid.NewGuid().ToByteArray());
        request.Message(3, any.ToArray());
        request.Message(6, context.ToArray());
        return GatewayEnvelope.Frame(request.ToArray());
    }

    private static async Task<byte[]> ReadGrpcFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        Assert.Equal(0, header[0]);
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1)));
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellationToken);
        return message;
    }

    private static async Task<(string Type, byte[] Payload)> ReadNoticeAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fields = ProtoReader.Fields(await ReadGrpcFrameAsync(stream, timeout.Token));
        var any = ProtoReader.Fields(fields.Single(field => field.Number == 5).Bytes);
        return (Encoding.UTF8.GetString(any.Single(field => field.Number == 1).Bytes.Span)
            .Replace(GatewayEnvelope.TypeUrlPrefix, "", StringComparison.Ordinal),
            any.Single(field => field.Number == 2).Bytes.ToArray());
    }

    private static long IdentifierValue(ReadOnlyMemory<byte> payload) =>
        (long)ProtoReader.Fields(payload).Single(field => field.Number == 1).Scalar;

    private static ClientSession Session(long characterId, long corporationId, long systemId, long role)
    {
        var session = new ClientSession { SessionId = characterId, ClientId = characterId };
        session.Set("charid", new PyLong(characterId));
        session.Set("corpid", new PyLong(corporationId));
        session.Set("accountRole", new PyLong(role));
        session.Set("allianceid", PyNone.Instance);
        session.Set("warfactionid", PyNone.Instance);
        session.Set("solarsystemid2", new PyLong(systemId));
        return session;
    }

    private static GatewayRequest Request(string typeName, long characterId, byte[]? payload = null) =>
        new([], GatewayEnvelope.TypeUrlPrefix + typeName, payload ?? [], characterId);

    private static long[] MemberIds(byte[] payload) => ProtoReader.Fields(payload)
        .Where(field => field.Number == 1 && field.WireType == 2)
        .Select(field => ProtoReader.Fields(field.Bytes).First(f => f.Number == 1).Bytes)
        .Select(encodedId => ProtoReader.Fields(encodedId).First(f => f.Number == 1).Scalar)
        .Select(value => (long)value)
        .ToArray();
}
