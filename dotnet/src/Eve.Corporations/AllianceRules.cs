using System.Text;

namespace Eve.Corporations;

/// <summary>Source name policy and executor election, independent of persistence and protocol.</summary>
public static class AllianceRules
{
    public const long ApplicationDelayMs = 86400000;
    public const long ApplicationWarRecheckDelayMs = 3600000;
    private static string Normalize(string text) => text.Normalize(NormalizationForm.FormC);
    private static bool Substantive(string text) => text.EnumerateRunes().Any(Rune.IsLetterOrDigit);
    private static bool Valid(string text,string punctuation) => text.EnumerateRunes().All(r => Rune.IsLetterOrDigit(r) || r.IsAscii && punctuation.Contains((char)r.Value));

    public static string? ApplicationWarHoldReason(long corporationId,long allianceId,IEnumerable<AllianceApplicationWar> wars)
    {
        foreach (var war in wars)
        {
            if (war.DeclaredById == corporationId) return "AGGRESSOR_WAR";
            if (war.Mutual) return "MUTUAL_WAR";
            if (war.AgainstId != corporationId && war.AgainstId != allianceId) return "FOREIGN_ALLY_WAR";
        }
        return null;
    }

    public static NewAlliance Validate(NewAlliance input)
    {
        var name = Normalize(input.Name).Trim();
        var shortName = Normalize(input.ShortName).Trim().ToUpperInvariant();
        var description = Normalize(input.Description);
        var url = Normalize(input.Url);
        if (name.Length == 0) throw new CorporationError("ALLIANCE_NAME_REQUIRED");
        if (name.EnumerateRunes().Count() is < 4 or > 50) throw new CorporationError("ALLIANCE_NAME_INVALID_LENGTH");
        if (!Valid(name," .'-") || !Substantive(name)) throw new CorporationError("ALLIANCE_NAME_INVALID_CHARACTERS");
        if (shortName.EnumerateRunes().Count() is < 1 or > 5) throw new CorporationError("ALLIANCE_SHORT_NAME_INVALID_LENGTH");
        if (!Valid(shortName,".-") || !Substantive(shortName)) throw new CorporationError("ALLIANCE_SHORT_NAME_INVALID_CHARACTERS");
        if (description.EnumerateRunes().Count() > 5000) throw new CorporationError("ALLIANCE_DESCRIPTION_TOO_LONG");
        if (url.EnumerateRunes().Count() > 2048) throw new CorporationError("ALLIANCE_URL_TOO_LONG");
        return new(name,shortName,description,url);
    }

    public static (long? Executor,bool Suspended) Elect(IReadOnlyList<AllianceMember> members,long? incumbent,bool dictatorial)
    {
        var ids = members.Select(m => m.CorporationId).ToHashSet();
        var current = incumbent is { } id && ids.Contains(id) ? incumbent : null;
        if (dictatorial && current is not null) return (current,false);
        var threshold = (members.Count + 1) / 2;
        var votes = members.Where(m => m.ChosenExecutorId is { } vote && ids.Contains(vote)).GroupBy(m => m.ChosenExecutorId)
            .Select(g => (Id:g.Key,Count:g.Count())).Where(v => v.Count >= threshold)
            .OrderByDescending(v => v.Count).ThenBy(v => v.Id).ToArray();
        if (votes.Length == 0) return (current,true);
        var tied = votes.Where(v => v.Count == votes[0].Count).ToArray();
        if (tied.Length == 1) return (tied[0].Id,false);
        return current is { } c && tied.Any(v => v.Id == c) ? (current,false) : (current,true);
    }
}

public sealed record AllianceApplicationWar(long DeclaredById, long AgainstId, bool Mutual);
public sealed record AllianceApplicationDueJob(long ApplicationId, long AllianceId, long CorporationId, long DueAtMs);

public delegate Task<IReadOnlyList<AllianceApplicationWar>> AllianceWarReadCallback(
    long corporationId, long allianceId, CancellationToken cancellationToken);
public sealed record AllianceApplicationDueOutcome(bool Effective, long? RescheduleAtMs);

public delegate Task AllianceApplicationScheduleCallback(
    System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
    AllianceApplicationDueJob job, CancellationToken cancellationToken);
public delegate Task AllianceAffiliationMutationCallback(
    System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
    long corporationId, long? allianceId, CancellationToken cancellationToken);

public delegate Task CorporationMemberAffiliationMutationCallback(
    System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
    long characterId, long corporationId, CancellationToken cancellationToken);
