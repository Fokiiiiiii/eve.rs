using Eve.Skills;

namespace Eve.Host.PublicGateway;

/// <summary>
/// The skill plan namespace of the public gateway (eve_public.character.skill.plan, the plan and milestone requests): the personal plans of
/// the active character, kept by <see cref="PgSkillPlanStore"/>. The answers are those of the EveJS skill plan gateway
/// (services/skills/plans/skillPlanState.js): a missing plan is 404, a refused write 400, and an unknown request is not answered here.
/// The requirements are stored in the reference's canonical form (prerequisites absorbed and listed first). Plans do not train skills.
/// </summary>
internal static class SkillPlanGateway
{
    private const string PlanPrefix = "eve_public.character.skill.plan.";
    private const string MilestonePrefix = "eve_public.character.skill.plan.milestone.";

    public static IReadOnlyDictionary<string, GatewayHandler> Handlers(PgSkillPlanStore store, SkillCatalog catalog)
    {
        var handlers = new Dictionary<string, GatewayHandler>(StringComparer.Ordinal);

        Add(handlers, PlanPrefix, "GetAllRequest", "GetAllResponse", async (request, ct) =>
        {
            var writer = new ProtoWriter();
            foreach (var id in await store.ListAsync(request.CharacterId!.Value, ct))
                writer.Message(1, SkillPlanWire.EncodeIdentifier(id));
            return writer.ToArray();
        });
        Add(handlers, PlanPrefix, "GetRequest", "GetResponse", async (request, ct) =>
        {
            var plan = await store.GetAsync(request.CharacterId!.Value, IdentifierField(request.Payload), ct)
                ?? throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan not found");
            var writer = new ProtoWriter();
            writer.Message(1, SkillPlanWire.EncodePlan(plan));
            return writer.ToArray();
        });
        // no plan is shared between characters yet: the reference answers an empty message
        Add(handlers, PlanPrefix, "GetSharedRequest", "GetSharedResponse", (_, _) => Task.FromResult(Array.Empty<byte>()));
        Add(handlers, PlanPrefix, "CreateRequest", "CreateResponse", async (request, ct) =>
        {
            var plan = SkillPlanWire.Message(request.Payload, 1) ?? ReadOnlyMemory<byte>.Empty;
            var (name, description, requirements) = SkillPlanWire.DecodePlan(plan);
            var id = await store.CreateAsync(request.CharacterId!.Value, name, description, SkillPlanCanonical.Canonicalize(requirements, catalog).ToList(), ct);
            var writer = new ProtoWriter();
            writer.Message(1, SkillPlanWire.EncodeIdentifier(id));
            return writer.ToArray();
        });
        Add(handlers, PlanPrefix, "DeleteRequest", "DeleteResponse", async (request, ct) =>
        {
            await store.DeleteAsync(request.CharacterId!.Value, IdentifierField(request.Payload), ct);
            return [];
        });
        Add(handlers, PlanPrefix, "SetNameRequest", "SetNameResponse", async (request, ct) =>
        {
            await store.UpdateAsync(request.CharacterId!.Value, IdentifierField(request.Payload), name: SkillPlanWire.StringOf(request.Payload, 2), cancellationToken: ct);
            return [];
        });
        Add(handlers, PlanPrefix, "SetDescriptionRequest", "SetDescriptionResponse", async (request, ct) =>
        {
            await store.UpdateAsync(request.CharacterId!.Value, IdentifierField(request.Payload), description: SkillPlanWire.StringOf(request.Payload, 2), cancellationToken: ct);
            return [];
        });
        Add(handlers, PlanPrefix, "SetSkillRequirementsRequest", "SetSkillRequirementsResponse", async (request, ct) =>
        {
            var requirements = SkillPlanWire.Messages(request.Payload, 2).Select(SkillPlanWire.DecodeRequirement);
            await store.UpdateAsync(request.CharacterId!.Value, IdentifierField(request.Payload),
                requirements: SkillPlanCanonical.Canonicalize(requirements, catalog).ToList(), cancellationToken: ct);
            return [];
        });
        Add(handlers, PlanPrefix, "GetActiveRequest", "GetActiveResponse", async (request, ct) =>
        {
            var characterId = request.CharacterId!.Value;
            var active = await store.ActiveAsync(characterId, ct);
            var writer = new ProtoWriter();
            writer.Message(1, SkillPlanWire.EncodeIdentifier(active));
            if (active != Guid.Empty && await store.GetAsync(characterId, active, ct) is { } plan)
                writer.Message(2, SkillPlanWire.EncodePlan(plan));
            return writer.ToArray();
        });
        Add(handlers, PlanPrefix, "SetActiveRequest", "SetActiveResponse", async (request, ct) =>
        {
            await store.SetActiveAsync(request.CharacterId!.Value, IdentifierField(request.Payload), ct);
            return [];
        });

        Add(handlers, MilestonePrefix, "GetAllRequest", "GetAllResponse", async (request, ct) =>
        {
            var writer = new ProtoWriter();
            foreach (var milestone in await store.MilestonesAsync(request.CharacterId!.Value, IdentifierField(request.Payload), ct))
                writer.Message(1, SkillPlanWire.EncodeMilestoneEntry(milestone));
            return writer.ToArray();
        });
        Add(handlers, MilestonePrefix, "CreateRequest", "CreateResponse", async (request, ct) =>
        {
            var attributes = SkillPlanWire.Message(request.Payload, 1) ?? ReadOnlyMemory<byte>.Empty;
            var (planId, trainTo, skill, description) = SkillPlanWire.DecodeMilestone(attributes);
            var id = await store.CreateMilestoneAsync(request.CharacterId!.Value, planId, description, trainTo, skill?.SkillTypeId, skill?.Level ?? 0, ct);
            var writer = new ProtoWriter();
            writer.Message(1, SkillPlanWire.EncodeIdentifier(id));
            return writer.ToArray();
        });
        Add(handlers, MilestonePrefix, "DeleteRequest", "DeleteResponse", async (request, ct) =>
        {
            await store.DeleteMilestoneAsync(request.CharacterId!.Value, IdentifierField(request.Payload), ct);
            return [];
        });
        Add(handlers, MilestonePrefix, "SetDescriptionRequest", "SetDescriptionResponse", async (request, ct) =>
        {
            var identifier = SkillPlanWire.Message(request.Payload, 1) ?? ReadOnlyMemory<byte>.Empty;
            await store.SetMilestoneDescriptionAsync(request.CharacterId!.Value, SkillPlanWire.IdentifierOf(identifier), SkillPlanWire.StringOf(request.Payload, 2), ct);
            return [];
        });

        return handlers;
    }

    /// <summary>The uuid of the identifier in field 1 of a request (a plan or a milestone); Guid.Empty when the request names none.</summary>
    private static Guid IdentifierField(ReadOnlyMemory<byte> payload) => SkillPlanWire.Message(payload, 1) is { } plan ? SkillPlanWire.IdentifierOf(plan) : Guid.Empty;

    /// <summary>
    /// Registers one request type. Without an active character the request is refused (400); a SkillPlanException is answered with its
    /// status (404 for a missing plan, 400 for a refused write); the answer carries the response type's full name.
    /// </summary>
    private static void Add(Dictionary<string, GatewayHandler> handlers, string prefix, string request, string response,
        Func<GatewayRequest, CancellationToken, Task<byte[]>> body)
    {
        var responseType = prefix + response;
        handlers[prefix + request] = async (gateway, cancellationToken) =>
        {
            if (gateway.CharacterId is null)
                return new GatewayAnswer(400, "no active character", responseType, []);
            try
            {
                return new GatewayAnswer(200, "", responseType, await body(gateway, cancellationToken));
            }
            catch (SkillPlanException error)
            {
                return new GatewayAnswer(error.Error == SkillPlanError.NotFound ? 404u : 400u, error.Message, responseType, []);
            }
        };
    }
}
