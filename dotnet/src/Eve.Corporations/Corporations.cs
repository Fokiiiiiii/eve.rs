// Domain shapes for EVE.js corporationRuntimeState, corpRegistryRuntime and allianceRegistryRuntime (828823a), ADR 0009.
using System.Text.Json;

namespace Eve.Corporations;

public sealed class CorporationError(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record NewCorporation(string Name, string Ticker, string Description, string Url, decimal TaxRate,
    decimal LoyaltyPointTaxRate = 0, bool ApplicationsEnabled = true, bool FriendlyFire = false, JsonElement Settings = default);
public sealed record Corporation(long Id, string Name, string Ticker, long CeoId, long CreatorId, long? AllianceId,
    long StationId, int SolarSystemId, decimal TaxRate, decimal LoyaltyPointTaxRate, string Description, string Url,
    int MemberLimit, bool IsNpc, bool FriendlyFire, long CreatedMs, JsonElement Settings, bool Deleted = false,
    bool CeoRolesSuspended = false);
public sealed record RoleMasks(long Roles = 0, long GrantableRoles = 0, long AtHq = 0, long GrantableHq = 0,
    long AtBase = 0, long GrantableBase = 0, long AtOther = 0, long GrantableOther = 0);
public sealed record Member(long CharacterId, long CorporationId, long StartedMs, string Title, RoleMasks Masks,
    long? BaseId, int TitleMask, bool BlockRoles, int AccountKey, int DivisionId = 0, int SquadronId = 0, long? StasisUntilMs = null);
public sealed record CorporationTitle(int Id, string Name, RoleMasks Masks);
public sealed record SessionRoles(long CorpRole, long AtAll, long AtHq, long AtBase, long AtOther, long? HqId, long? BaseId, int AccountKey);
public sealed record Employment(long Id, long CharacterId, long CorporationId, long StartedMs, long? EndedMs, bool Deleted);
public sealed record Application(long Id, long CorporationId, long CharacterId, string Text, int Status,
    long CreatedMs, long UpdatedMs, string Response, bool Archived);

public sealed record CorporationRecruitmentAd(
    long Id,long CorporationId,long ChannelId,long TypeMask,long LangMask,string Description,string Title,
    long CreateFiletime,long ExpiryFiletime,long HourMask1,long MinSP,long OtherMask,IReadOnlyList<long> Recruiters);
public sealed record CorporationContact(long ContactId,int RelationshipId,long LabelMask,bool InWatchlist);
public sealed record CorporationContactLabel(long LabelId,string Name,int Color);
public sealed record CorporationBulletin(long BulletinId,long CorporationId,long CreateCharacterId,long CreateDateTime,
    long EditCharacterId,long EditDateTime,string Title,string Body,int SortOrder);

public sealed record CorporationBill(long BillId,int BillTypeId,decimal AmountIsk,decimal Interest,long DebtorId,
    long CreditorId,long DueFiletime,long ExternalId,long ExternalId2,long WarId,bool Paid,long? PaidFiletime,
    long? PaidByOwnerId,string OperationKey);

public sealed record NewAlliance(string Name, string ShortName, string Description, string Url);
public sealed record Alliance(long Id, string Name, string ShortName, string Description, string Url,
    long? ExecutorCorporationId, long CreatorCorporationId, long CreatorCharacterId, long CreatedMs, bool Deleted,
    bool Suspended = false, bool Dictatorial = false)
{
    public long? ActiveExecutorId => Suspended ? null : ExecutorCorporationId;
}
public sealed record AllianceApplication(long Id,long AllianceId,long CorporationId,string Text,int Status,long CreatedMs,long UpdatedMs,bool Archived,long? DueMs);
public sealed record AllianceMember(long AllianceId, long CorporationId, long StartedMs, long? ChosenExecutorId);

/// <summary>Rules and numeric masks ported from corporationRuntimeState.js. No protocol or database dependency.</summary>
public static class CorporationRoles
{
    public const long Director = 1;
    public const long RentOffice = 1L << 41;
    public const long PersonnelManager = 128;
    public const long Accountant = 256;
    public const long JuniorAccountant = 4503599627370496;
    public const long Trader = 18014398509481984;
    public const long Diplomat = 17179869184;
    public const long ChatManager = 36_028_797_018_963_968L;
    public const long All = long.MaxValue & ~((7L << 4) | (31L << 36));
    public const long Locational = 14L | (127L << 13) | (127L << 20) | (127L << 42);
    public const long Administrative = All & ~Locational;
    public const long Grantable = Administrative & ~Director;

    public static bool IsKnown(long value, long allowed) => value >= 0 && (value & ~allowed) == 0;
    public static long WalletTakeRole(int accountKey) => accountKey is >= 1000 and <= 1006 ? 1L << (27 + accountKey - 1000) : 0;
    private static long Effective(long direct, long allowed, IEnumerable<long> titles, long? titleAllowed = null)
    {
        if (!IsKnown(direct, allowed)) return 0;
        return titles.Where(role => IsKnown(role, titleAllowed ?? allowed)).Aggregate(direct, (mask, role) => mask | role);
    }

    public static bool HasHangarAccess(SessionRoles? roles, long stationId, int flag, bool take)
    {
        if (roles is null || flag is < 115 or > 121)
            return false;
        var scoped = stationId == roles.HqId
            ? roles.AtHq
            : stationId == roles.BaseId ? roles.AtBase : roles.AtOther;
        // EVE hangar division 1..7: Query uses role bits 20..26; Take uses 13..19.
        var roleBit = flag - (take ? 102 : 95);
        return (scoped & (1L << roleBit)) != 0;
    }

    public static SessionRoles Session(Corporation corporation, Member member, long? stationId, IReadOnlyList<CorporationTitle> titles)
    {
        var assigned = member.TitleMask is >= 0 and <= 65535
            ? titles.Where(t => t.Id > 0 && (t.Id & (t.Id - 1)) == 0 && (member.TitleMask & t.Id) == t.Id).ToArray()
            : [];
        var all = Effective(member.Masks.Roles, Administrative, assigned.Select(t => t.Masks.Roles), Grantable);
        var director = !corporation.CeoRolesSuspended && corporation.CeoId == member.CharacterId ||
            (all & Director) != 0;
        var hq = director ? Locational : Effective(member.Masks.AtHq, Locational, assigned.Select(t => t.Masks.AtHq));
        var atBase = director ? Locational : Effective(member.Masks.AtBase, Locational, assigned.Select(t => t.Masks.AtBase));
        var other = director ? Locational : Effective(member.Masks.AtOther, Locational, assigned.Select(t => t.Masks.AtOther));
        all = director ? Administrative : all;
        var local = stationId > 0 && stationId == corporation.StationId ? hq : stationId > 0 && stationId == member.BaseId ? atBase : other;
        return new SessionRoles(all | local, all, hq, atBase, other, corporation.StationId > 0 ? corporation.StationId : null, member.BaseId, member.AccountKey);
    }

    public static RoleMasks GrantableFor(Corporation corporation, Member member, long characterCorporationId, IReadOnlyList<CorporationTitle> titles)
    {
        if (corporation.IsNpc || characterCorporationId != corporation.Id || member.CorporationId != corporation.Id) return new RoleMasks();
        var ceo = !corporation.CeoRolesSuspended && corporation.CeoId == member.CharacterId;
        if (ceo || (IsKnown(member.Masks.Roles, Administrative) && (member.Masks.Roles & Director) != 0))
            return new RoleMasks(0, ceo ? Administrative : Grantable, 0, Locational, 0, Locational, 0, Locational);
        if (!IsKnown(member.Masks.Roles, Administrative)) return new RoleMasks();
        var assigned = member.TitleMask is >= 0 and <= 65535
            ? titles.Where(t => t.Id > 0 && (t.Id & (t.Id - 1)) == 0 && (member.TitleMask & t.Id) == t.Id).ToArray()
            : [];
        return new RoleMasks(0, Effective(member.Masks.GrantableRoles, Grantable, assigned.Select(t => t.Masks.GrantableRoles)),
            0, Effective(member.Masks.GrantableHq, Locational, assigned.Select(t => t.Masks.GrantableHq)),
            0, Effective(member.Masks.GrantableBase, Locational, assigned.Select(t => t.Masks.GrantableBase)),
            0, Effective(member.Masks.GrantableOther, Locational, assigned.Select(t => t.Masks.GrantableOther)));
    }
}
