using Eve.Agents;

namespace Eve.Services.Core;


public sealed record AgentResearchField(long SkillTypeId, long DatacoreTypeId, string DatacoreName);

/// <summary>An SDE type as the research fields read it: its English name, and the skill its datacore requires (dogma attribute 182).</summary>
public sealed record AgentResearchTypeRow(long TypeId, string EnglishName, long GroupId, bool Published, long? RequiredSkill1);


public sealed record AgentResearchAgentRow(long AgentId, int AgentTypeId, IReadOnlyList<long> Skills);

/// <summary>What the server holds for the R&amp;D agents: the fields from the SDE, and the project store and the datacore purchase.</summary>
public interface IAgentResearchPort
{

    IReadOnlyList<AgentResearchField> FieldsFor(long agentId);

    ResearchProjectStore Projects { get; }

    /// <summary>Buys datacores for a project in one transaction: the RP, the ISK and the datacores. Refused with nothing written.</summary>
    ValueTask<AgentDatacorePurchase> BuyDatacoresAsync(long characterId, long agentId, long skillTypeId, long quantity, long stationId, CancellationToken cancellationToken);
}

/// <summary>The outcome of a datacore purchase: the datacores bought, or the reason it was refused.</summary>
public sealed record AgentDatacorePurchase(AgentDatacoreRefusal Refusal, long Quantity);

public enum AgentDatacoreRefusal
{
    None = 0,
    NotEnoughRp = 1,
    NotEnoughIsk = 2,
    NotDeliverable = 3,
}

public static class AgentResearchFields
{
    /// <summary>The datacore group (the reference's GROUP_DATACORES).</summary>
    public const long DatacoreGroupId = 333;

    /// <summary>

    /// the datacore that names it as its required skill (dogma attribute 182). A datacore is a published group 333 type. When two
    /// datacores name one skill, the higher type id wins, the reference's last-wins order. A field with no datacore stays listed;
    /// buying it is refused. Fields are in ascending skill order, as the reference lists them.
    /// </summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<AgentResearchField>> FieldsByAgent(
        IEnumerable<AgentResearchTypeRow> types, IEnumerable<AgentResearchAgentRow> agents)
    {
        var datacoreBySkill = new Dictionary<long, AgentResearchTypeRow>();
        foreach (var row in types
            .Where(row => row.GroupId == DatacoreGroupId && row.Published && row.RequiredSkill1 is not null)
            .OrderBy(row => row.TypeId))
        {
            datacoreBySkill[row.RequiredSkill1!.Value] = row;
        }

        return agents
            .Where(agent => agent.AgentTypeId == AgentAccessRule.ResearchAgentType && agent.Skills.Count > 0)
            .ToDictionary(
                agent => agent.AgentId,
                agent => (IReadOnlyList<AgentResearchField>)agent.Skills
                    .Distinct()
                    .OrderBy(skill => skill)
                    .Select(skill => datacoreBySkill.TryGetValue(skill, out var datacore)
                        ? new AgentResearchField(skill, datacore.TypeId, datacore.EnglishName)
                        : new AgentResearchField(skill, 0, string.Empty))
                    .ToArray());
    }
}
