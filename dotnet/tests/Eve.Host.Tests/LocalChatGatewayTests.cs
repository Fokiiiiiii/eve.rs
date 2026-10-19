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
        for (var i = 0; i < 5; i++)
            Assert.Equal(200U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, valid.ToArray()), CancellationToken.None)).StatusCode);
        Assert.Equal(429U, (await gateway[BroadcastRequest](Request(BroadcastRequest, 1001, valid.ToArray()), CancellationToken.None)).StatusCode);
    }

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
