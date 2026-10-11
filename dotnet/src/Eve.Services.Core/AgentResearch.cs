using Eve.Agents;

namespace Eve.Services.Core;

/// <summary>A science specialty field an R&amp;D agent takes: the skill, and the datacore that research in it produces.</summary>
public sealed record AgentResearchField(long SkillTypeId, long DatacoreTypeId, string DatacoreName);

/// <summary>An SDE type as the research fields read it: its English name, and the skill its datacore requires (dogma attribute 182).</summary>
public sealed record AgentResearchTypeRow(long TypeId, string EnglishName, long? RequiredSkill1);

/// <summary>What the server holds for the R&amp;D agents: the fields from the SDE, and the project store and the datacore purchase.</summary>
public interface IAgentResearchPort
{
    IReadOnlyList<AgentResearchField> Fields { get; }

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
    /// <summary>
    /// The research fields: the skill each datacore names as its required skill (dogma attribute 182), other than Science, which
    /// the civilian datacores name. No name is matched: the SDE link is the field.
    /// </summary>
    public static IReadOnlyList<AgentResearchField> FromSde(IEnumerable<AgentResearchTypeRow> types) => types
        .Where(row => row.EnglishName.StartsWith("Datacore - ", StringComparison.Ordinal) &&
            row.RequiredSkill1 is { } skill && skill != ResearchRules.ScienceSkillTypeId)
        .Select(row => new AgentResearchField(row.RequiredSkill1!.Value, row.TypeId, row.EnglishName))
        .OrderBy(field => field.SkillTypeId)
        .ToArray();
}
