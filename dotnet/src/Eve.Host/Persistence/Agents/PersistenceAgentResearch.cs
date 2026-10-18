using System.Text.Json;
using Eve.Agents;
using Eve.Inventory;
using Eve.Services.Core;
using Eve.Services;
using Eve.Sde;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The R&amp;D agents' server side: the fields from the pinned SDE, and the datacore purchase. The purchase takes the research lock,
/// debits the RP from the project (its points as they stand now), debits the ISK (journal reference type 112, the client's
/// refDatacoreFee), creates the datacores in the station hangar, and re-anchors the project, all in one transaction. A refusal
/// writes nothing. The buyer is told of the datacores after the commit.
/// </summary>
public sealed class PersistenceAgentResearch(
    NpgsqlDataSource source,
    ResearchProjectStore projects,
    WalletTransactions wallets,
    PgItemStore items,
    ItemTypeCatalog types,
    CharacterSessions sessions,
    IReadOnlyDictionary<long, IReadOnlyList<AgentResearchField>> fieldsByAgent,
    Func<long> nowMs) : IAgentResearchPort
{
    /// <summary>The journal reference type of the datacore fee (eve-logic appConst.py:741, refDatacoreFee).</summary>
    public const int DatacoreFeeRefType = 112;

    private const int RequiredSkillAttribute = 182;

    public IReadOnlyList<AgentResearchField> FieldsFor(long agentId) =>
        fieldsByAgent.TryGetValue(agentId, out var fields) ? fields : [];

    public ResearchProjectStore Projects { get; } = projects;

    /// <summary>
    /// The research fields of every R&amp;D agent, from the SDE: npcCharacters (the agent type and its skills), the group 333 datacores
    /// (published) and their required skill (dogma attribute 182). The reference builds the same table from these three files.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, IReadOnlyList<AgentResearchField>>> LoadFieldsAsync(SdeReader sde, CancellationToken cancellationToken)
    {
        var typeRows = await sde.ReadAsync("types", cancellationToken: cancellationToken);
        var datacores = typeRows.Where(row => GroupId(row.Doc) == AgentResearchFields.DatacoreGroupId && Published(row.Doc)).Select(row => row.Key).ToArray();
        var skillOf = (await sde.ReadAsync("typeDogma", datacores, cancellationToken))
            .ToDictionary(row => row.Key, row => RequiredSkill(row.Doc));
        var types = typeRows.Select(row => new AgentResearchTypeRow(row.Key, EnglishName(row.Doc), GroupId(row.Doc), Published(row.Doc),
            skillOf.GetValueOrDefault(row.Key)));
        var agents = (await sde.ReadAsync("npcCharacters", cancellationToken: cancellationToken)).Select(row => new AgentResearchAgentRow(
            row.Key, AgentTypeId(row.Doc), Skills(row.Doc)));
        return AgentResearchFields.FieldsByAgent(types, agents);
    }

    public async ValueTask<AgentDatacorePurchase> BuyDatacoresAsync(
        long characterId, long agentId, long skillTypeId, long quantity, long stationId, CancellationToken cancellationToken)
    {
        var field = FieldsFor(agentId).FirstOrDefault(candidate => candidate.SkillTypeId == skillTypeId);
        if (field is null || field.DatacoreTypeId <= 0 || quantity <= 0 || stationId <= 0)
            return new AgentDatacorePurchase(AgentDatacoreRefusal.NotDeliverable, 0);
        var rpCost = quantity * ResearchRules.DatacoreResearchPoints;
        var iskCost = quantity * ResearchRules.DatacoreIsk;
        var now = nowMs();

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Projects.LockAsync(connection, transaction, characterId, cancellationToken);
        var project = await Projects.ReadAsync(connection, transaction, characterId, agentId, cancellationToken);
        if (project is null) return new AgentDatacorePurchase(AgentDatacoreRefusal.NotDeliverable, 0);
        var accrued = ResearchProjectStore.AccruedPoints(project, DateTimeOffset.FromUnixTimeMilliseconds(now));
        if (accrued < rpCost) return new AgentDatacorePurchase(AgentDatacoreRefusal.NotEnoughRp, 0);

        var reference = $"research-datacore:{characterId}:{agentId}:{now}";
        WalletSettlement settlement;
        try
        {
            settlement = await wallets.AdjustAsync(connection, transaction, new WalletAccount(characterId), -iskCost, 0,
                DatacoreFeeRefType, "Datacore purchase", now, reference, reference, cancellationToken);
        }
        catch (Exception exception) when (exception is InsufficientFundsException or WalletNotFoundException)
        {
            return new AgentDatacorePurchase(AgentDatacoreRefusal.NotEnoughIsk, 0);
        }

        var itemId = await items.CreateAsync(connection, transaction,
            new NewItem((int)field.DatacoreTypeId, characterId, stationId, ItemFlags.Hangar, quantity), cancellationToken);
        await Projects.RebaseAsync(connection, transaction,
            project with { StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(now), RemainderPoints = accrued - rpCost }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var entry in settlement.Entries)
            wallets.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
        var type = await types.GetAsync((int)field.DatacoreTypeId, cancellationToken);
        sessions.Of(characterId)?.SendNotification("OnItemChange", "clientID",
            InventoryWire.PackedRow(new ItemRow(itemId, (int)field.DatacoreTypeId, characterId, stationId, ItemFlags.Hangar, quantity,
                type?.GroupId ?? 0, type?.CategoryId ?? 0)),
            new Eve.Marshal.PyDict([]), Eve.Marshal.PyNone.Instance);
        // The client's agents.py OnDatacoreBought(characterID, agentID, balance): it refreshes the research view and the cash.
        if (settlement.Entries.LastOrDefault(entry => entry.OwnerId == characterId && entry.AccountKey == 1000) is { } cash)
            sessions.Of(characterId)?.SendNotification("OnDatacoreBought", "clientID",
                new Eve.Marshal.PyInt(characterId), new Eve.Marshal.PyInt(agentId), new Eve.Marshal.PyReal((double)cash.Balance));
        return new AgentDatacorePurchase(AgentDatacoreRefusal.None, quantity);
    }

    private static long GroupId(JsonElement doc) =>
        doc.TryGetProperty("groupID", out var group) && group.ValueKind == JsonValueKind.Number ? group.GetInt64() : 0;

    private static bool Published(JsonElement doc) =>
        doc.TryGetProperty("published", out var published) && published.ValueKind == JsonValueKind.True;

    private static int AgentTypeId(JsonElement doc) =>
        doc.TryGetProperty("agent", out var agent) && agent.TryGetProperty("agentTypeID", out var type) &&
        type.ValueKind == JsonValueKind.Number ? type.GetInt32() : 0;

    /// <summary>The skill type ids an NPC character offers (npcCharacters.skills), empty when it offers none.</summary>
    private static IReadOnlyList<long> Skills(JsonElement doc) =>
        doc.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array
            ? skills.EnumerateArray()
                .Where(skill => skill.TryGetProperty("typeID", out var id) && id.ValueKind == JsonValueKind.Number)
                .Select(skill => skill.GetProperty("typeID").GetInt64())
                .ToArray()
            : [];

    private static string EnglishName(JsonElement doc) =>
        doc.TryGetProperty("name", out var names) && names.TryGetProperty("en", out var english) && english.ValueKind == JsonValueKind.String
            ? english.GetString() ?? ""
            : "";

    private static long? RequiredSkill(JsonElement doc)
    {
        if (!doc.TryGetProperty("dogmaAttributes", out var attributes)) return null;
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.TryGetProperty("attributeID", out var id) && id.GetInt32() == RequiredSkillAttribute &&
                attribute.TryGetProperty("value", out var value))
                return (long)Math.Round(value.GetDouble());
        }
        return null;
    }
}
