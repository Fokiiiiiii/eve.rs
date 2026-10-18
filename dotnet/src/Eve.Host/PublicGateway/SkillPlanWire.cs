using Eve.Skills;

namespace Eve.Host.PublicGateway;

/// <summary>
/// The messages of eve_public.character.skill.plan (plan_pb2 and milestone_pb2) and their shared types (eve_public.skill.plan.Attributes,
/// SkillRequirement and Identifier, eve_public.skilltype.Identifier, eve_public.inventory.genericitemtype.Identifier), with the field numbers of
/// the V24.01 descriptors. A message the client sends is read by field number; a message it reads is written here.
/// </summary>
internal static class SkillPlanWire
{
    /// <summary>The skill plan requirement's skill type, a skilltype.Identifier (sequential 1).</summary>
    public static byte[] EncodeTypeId(long typeId)
    {
        var writer = new ProtoWriter();
        writer.Uint(1, (ulong)typeId);
        return writer.ToArray();
    }

    /// <summary>skill.plan.Identifier and milestone.Identifier: uuid 1, bytes, big-endian as uuid.UUID.bytes.</summary>
    public static byte[] EncodeIdentifier(Guid id)
    {
        var writer = new ProtoWriter();
        writer.Bytes(1, id.ToByteArray(bigEndian: true));
        return writer.ToArray();
    }

    /// <summary>The uuid of an identifier message; Guid.Empty when it has none.</summary>
    public static Guid IdentifierOf(ReadOnlyMemory<byte> message)
    {
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == 1 && field.Bytes.Length == 16)
                return new Guid(field.Bytes.Span, bigEndian: true);
        }
        return Guid.Empty;
    }

    /// <summary>The first message of a field (null when the field is absent).</summary>
    public static ReadOnlyMemory<byte>? Message(ReadOnlyMemory<byte> message, int number)
    {
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == number && field.WireType == 2)
                return field.Bytes;
        }
        return null;
    }

    /// <summary>All the messages of a repeated field, in order.</summary>
    public static List<ReadOnlyMemory<byte>> Messages(ReadOnlyMemory<byte> message, int number)
    {
        var messages = new List<ReadOnlyMemory<byte>>();
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == number && field.WireType == 2)
                messages.Add(field.Bytes);
        }
        return messages;
    }

    /// <summary>A string field (empty when absent).</summary>
    public static string StringOf(ReadOnlyMemory<byte> message, int number)
    {
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == number && field.WireType == 2)
                return System.Text.Encoding.UTF8.GetString(field.Bytes.Span);
        }
        return "";
    }

    /// <summary>A varint field (0 when absent).</summary>
    public static long VarintOf(ReadOnlyMemory<byte> message, int number)
    {
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == number && field.WireType == 0)
                return (long)field.Scalar;
        }
        return 0;
    }

    /// <summary>Attributes of a plan: name 1, skill_requirements 2 (repeated), description 3.</summary>
    public static byte[] EncodePlan(SkillPlanRecord plan)
    {
        var writer = new ProtoWriter();
        writer.String(1, plan.Name);
        foreach (var requirement in plan.Requirements)
            writer.Message(2, EncodeRequirement(requirement.SkillTypeId, requirement.Level));
        writer.String(3, plan.Description);
        return writer.ToArray();
    }

    public static (string Name, string Description, List<SkillPlanRequirement> Requirements) DecodePlan(ReadOnlyMemory<byte> message) =>
        (StringOf(message, 1), StringOf(message, 3), Messages(message, 2).Select(DecodeRequirement).ToList());

    /// <summary>SkillRequirement: skill_type 1 (skilltype.Identifier), level 2.</summary>
    public static byte[] EncodeRequirement(long skillTypeId, int level)
    {
        var writer = new ProtoWriter();
        writer.Message(1, EncodeTypeId(skillTypeId));
        writer.Uint(2, (ulong)level);
        return writer.ToArray();
    }

    public static SkillPlanRequirement DecodeRequirement(ReadOnlyMemory<byte> message)
    {
        var skillType = Message(message, 1) ?? ReadOnlyMemory<byte>.Empty;
        return new SkillPlanRequirement((int)VarintOf(skillType, 1), (int)VarintOf(message, 2));
    }

    /// <summary>milestone.Attributes: skill_plan 1 (skill.plan.Identifier), train_to_type 2 (genericitemtype.Identifier), skill 3, description 4.</summary>
    public static (Guid PlanId, int? TrainToTypeId, SkillPlanRequirement? Skill, string Description) DecodeMilestone(ReadOnlyMemory<byte> message)
    {
        var plan = IdentifierOf(Message(message, 1) ?? ReadOnlyMemory<byte>.Empty);
        var trainTo = Message(message, 2);
        var skill = Message(message, 3);
        return (plan,
            trainTo is { } target ? (int?)VarintOf(target, 1) : null,
            skill is { } requirement ? DecodeRequirement(requirement) : null,
            StringOf(message, 4));
    }

    /// <summary>A milestone as the client reads it: identifier 1 and data 2 (the milestone's attributes, with its plan).</summary>
    public static byte[] EncodeMilestoneEntry(SkillPlanMilestone milestone)
    {
        var identifier = new ProtoWriter();
        identifier.Bytes(1, milestone.MilestoneId.ToByteArray(bigEndian: true));
        var data = new ProtoWriter();
        data.Message(1, EncodeIdentifier(milestone.PlanId));
        if (milestone.TrainToTypeId is { } target)
        {
            data.Message(2, EncodeTypeId(target));
        }
        else if (milestone.SkillTypeId is { } skillType)
        {
            data.Message(3, EncodeRequirement(skillType, milestone.Level));
        }
        data.String(4, milestone.Description);
        var entry = new ProtoWriter();
        entry.Message(1, identifier.ToArray());
        entry.Message(2, data.ToArray());
        return entry.ToArray();
    }
}
