using Npgsql;

namespace Eve.FactionWarfare;

/// <summary>
/// The battlefield rules of factional warfare. The point timer (20 s), the point value (1), the capture range (30 km), the point types and the score
/// thresholds (150 each way) are the SDE's (`spacecomponentsbytype`: the fwCapturePoint and fwScoreboard components of types 73443 to 73461 and 73447 to
/// 73450). The victory points, the loyalty points and the spawn rules are the wiki's "Battlefields" (DEVIATIONS "Factional warfare: battlefields").
/// </summary>
public static class FwBattlefields
{
    /// <summary>The score that decides a battlefield, each way (SDE fwScoreboard attackerWinsThreshold 150, occupierWinsThreshold -150).</summary>
    public const long ScoreThreshold = 150;
    /// <summary>The score a point gives its holder each tick (SDE fwCapturePoint pointValue 1).</summary>
    public const int PointValue = 1;
    /// <summary>The hold time of one point tick (SDE fwCapturePoint numberOfSeconds 20).</summary>
    public const long PointTickMs = 20_000;
    /// <summary>The capture range of a point (SDE fwCapturePoint range 30000; the wiki's 30 km).</summary>
    public const double PointRangeMeters = 30_000;
    /// <summary>The distance of the two outer points from the central one (wiki: about 80 km either side).</summary>
    public const double PointSpacingMeters = 80_000;
    /// <summary>The point's ball radius (SDE radius 231 of the control zone types).</summary>
    public const double PointRadiusMeters = 231;
    /// <summary>The SDE group of the control zone types (4494) and of the scoreboard types (227).</summary>
    public const long PointGroupId = 4494;
    public const long ScoreboardGroupId = 227;
    public const int PointCount = 3;
    /// <summary>How many battlefields are open at once (wiki: up to two).</summary>
    public const int MaxOpen = 2;
    /// <summary>A new battlefield comes this long after one completes (wiki: three hours).</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(3);
    /// <summary>The victory points a battlefield pays the system (wiki: 2,000).</summary>
    public const long VictoryPoints = 2_000;
    /// <summary>The loyalty points a battlefield pays (wiki: up to 150,000), split evenly over the first <see cref="LpParticipants"/> winners.</summary>
    public const long LpPool = 150_000;
    public const int LpParticipants = 30;
    public const long LpEach = LpPool / LpParticipants;
    /// <summary>The first deployable id of the battlefield balls: each battlefield has <see cref="PointCount"/> points and a scoreboard.</summary>
    public const long IdBase = 4_300_000_000_000_000;

    public static long PointId(long battlefieldId, int index) => IdBase + battlefieldId * (PointCount + 1) + index;

    public static long ScoreboardId(long battlefieldId) => IdBase + battlefieldId * (PointCount + 1) + PointCount;

    /// <summary>The point's place: the centre of the battlefield, then one point about 80 km either side of it along the axis.</summary>
    public static double[] PointPosition(double[] origin, int index) => index switch
    {
        0 => [origin[0], origin[1], origin[2]],
        1 => [origin[0] + PointSpacingMeters, origin[1], origin[2]],
        _ => [origin[0] - PointSpacingMeters, origin[1], origin[2]],
    };

    private static readonly IReadOnlyDictionary<long, long[]> PointTypes = new Dictionary<long, long[]>
    {
        [500001] = [73445, 73858, 73859],
        [500002] = [73444, 73856, 73857],
        [500003] = [73443, 73854, 73855],
        [500004] = [73446, 73860, 73861],
    };

    private static readonly IReadOnlyDictionary<long, long> ScoreboardTypes = new Dictionary<long, long>
    {
        [500001] = 73449,
        [500002] = 73448,
        [500003] = 73447,
        [500004] = 73450,
    };

    /// <summary>The control zone type of a point, by the occupier of the battlefield (the SDE's "{Faction} Control Zone A, B, C").</summary>
    public static long PointTypeOf(long occupierId, int index) => PointTypes[occupierId][index];

    public static long ScoreboardTypeOf(long occupierId) => ScoreboardTypes[occupierId];

    public static string NameOf(long occupierId, int index) => $"{FactionName(occupierId)} Control Zone {"ABC"[index]}";

    public static string ScoreboardNameOf(long occupierId) => $"{FactionName(occupierId)} Scoreboard";

    private static string FactionName(long factionId) => factionId switch
    {
        500001 => "Caldari",
        500002 => "Minmatar",
        500003 => "Amarr",
        500004 => "Gallente",
        _ => throw new ArgumentOutOfRangeException(nameof(factionId), "not a warfare faction"),
    };

    /// <summary>
    /// The hold timer after one step: the time held adds to it, and each full tick gives one point (the rest is carried to the next step).
    /// </summary>
    public static (long HoldMs, int Ticks) Hold(long holdMs, long stepMs)
    {
        long total = holdMs + stepMs;
        return (total % PointTickMs, (int)(total / PointTickMs));
    }

    /// <summary>The score kept between the two thresholds.</summary>
    public static long Clamp(long score) => Math.Clamp(score, -ScoreThreshold, ScoreThreshold);

    /// <summary>The side that won (true: the attackers, false: the occupier), or null while the battle goes on.</summary>
    public static bool? Decided(long score) => score >= ScoreThreshold ? true : score <= -ScoreThreshold ? false : null;

    /// <summary>The pilots that get loyalty points: the first <see cref="LpParticipants"/> of the winners by character id (the rest get none).</summary>
    public static IReadOnlyList<long> LpRecipients(IEnumerable<long> winners) => winners.OrderBy(id => id).Take(LpParticipants).ToList();
}

/// <summary>A capture point of an open battlefield: its hold timer (ms into the current tick) and the side that held it last (0: none yet).</summary>
public sealed record FwBattlefieldPoint(int Index, long HoldMs, long HolderId);

/// <summary>An open battlefield: its system, the occupier (its defenders; the attackers are the occupier's enemy), its score and its points.</summary>
public sealed record FwBattlefield(long BattlefieldId, long SolarSystemId, long OccupierId, long Score, long UpdatedMs, IReadOnlyList<FwBattlefieldPoint> Points);

/// <summary>A point's ball as the simulation needs it: its id, type, name, place, and the next tick time (ms, 0 when none is running).</summary>
public sealed record FwBattlefieldPointState(long PointId, long TypeId, string Name, double[] Position, long NextTickMs);

/// <summary>A battlefield's balls and score after a tick. A closed battlefield is sent once more so its balls are taken out.</summary>
public sealed record FwBattlefieldState(long BattlefieldId, long SolarSystemId, long OccupierId, long AttackerId, long ScoreboardId, long ScoreboardTypeId,
    string ScoreboardName, long Score, IReadOnlyList<FwBattlefieldPointState> Points, bool Closed);

/// <summary>A battlefield won this tick: its winning faction, the victory points moved, and how many pilots got loyalty points.</summary>
public sealed record FwBattlefieldWon(long BattlefieldId, long SolarSystemId, long ScoreboardId, long WinnerId, long VictoryPoints, long AttackerPoints,
    int LpRecipients);

/// <summary>Pays the loyalty points of a battlefield in the caller's transaction (the host credits them through the loyalty store, with the corporation tax).</summary>
public interface IFwRewards
{
    Task CreditLpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long issuerCorpId, long amount, string reference,
        CancellationToken cancellationToken);
}

/// <summary>
/// One tick of the battlefields in one transaction. A battlefield spawns in an occupied system with none open (at most two open, and three hours after
/// the last completion). Its points are controlled by the pilots within range: only one side present holds the point and its hold timer runs; both sides
/// or none pause it. Each full tick of holding moves the score one point toward the holder's side; the battlefield is won at +/-150.
/// </summary>
public static class FwBattlefieldTicker
{
    public static async Task<(IReadOnlyList<FwBattlefieldState> States, IReadOnlyList<FwBattlefieldWon> Wins)> TickAsync(FwStore store, IFwComplexSites sites,
        IFwRewards rewards, NpgsqlConnection connection, NpgsqlTransaction transaction, long nowMs, CancellationToken cancellationToken)
    {
        var systems = await store.SystemsAsync(connection, transaction, cancellationToken);
        var open = (await store.OpenBattlefieldsAsync(connection, transaction, cancellationToken)).ToList();
        if (open.Count < FwBattlefields.MaxOpen && await SpawnAllowedAsync(store, connection, transaction, nowMs, cancellationToken)
            && systems.Where(system => open.All(battlefield => battlefield.SolarSystemId != system.SolarSystemId))
                .OrderBy(system => system.SolarSystemId).FirstOrDefault() is { } site)
        {
            long id = await store.InsertBattlefieldAsync(connection, transaction, site.SolarSystemId, site.OccupierId, nowMs, cancellationToken);
            open.Add(new FwBattlefield(id, site.SolarSystemId, site.OccupierId, 0, nowMs,
                Enumerable.Range(0, FwBattlefields.PointCount).Select(index => new FwBattlefieldPoint(index, 0, 0)).ToList()));
        }

        var states = new List<FwBattlefieldState>();
        var wins = new List<FwBattlefieldWon>();
        foreach (var battlefield in open.OrderBy(battlefield => battlefield.BattlefieldId))
        {
            long id = battlefield.BattlefieldId;
            long occupier = battlefield.OccupierId;
            long attacker = FwFactions.OccupationEnemyOf(occupier);
            var origin = await sites.OriginAsync(connection, transaction, battlefield.SolarSystemId, cancellationToken);
            long step = Math.Clamp(nowMs - battlefield.UpdatedMs, 0, (long)FwComplexes.Tick.TotalMilliseconds);
            long score = battlefield.Score;
            var points = new List<FwBattlefieldPointState>();
            foreach (var point in battlefield.Points.OrderBy(point => point.Index))
            {
                var at = FwBattlefields.PointPosition(origin, point.Index);
                var pilots = (await sites.PilotsNearAsync(battlefield.SolarSystemId, at, FwBattlefields.PointRangeMeters, cancellationToken))
                    .Where(pilot => pilot.WarFactionId == occupier || pilot.WarFactionId == attacker).ToList();
                foreach (var pilot in pilots)
                    await store.AddBattlefieldPilotAsync(connection, transaction, id, pilot.CharacterId, pilot.WarFactionId, cancellationToken);
                bool occupierPresent = pilots.Any(pilot => pilot.WarFactionId == occupier);
                bool attackerPresent = pilots.Any(pilot => pilot.WarFactionId == attacker);
                long holder = point.HolderId;
                long hold = point.HoldMs;
                long nextMs = 0;
                if (occupierPresent != attackerPresent)
                {
                    long side = attackerPresent ? attacker : occupier;
                    if (side != holder)
                    {
                        hold = 0;
                        holder = side;
                    }
                    var (heldMs, ticks) = FwBattlefields.Hold(hold, step);
                    hold = heldMs;
                    score = FwBattlefields.Clamp(score + ticks * FwBattlefields.PointValue * (side == attacker ? 1 : -1));
                    nextMs = nowMs + FwBattlefields.PointTickMs - hold;
                }
                await store.SaveBattlefieldPointAsync(connection, transaction, id, point.Index, hold, holder, cancellationToken);
                points.Add(new FwBattlefieldPointState(FwBattlefields.PointId(id, point.Index), FwBattlefields.PointTypeOf(occupier, point.Index),
                    FwBattlefields.NameOf(occupier, point.Index), at, nextMs));
            }

            long scoreboard = FwBattlefields.ScoreboardId(id);
            long scoreboardType = FwBattlefields.ScoreboardTypeOf(occupier);
            string scoreboardName = FwBattlefields.ScoreboardNameOf(occupier);
            var decided = FwBattlefields.Decided(score);
            if (decided is null)
            {
                await store.SaveBattlefieldAsync(connection, transaction, id, score, nowMs, cancellationToken);
                states.Add(new FwBattlefieldState(id, battlefield.SolarSystemId, occupier, attacker, scoreboard, scoreboardType, scoreboardName, score, points, false));
                continue;
            }

            // the win: the victory points move the system's attacker points, the winners' pilots share the points for their statistics,
            // and the first winners get the loyalty points (the winning side's militia issues them)
            long winner = decided.Value ? attacker : occupier;
            long victoryPoints = decided.Value ? FwBattlefields.VictoryPoints : -FwBattlefields.VictoryPoints;
            long attackerPoints = await store.AddAttackerPointsSignedAsync(connection, transaction, battlefield.SolarSystemId, victoryPoints, nowMs, cancellationToken);
            var winners = await store.BattlefieldPilotsAsync(connection, transaction, id, winner, cancellationToken);
            var recipients = FwBattlefields.LpRecipients(winners);
            foreach (long characterId in recipients)
                await rewards.CreditLpAsync(connection, transaction, characterId, FwFactions.MilitiaCorporationOf(winner), FwBattlefields.LpEach,
                    $"factionwarfare:battlefield:{id}:{characterId}", cancellationToken);
            long share = winners.Count == 0 ? 0 : Math.Abs(victoryPoints) / winners.Count;
            if (share > 0)
            {
                int day = FwRules.DayOf(nowMs);
                foreach (long characterId in winners)
                    await store.AddPilotDailyAsync(connection, transaction, characterId, day, 0, share, cancellationToken);
            }
            await store.CloseBattlefieldAsync(connection, transaction, id, winner, score, nowMs, cancellationToken);
            states.Add(new FwBattlefieldState(id, battlefield.SolarSystemId, occupier, attacker, scoreboard, scoreboardType, scoreboardName, score, points, true));
            wins.Add(new FwBattlefieldWon(id, battlefield.SolarSystemId, scoreboard, winner, victoryPoints, attackerPoints, recipients.Count));
        }
        return (states, wins);
    }

    private static async Task<bool> SpawnAllowedAsync(FwStore store, NpgsqlConnection connection, NpgsqlTransaction transaction, long nowMs,
        CancellationToken cancellationToken)
    {
        var lastClosed = await store.LastBattlefieldClosedMsAsync(connection, transaction, cancellationToken);
        return lastClosed is not { } closedMs || nowMs >= closedMs + (long)FwBattlefields.Cooldown.TotalMilliseconds;
    }
}
