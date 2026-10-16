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
public sealed class PublicGatewayTests
{
    private const string Plan = "eve_public.character.skill.plan.";
    private const string Milestone = "eve_public.character.skill.plan.milestone.";
    private const int Gunnery = 3300;

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
        Assert.Equal(("Gunnery", "Guns", (Gunnery, 4)), (attributes.Name, attributes.Description, attributes.Requirement));

        // SetNameRequest (skill_plan 1, name 2) and SetSkillRequirementsRequest (skill_plan 1, requirements 2) change the plan
        Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "SetNameRequest", character, Concat(IdentifierField(1, plan), Str(2, "Gunnery II")))).Status);
        Assert.Equal(200u, (await SendOneAsync(server.Port, Plan + "SetSkillRequirementsRequest", character,
            Concat(IdentifierField(1, plan), Requirement(2, Gunnery, 5)))).Status);
        var changed = Plans((await SendOneAsync(server.Port, Plan + "GetRequest", character, IdentifierField(1, plan))).Payload);
        Assert.Equal(("Gunnery II", (Gunnery, 5)), (changed.Name, changed.Requirement));

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

    // ---- encoding of the test requests (the client's messages, by field number) ------------------------------------------

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
    private static (string Name, string Description, (int TypeId, int Level) Requirement) Plans(byte[] payload, int field = 1)
    {
        var attributes = SkillPlanWire.Message(payload, field) ?? throw new InvalidOperationException("no plan in the answer");
        var (name, description, requirements) = SkillPlanWire.DecodePlan(attributes);
        var first = Assert.Single(requirements);
        return (name, description, (first.SkillTypeId, first.Level));
    }

    /// <summary>Sends one request frame on a new HTTP/2 connection and reads its answer frame and the gRPC status trailer.</summary>
    private static async Task<Answer> SendOneAsync(int port, string requestName, long? character, byte[] payload)
    {
        using var http = new HttpClient(new SocketsHttpHandler())
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
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
        var body = GatewayEnvelope.Frame(request.ToArray());
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc+proto");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/eve_public.gateway.Requests/Send", content);
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
