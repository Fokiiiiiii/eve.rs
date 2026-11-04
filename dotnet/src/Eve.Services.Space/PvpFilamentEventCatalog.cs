using System.Globalization;
using System.Text.Json;

namespace Eve.Services.Space;

public sealed record PvpFilamentReward(int TypeId, int Amount);

public sealed record PvpFilamentEvent(
    int MatchTypeId, int ScheduleId, int FilamentTypeId, int NumberOfFleets, int FleetSize, int ShipRestrictions,
    int EventNameId, int EventDescriptionId, int FilamentDescriptionId, int RestrictionsDescriptionId,
    int RewardsHintId, int ShipRestrictionsHintId, int RulesHintId, int Qa,
    int ModuleAndImplantMetaLevelRestrictions, int MaxOfShipTypeRestriction, int MaxOfShipGroupRestriction,
    int MinimumQueueSizeMultiplier, int ProvingGroundLootTableId, int? BannedModulesTypeListId,
    int? LimitedAmountOfModulesTypeListId, int NumberOfLimitedModules, IReadOnlyList<PvpFilamentReward> Rewards,
    DateOnly StartDate, DateOnly EndDate, string Region)
{
    private static readonly TimeOnly Downtime = new(11, 0);

    public DateTimeOffset StartsAt => new(StartDate.ToDateTime(Downtime, DateTimeKind.Utc));
    public DateTimeOffset EndsAt => new(EndDate.ToDateTime(Downtime, DateTimeKind.Utc));

    public bool IsActiveAt(DateTimeOffset now) =>
        Region == "ccp" && Qa == 0 && StartsAt <= now && now < EndsAt;
}

public sealed class PvpFilamentEventCatalog
{
    private const int ClientBuild = 3396210;
    private const string ResourceName = "clientPvpFilaments.json";
    private readonly IReadOnlyList<PvpFilamentEvent> _events;

    private PvpFilamentEventCatalog(IReadOnlyList<PvpFilamentEvent> events) => _events = events;

    public static PvpFilamentEventCatalog LoadDefault()
    {
        using var stream = typeof(PvpFilamentEventCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"The client PvP Filament resource {ResourceName} is missing.");
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public IReadOnlyList<PvpFilamentEvent> ListAllEvents() => _events;

    public IReadOnlyList<PvpFilamentEvent> ListActiveEvents(DateTimeOffset now) =>
        _events.Where(entry => entry.IsActiveAt(now)).ToArray();

    public PvpFilamentEvent? GetMostRecentEvent(DateTimeOffset now) =>
        _events.Where(entry => entry.Qa == 0 && entry.EndsAt <= now)
            .OrderByDescending(entry => entry.EndsAt)
            .ThenByDescending(entry => entry.ScheduleId)
            .FirstOrDefault();

    public DateOnly? GetNextEventDate(DateTimeOffset now) =>
        _events.FirstOrDefault(entry => entry.Qa == 0 && entry.EndsAt > now)?.StartDate;

    public PvpFilamentEvent? GetActiveEventByFilamentTypeId(int typeId, DateTimeOffset now) =>
        typeId > 0 ? _events.FirstOrDefault(entry => entry.FilamentTypeId == typeId && entry.IsActiveAt(now)) : null;

    public PvpFilamentEvent? GetEvent(int matchTypeId, int scheduleId) =>
        matchTypeId > 0 && scheduleId > 0
            ? _events.FirstOrDefault(entry => entry.MatchTypeId == matchTypeId && entry.ScheduleId == scheduleId)
            : null;

    private static PvpFilamentEventCatalog Parse(JsonElement root)
    {
        if (RequiredInt(root.GetProperty("source"), "build") != ClientBuild)
            throw new InvalidDataException($"The PvP Filament resource must match client build {ClientBuild}.");

        var matches = new Dictionary<int, Match>();
        foreach (var row in root.GetProperty("matchTypes").EnumerateArray())
        {
            var match = ReadMatch(row);
            if (!matches.TryAdd(match.MatchTypeId, match))
                throw new InvalidDataException($"Duplicate PvP Filament match type {match.MatchTypeId}.");
        }

        var events = new List<PvpFilamentEvent>();
        var schedules = new HashSet<int>();
        foreach (var row in root.GetProperty("eventSchedules").EnumerateArray())
        {
            var scheduleId = PositiveInt(row, "scheduleID");
            if (!schedules.Add(scheduleId))
                throw new InvalidDataException($"Duplicate PvP Filament schedule {scheduleId}.");
            var region = RequiredString(row, "region");
            if (region is not ("ccp" or "optic"))
                throw new InvalidDataException($"PvP Filament schedule {scheduleId} has unknown region {region}.");
            var qa = RequiredInt(row, "QA");
            if (qa is not (0 or 1))
                throw new InvalidDataException($"PvP Filament schedule {scheduleId} has invalid QA flag.");
            var matchTypeId = PositiveInt(row, "matchTypeID");
            if (!matches.TryGetValue(matchTypeId, out var match))
                throw new InvalidDataException($"PvP Filament schedule {scheduleId} references missing match type {matchTypeId}.");
            var startDate = ReadDate(row, "startDate");
            var endDate = ReadDate(row, "endDate");
            if (endDate <= startDate)
                throw new InvalidDataException($"PvP Filament schedule {scheduleId} has an invalid date range.");
            if (region == "ccp")
                events.Add(match.WithSchedule(scheduleId, qa, startDate, endDate, region));
        }

        events.Sort(static (left, right) =>
        {
            var dateOrder = left.StartsAt.CompareTo(right.StartsAt);
            return dateOrder != 0 ? dateOrder : left.ScheduleId.CompareTo(right.ScheduleId);
        });
        return new PvpFilamentEventCatalog(events.AsReadOnly());
    }

    private sealed record Match(
        int MatchTypeId, int FilamentTypeId, int NumberOfFleets, int FleetSize, int ShipRestrictions,
        int EventNameId, int EventDescriptionId, int FilamentDescriptionId, int RestrictionsDescriptionId,
        int RewardsHintId, int ShipRestrictionsHintId, int RulesHintId, int ModuleAndImplantMetaLevelRestrictions,
        int MaxOfShipTypeRestriction, int MaxOfShipGroupRestriction, int MinimumQueueSizeMultiplier,
        int ProvingGroundLootTableId, int? BannedModulesTypeListId, int? LimitedAmountOfModulesTypeListId,
        int NumberOfLimitedModules, IReadOnlyList<PvpFilamentReward> Rewards)
    {
        public PvpFilamentEvent WithSchedule(int scheduleId, int qa, DateOnly startDate, DateOnly endDate, string region) =>
            new(MatchTypeId, scheduleId, FilamentTypeId, NumberOfFleets, FleetSize, ShipRestrictions,
                EventNameId, EventDescriptionId, FilamentDescriptionId, RestrictionsDescriptionId,
                RewardsHintId, ShipRestrictionsHintId, RulesHintId, qa, ModuleAndImplantMetaLevelRestrictions,
                MaxOfShipTypeRestriction, MaxOfShipGroupRestriction, MinimumQueueSizeMultiplier,
                ProvingGroundLootTableId, BannedModulesTypeListId, LimitedAmountOfModulesTypeListId,
                NumberOfLimitedModules, Rewards, startDate, endDate, region);
    }

    private static Match ReadMatch(JsonElement row)
    {
        var rewards = row.GetProperty("rewards").EnumerateArray()
            .Select(reward => new PvpFilamentReward(PositiveInt(reward, "typeID"), PositiveInt(reward, "amount")))
            .ToArray();
        if (rewards.Length == 0)
            throw new InvalidDataException("A PvP Filament match type has no sourced rewards.");
        return new Match(
            PositiveInt(row, "matchTypeID"), PositiveInt(row, "filamentTypeID"),
            PositiveInt(row, "numberOfFleets"), PositiveInt(row, "fleetSize"), PositiveInt(row, "shipRestrictions"),
            PositiveInt(row, "eventNameID"), PositiveInt(row, "eventDescriptionID"),
            PositiveInt(row, "filamentDescriptionID"), PositiveInt(row, "restrictionsDescriptionID"),
            PositiveInt(row, "rewardsHintID"), PositiveInt(row, "shipRestrictionsHintID"), PositiveInt(row, "rulesHintID"),
            NonnegativeInt(row, "moduleAndImplantMetaLevelRestrictions"), NonnegativeInt(row, "maxOfShipTypeRestriction"),
            NonnegativeInt(row, "maxOfShipGroupRestriction"), PositiveInt(row, "minimumQueueSizeMultiplier"),
            PositiveInt(row, "provingGroundLootTableID"), OptionalPositiveInt(row, "bannedModulesTypeListID"),
            OptionalPositiveInt(row, "limitedAmountOfModulesTypeListID"), NonnegativeInt(row, "numberOfLimitedModules"),
            Array.AsReadOnly(rewards));
    }

    private static int PositiveInt(JsonElement row, string name)
    {
        var value = RequiredInt(row, name);
        return value > 0 ? value : throw new InvalidDataException($"PvP Filament field {name} must be positive.");
    }

    private static int NonnegativeInt(JsonElement row, string name)
    {
        var value = RequiredInt(row, name);
        return value >= 0 ? value : throw new InvalidDataException($"PvP Filament field {name} cannot be negative.");
    }

    private static int RequiredInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"PvP Filament field {name} is missing or invalid.");

    private static int? OptionalPositiveInt(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.TryGetInt32(out var number) && number > 0
            ? number
            : throw new InvalidDataException($"PvP Filament field {name} is invalid.");
    }

    private static string RequiredString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"PvP Filament field {name} is missing or invalid.");

    private static DateOnly ReadDate(JsonElement row, string name)
    {
        var value = RequiredString(row, name);
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidDataException($"PvP Filament schedule date {value} is invalid.");
    }
}
