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
internal sealed class PersistenceAgentResearch(
    NpgsqlDataSource source,
    ResearchProjectStore projects,
    WalletTransactions wallets,
    PgItemStore items,
    ItemTypeCatalog types,
    CharacterSessions sessions,
    IReadOnlyList<AgentResearchField> fields,
    Func<long> nowMs) : IAgentResearchPort
{
    /// <summary>The journal reference type of the datacore fee (eve-logic appConst.py:741, refDatacoreFee).</summary>
    public const int DatacoreFeeRefType = 112;

    private const int RequiredSkillAttribute = 182;

    public IReadOnlyList<AgentResearchField> Fields { get; } = fields;

    public ResearchProjectStore Projects { get; } = projects;

    /// <summary>The research fields, from the SDE types and the dogma attribute that links each datacore to its skill.</summary>
    public static async Task<IReadOnlyList<AgentResearchField>> LoadFieldsAsync(SdeReader sde, CancellationToken cancellationToken)
    {
        var typeRows = await sde.ReadAsync("types", cancellationToken: cancellationToken);
        var datacores = typeRows.Where(row => EnglishName(row.Doc).StartsWith("Datacore - ", StringComparison.Ordinal)).Select(row => row.Key).ToArray();
        var skillOf = (await sde.ReadAsync("typeDogma", datacores, cancellationToken))
            .ToDictionary(row => row.Key, row => RequiredSkill(row.Doc));
        return AgentResearchFields.FromSde(typeRows.Select(row => new AgentResearchTypeRow(row.Key, EnglishName(row.Doc), skillOf.GetValueOrDefault(row.Key))));
    }

    public async ValueTask<AgentDatacorePurchase> BuyDatacoresAsync(
        long characterId, long agentId, long skillTypeId, long quantity, long stationId, CancellationToken cancellationToken)
    {
        var field = Fields.FirstOrDefault(candidate => candidate.SkillTypeId == skillTypeId);
        if (field is null || quantity <= 0 || stationId <= 0)
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
        return new AgentDatacorePurchase(AgentDatacoreRefusal.None, quantity);
    }

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
