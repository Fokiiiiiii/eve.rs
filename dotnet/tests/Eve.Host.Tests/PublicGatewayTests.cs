using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using Eve.Characters;
using Eve.Host.PublicGateway;
using Eve.Skills;

namespace Eve.Host.Tests;

/// <summary>
/// The public protobuf gateway through the Host composition: its skill plan handlers answer over real HTTP/2 (h2c) on the listener,
/// with the envelopes the V24.01 client sends and reads (eve-logic skills/skillplan/grpc/message_bus/requestsMessenger.py: status 200 is
/// success, Requests/Send carries eve_public.Request frames). The plans are kept in PostgreSQL and survive a restart of the Host.
/// </summary>
[Collection("host-composition")]
public sealed class PublicGatewayTests
{
    private const string Plan = "eve_public.character.skill.plan.";
    private const string Milestone = "eve_public.character.skill.plan.milestone.";
    private const int Gunnery = 3300;

    // Short-lived login tokens for the two accounts used by the gateway ownership checks.
    private static string UserToken => GatewayTokens.Issue(1, null, DateTimeOffset.UtcNow);
    private static string OtherUserToken => GatewayTokens.Issue(2, null, DateTimeOffset.UtcNow);

    [Fact]
    public void User_tokens_are_signed_account_scoped_and_expire()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var token = GatewayTokens.Issue(71, 90000001, now);
        var authority = new GatewayAuthority((_, _) => Task.FromResult<long?>(null), () => now);

        Assert.Equal(71, authority.AccountOf(token));
        var signatureStart = token.LastIndexOf('.') + 1;
        var changedSignature = token[..signatureStart] + (token[signatureStart] == 'A' ? "B" : "A") + token[(signatureStart + 1)..];
        Assert.Null(authority.AccountOf(changedSignature));
        Assert.Null(new GatewayAuthority((_, _) => Task.FromResult<long?>(null), () => now.AddMinutes(11)).AccountOf(token));
    }

    [Fact]
    public void Arbitrary_gateway_frame_buffers_never_escape_the_format_error_contract()
    {
        var random = new Random(0x51a7);
        for (var round = 0; round < 20_000; round++)
        {
            var buffer = new List<byte>(random.Next(0, 513));
            for (var index = 0; index < buffer.Capacity; index++)
                buffer.Add((byte)random.Next(0, 256));

            try
            {
                var frames = GatewayEnvelope.TakeFrames(buffer);
                Assert.All(frames, frame => Assert.InRange(frame.Length, 0, GatewayEnvelope.MaxFrameLength));
                Assert.True(buffer.Count < 5 || (buffer[0] == 0 &&
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Skip(1).Take(4).ToArray()) <= GatewayEnvelope.MaxFrameLength));
            }
            catch (FormatException)
            {
                // Invalid compression flags and oversized declared lengths are protocol errors.
            }
        }
    }

    [Fact]
    public async Task Sensitive_request_bytes_are_not_written_to_host_logs()
    {
        const string sentinel = "ACCOUNT-SECRET-123";
        var routes = new PublicGatewayRoutes(
            new Dictionary<string, GatewayHandler>(),
            new NoticeHub(),
            new GatewayAuthority((_, _) => Task.FromResult<long?>(null), () => DateTimeOffset.UtcNow));
        await using var server = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, routes);
        using var http = new HttpClient(new SocketsHttpHandler())
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var content = new ByteArrayContent(GatewayEnvelope.Frame(System.Text.Encoding.ASCII.GetBytes(sentinel)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc+proto");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/eve_public.gateway.Requests/Send")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var previousError = Console.Error;
        using var log = new StringWriter();
        try
        {
            Console.SetError(log);
            try
            {
                using var response = await http.SendAsync(request);
            }
            catch (HttpRequestException)
            {
                // The malformed protobuf resets this stream.
            }
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.DoesNotContain(sentinel, log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(sentinel)), log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_with_a_declared_length_above_the_host_limit_is_rejected_before_its_body_arrives()
    {
        var frame = new List<byte> { 0, 0x01, 0, 0, 1 };

        var error = Assert.Throws<FormatException>(() => GatewayEnvelope.TakeFrames(frame));

        Assert.Contains(GatewayEnvelope.MaxFrameLength.ToString(), error.Message);
    }

    [PgFact]
    public async Task Skill_plans_are_created_read_changed_activated_and_deleted_through_the_gateway_and_survive_a_restart()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await using var server = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);

        // CreateRequest (skill_plan 1: name 1, skill_requirements 2, description 3) answers CreateResponse (skill_plan 1: uuid 1)
        var created = await SendOneAsync(server.Port, Plan + "CreateRequest", character, Attributes("Gunnery", "Guns", (Gunnery, 4)));
        Assert.Equal(200u, created.Status);
        Assert.Equal(Plan + "CreateResponse", created.TypeName);
        var plan = IdOf(created.Payload);
        Assert.NotEqual(Guid.Empty, plan);

        // GetAllResponse lists the identifiers; GetResponse carries the attributes the client reads
        Assert.Equal(new[] { plan }, (await SendOneAsync(server.Port, Plan + "GetAllRequest", character, [])).Ids());
        var read = await SendOneAsync(server.Port, Plan + "GetRequest", character, IdentifierField(1, plan));
        Assert.Equal(200u, read.Status);
        var attributes = Plans(read.Payload);
        // the plan keeps the reference's canonical form: every level of the skill, one entry each
        Assert.Equal("Gunnery", attributes.Name);
        Assert.Equal("Guns", attributes.Description);
        Assert.Equal(Levels(Gunnery, 4), attributes.Requirements);

        // SetNameRequest (skill_plan 1, name 2) and SetSkillRequirementsRequest (skill_plan 1, requirements 2) change the plan
        Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "SetNameRequest", character, Concat(IdentifierField(1, plan), Str(2, "Gunnery II")))).Status);
        Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "SetSkillRequirementsRequest", character,
            Concat(IdentifierField(1, plan), Requirement(2, Gunnery, 5)))).Status);
        var changed = Plans((await SendOneAsync(server.Port, Plan + "GetRequest", character, IdentifierField(1, plan))).Payload);
        Assert.Equal("Gunnery II", changed.Name);
        Assert.Equal(Levels(Gunnery, 5), changed.Requirements);

        // a milestone of the plan: train to a type (milestone.CreateRequest, milestone 1: skill_plan 1, train_to_type 2)
        var train = new ProtoWriter();
        train.Message(1, IdentifierBytes(plan));
        train.Message(2, TypeId(Gunnery));
        var milestone = await SendOneAsync(server.Port, Milestone + "CreateRequest", character, Concat(Message(1, train.ToArray())));
        Assert.Equal(200u, milestone.Status);
        var milestones = await SendOneAsync(server.Port, Milestone + "GetAllRequest", character, IdentifierField(1, plan));
        Assert.Single(milestones.Messages(1));

        // SetActiveRequest, then GetActiveResponse names it with its attributes; the plan's activation trains nothing
        Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "SetActiveRequest", character, IdentifierField(1, plan))).Status);
        var active = await SendOneAsync(server.Port, Plan + "GetActiveRequest", character, []);
        Assert.Equal(plan, IdOf(active.Payload));
        Assert.Equal("Gunnery II", Plans(active.Payload, 2).Name);

        // a restart of the Host keeps the plan, its milestone and the active choice
        await rig.RestartAsync();
        await using var restarted = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);
        Assert.Equal(new[] { plan }, (await SendOneAsync(restarted.Port, Plan + "GetAllRequest", character, [])).Ids());
        Assert.Single((await SendOneAsync(restarted.Port, Milestone + "GetAllRequest", character, IdentifierField(1, plan))).Messages(1));
        Assert.Equal(plan, IdOf((await SendOneAsync(restarted.Port, Plan + "GetActiveRequest", character, [])).Payload));

        // DeleteRequest removes the plan (and its milestone); a missing plan is 404 and nothing else changes
        Assert.Equal(200u, (await SendOneAsync(restarted.Port, Plan + "DeleteRequest", character, IdentifierField(1, plan))).Status);
        Assert.Empty((await SendOneAsync(restarted.Port, Plan + "GetAllRequest", character, [])).Ids());
        Assert.Equal(404u, (await SendOneAsync(restarted.Port, Plan + "GetRequest", character, IdentifierField(1, plan))).Status);
        Assert.Equal(Guid.Empty, IdOf((await SendOneAsync(restarted.Port, Plan + "GetActiveRequest", character, [])).Payload));
    }

    [PgFact]
    public async Task A_plan_limit_a_request_without_a_character_and_an_unknown_namespace_are_refused_with_the_reference_status()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await using var server = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);

        for (var i = 0; i < PgSkillPlanStore.MaxPlans; i++)
            Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "CreateRequest", character, Attributes($"Plan {i}", "", (Gunnery, 1)))).Status);
        // the eleventh plan is refused with the reference's bad request (the client shows SkillPlansErrorBadRequest)
        var refused = await SendOneAsync(server.Port, Plan + "CreateRequest", character, Attributes("Plan 10", "", (Gunnery, 1)));
        Assert.Equal(400u, refused.Status);
        Assert.Equal(Plan + "CreateResponse", refused.TypeName);

        // a request with no active identity is refused; a request type the gateway does not serve is 404
        Assert.Equal(400u, (await SendOneAsync(server.Port, Plan + "GetAllRequest", null, [])).Status);
        Assert.Equal(404u, (await SendOneAsync(server.Port, "eve_public.character.skill.plan.NoSuchRequest", character, [])).Status);
    }

    [PgFact]
    public async Task Character_titles_are_listed_worn_and_taken_off_through_the_gateway_and_survive_a_restart()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        const string titleApi = "eve_public.cosmetic.character.title.api.";
        var battlecruiserSmith = Guid.Parse("00851de7-b7ca-5136-871e-d07429563c06"); // SDE characterTitles
        var starkmanChampion = Guid.Parse("04ae6e1f-d39a-5f5b-b9f4-7a2b3e426a38"); // SDE characterTitles
        var notOwned = Guid.Parse("0c231769-621e-53d5-bd49-ce07a7b4a544"); // an SDE title the character was not granted
        var titles = new CharacterTitleStore(rig.Source, rig.CharactersSchema);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await titles.GrantAsync(character, battlecruiserSmith, now);
        await titles.GrantAsync(character, starkmanChampion, now + 1);
        await using var server = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);

        // GetOwnedRequest pages: size 1, then the next token (the offset) gives the second title and no next page
        var first = await SendOneAsync(server.Port, titleApi + "GetOwnedRequest", character, Message(1, Varint(1, 1)));
        Assert.Equal(200u, first.Status);
        Assert.Equal(new[] { battlecruiserSmith }, first.Ids());
        var next = SkillPlanWire.Message(first.Payload, 2) ?? throw new InvalidOperationException("no next page");
        var token = SkillPlanWire.StringOf(next, 1);
        Assert.Equal("1", token);
        var second = await SendOneAsync(server.Port, titleApi + "GetOwnedRequest", character, Message(1, Concat(Varint(1, 1), Str(2, token))));
        Assert.Equal(new[] { starkmanChampion }, second.Ids());
        Assert.Null(SkillPlanWire.Message(second.Payload, 2));

        // nothing is worn yet: GetEquippedForCharacterResponse answers no_title (2)
        var none = await SendOneAsync(server.Port, titleApi + "GetEquippedForCharacterRequest", character, []);
        Assert.Equal(1L, SkillPlanWire.VarintOf(none.Payload, 2));

        // EquipRequest (title 1) wears an owned title; a title the character does not own is refused with 409
        Assert.Equal(200u, (await SendOneAsync(server.Port, titleApi + "EquipRequest", character, IdentifierField(1, battlecruiserSmith))).Status);
        var worn = await SendOneAsync(server.Port, titleApi + "GetEquippedForCharacterRequest", character, []);
        Assert.Equal(battlecruiserSmith, SkillPlanWire.IdentifierOf(SkillPlanWire.Message(worn.Payload, 1) ?? ReadOnlyMemory<byte>.Empty));
        Assert.Equal(409u, (await SendOneAsync(server.Port, titleApi + "EquipRequest", character, IdentifierField(1, notOwned))).Status);
        Assert.Equal(battlecruiserSmith, await titles.EquippedAsync(character));

        // UnequipRequest takes it off; a restart keeps the owned titles, and the worn one is kept as well
        Assert.Equal(200u, (await SendOneAsync(server.Port, titleApi + "UnequipRequest", character, [])).Status);
        Assert.Null(await titles.EquippedAsync(character));
        Assert.Equal(200u, (await SendOneAsync(server.Port, titleApi + "EquipRequest", character, IdentifierField(1, starkmanChampion))).Status);
        await rig.RestartAsync();
        await using var restarted = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);
        Assert.Equal(starkmanChampion, await new CharacterTitleStore(rig.Source, rig.CharactersSchema).EquippedAsync(character));
        var afterRestart = await SendOneAsync(restarted.Port, titleApi + "GetOwnedRequest", character, []);
        Assert.Equal(new[] { battlecruiserSmith, starkmanChampion }, afterRestart.Ids());
    }

    [PgFact]
    public async Task A_request_without_a_token_or_for_a_character_of_another_account_is_refused()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await using var server = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);

        // no token is 401; a token of account 2 does not own account 1's character (403); the owner's token is answered
        Assert.Equal(401u, (await SendOneAsync(server.Port, Plan + "GetAllRequest", character, [], authorization: null)).Status);
        Assert.Equal(403u, (await SendOneAsync(server.Port, Plan + "GetAllRequest", character, [], authorization: OtherUserToken)).Status);
        Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "GetAllRequest", character, [])).Status);
    }

    [PgFact]
    public async Task A_title_the_character_puts_on_is_announced_on_the_notice_stream_of_its_account()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        const string titleApi = "eve_public.cosmetic.character.title.api.";
        var battlecruiserSmith = Guid.Parse("00851de7-b7ca-5136-871e-d07429563c06"); // SDE characterTitles
        await new CharacterTitleStore(rig.Source, rig.CharactersSchema).GrantAsync(character, battlecruiserSmith, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using var server = await PublicGatewayServer.StartAsync(IPAddress.Loopback, 0, rig.Gateway);

        // the Consume stream: its first request frame names the active character; the response keeps streaming after the request ends
        using var http = new HttpClient(new SocketsHttpHandler())
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        var identity = new ByteArrayContent(GatewayEnvelope.Frame(RequestFrame(Plan + "GetAllRequest", character, [])));
        identity.Headers.ContentType = new MediaTypeHeaderValue("application/grpc+proto");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/eve_public.gateway.Notices/Consume")
        {
            Content = identity,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("authorization", UserToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var notices = await response.Content.ReadAsStreamAsync();
        // the server reads the identity frame beside the stream; a short wait lets it take the character before the notice is published
        await Task.Delay(500);

        // the character wears a title (Equip answers through Requests/Send): its SetNotice reaches the stream
        Assert.Equal(200u, (await SendOneAsync(server.Port, titleApi + "EquipRequest", character, IdentifierField(1, battlecruiserSmith))).Status);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var notice = await ReadFrameAsync(notices, deadline.Token);
        var any = SkillPlanWire.Message(notice, 5) ?? throw new InvalidOperationException("no payload in the notice");
        var typeUrl = SkillPlanWire.StringOf(any, 1);
        Assert.Equal(titleApi + "SetNotice", typeUrl[(typeUrl.LastIndexOf('/') + 1)..]);
        Assert.Equal((long)character, SkillPlanWire.VarintOf(SkillPlanWire.Message(notice, 6) ?? ReadOnlyMemory<byte>.Empty, 4));
        var setNotice = SkillPlanWire.Message(any, 2) ?? throw new InvalidOperationException("no value in the notice");
        Assert.Equal(battlecruiserSmith, SkillPlanWire.IdentifierOf(SkillPlanWire.Message(setNotice, 2) ?? ReadOnlyMemory<byte>.Empty));
    }

    // ---- encoding of the test requests (the client's messages, by field number) ------------------------------------------

    /// <summary>The canonical requirements of one skill: its levels 1 to <paramref name="level"/>, one entry each.</summary>
    private static (int TypeId, int Level)[] Levels(int typeId, int level) => Enumerable.Range(1, level).Select(i => (typeId, i)).ToArray();

    /// <summary>One gRPC message frame read from a stream (the length after the compression flag).</summary>
    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellationToken);
        return message;
    }

    /// <summary>The request envelope of a request type for a character (its identity), without the gRPC framing.</summary>
    private static byte[] RequestFrame(string requestName, long? character, byte[] payload)
    {
        var request = new ProtoWriter();
        request.Bytes(2, Guid.NewGuid().ToByteArray(bigEndian: true));
        var any = new ProtoWriter();
        any.String(1, "type.evetech.net/" + requestName);
        any.Bytes(2, payload);
        request.Message(3, any.ToArray());
        if (character is { } id)
        {
            var identifier = new ProtoWriter();
            identifier.Uint(1, (ulong)id);
            var active = new ProtoWriter();
            active.Message(1, identifier.ToArray());
            var context = new ProtoWriter();
            context.Message(10, active.ToArray());
            request.Message(6, context.ToArray());
        }
        return request.ToArray();
    }

    private sealed record Answer(uint Status, string TypeName, byte[] Payload)
    {
        public List<ReadOnlyMemory<byte>> Messages(int field) => SkillPlanWire.Messages(Payload, field);
        public IReadOnlyList<Guid> Ids() => Messages(1).Select(SkillPlanWire.IdentifierOf).ToList();
    }

    private static byte[] Attributes(string name, string description, (int TypeId, int Level) requirement)
    {
        var writer = new ProtoWriter();
        writer.String(1, name);
        writer.Message(2, RequirementBody(requirement.TypeId, requirement.Level));
        writer.String(3, description);
        return Message(1, writer.ToArray());
    }

    private static byte[] RequirementBody(int typeId, int level)
    {
        var writer = new ProtoWriter();
        writer.Message(1, TypeId(typeId));
        writer.Uint(2, (ulong)level);
        return writer.ToArray();
    }

    private static byte[] Requirement(int field, int typeId, int level) => Message(field, RequirementBody(typeId, level));

    private static byte[] TypeId(int typeId)
    {
        var writer = new ProtoWriter();
        writer.Uint(1, (ulong)typeId);
        return writer.ToArray();
    }

    private static byte[] IdentifierBytes(Guid id) => SkillPlanWire.EncodeIdentifier(id);

    private static byte[] IdentifierField(int field, Guid id) => Message(field, SkillPlanWire.EncodeIdentifier(id));

    private static byte[] Str(int field, string value)
    {
        var writer = new ProtoWriter();
        writer.String(field, value);
        return writer.ToArray();
    }

    private static byte[] Message(int field, byte[] message)
    {
        var writer = new ProtoWriter();
        writer.Message(field, message);
        return writer.ToArray();
    }

    private static byte[] Varint(int number, long value)
    {
        var writer = new ProtoWriter();
        writer.Uint(number, (ulong)value);
        return writer.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static Guid IdOf(byte[] payload) => SkillPlanWire.IdentifierOf(SkillPlanWire.Message(payload, 1) ?? ReadOnlyMemory<byte>.Empty);

    /// <summary>The attributes of a plan message at a field (1 for GetResponse, 2 for GetActiveResponse's info).</summary>
    private static (string Name, string Description, (int TypeId, int Level)[] Requirements) Plans(byte[] payload, int field = 1)
    {
        var attributes = SkillPlanWire.Message(payload, field) ?? throw new InvalidOperationException("no plan in the answer");
        var (name, description, requirements) = SkillPlanWire.DecodePlan(attributes);
        return (name, description, requirements.Select(requirement => (requirement.SkillTypeId, requirement.Level)).ToArray());
    }

    /// <summary>Sends one request frame on a new HTTP/2 connection and reads its answer frame and the gRPC status trailer.</summary>
    private static Task<Answer> SendOneAsync(int port, string requestName, long? character, byte[] payload) =>
        SendOneAsync(port, requestName, character, payload, UserToken);

    private static async Task<Answer> SendOneAsync(int port, string requestName, long? character, byte[] payload, string? authorization)
    {
        using var http = new HttpClient(new SocketsHttpHandler())
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        var body = GatewayEnvelope.Frame(RequestFrame(requestName, character, payload));
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc+proto");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/eve_public.gateway.Requests/Send")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("authorization", authorization);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("0", response.TrailingHeaders.GetValues("grpc-status").Single());
        var bytes = (await response.Content.ReadAsByteArrayAsync()).ToList();
        var frames = GatewayEnvelope.TakeFrames(bytes);
        var answer = Assert.Single(frames);
        return Decode(answer);
    }

    private static Answer Decode(byte[] response)
    {
        var status = (uint)SkillPlanWire.VarintOf(response, 3);
        var any = SkillPlanWire.Message(response, 5);
        if (any is null)
            return new Answer(status, "", []);
        var typeUrl = SkillPlanWire.StringOf(any.Value, 1);
        var value = ProtoReader.Fields(any.Value).FirstOrDefault(field => field.Number == 2).Bytes.ToArray();
        return new Answer(status, typeUrl[(typeUrl.LastIndexOf('/') + 1)..], value);
    }
}
