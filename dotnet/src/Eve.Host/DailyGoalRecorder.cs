using Eve.Content;
using Eve.Events;
using Eve.Scheduler;
using Eve.Ships;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The daily goals of mining and salvage (EVE.js <c>dailyGoalsCatalog.js</c>: "Mine 2000 units of Ore", "Salvage 5 Wrecks"), as content. A pilot has one goal of each
/// kind for each period, and a period runs from 11:00 UTC to 11:00 UTC (<c>DOWNTIME_HOUR_UTC</c> and <c>currentPeriod</c> in <c>dailyGoalsState.js</c>). The subscriber
/// <c>mining.daily_goal</c> takes the fact of an ore cycle (ore only, as EVE.js counts it) or of a wreck salvaged, and adds the amount to the goal in its own
/// transaction, so it counts once with the subscriber's cursor. No feature hooks the mining or salvage path.
/// </summary>
/// <remarks>
/// Deviation (DEVIATIONS, mining tracks): EVE.js offers each pilot a rotation of goals each day; here both goals run every day for every pilot.
/// </remarks>
public sealed class DailyGoalRecorder(ContentRuntime content, DurableScheduler scheduler)
{
    public const string Kind = "dailyGoal";
    public const string MineOre = "mine_ore";
    public const string SalvageWrecks = "salvage_wrecks";
    public const string SubscriberName = "mining.daily_goal";

    /// <summary>"Mine 2000 units of Ore" (the industrialist pool, dailyGoalsCatalog.js).</summary>
    public const double MineOreTarget = 2000;

    /// <summary>"Salvage 5 Wrecks" (the industrialist pool, dailyGoalsCatalog.js).</summary>
    public const double SalvageWrecksTarget = 5;

    private const int DowntimeHourUtc = 11;
    private const long DayMs = 24L * 60 * 60 * 1000;

    /// <summary>The goals, to define on the content runtime before it attaches. Both are manual: this recorder advances them, so no topic is subscribed twice.</summary>
    public static IReadOnlyList<ContentDefinition> Definitions { get; } =
    [
        new(Kind, MineOre, [new ObjectiveDef(MineOre, "manual", null, MineOreTarget)]),
        new(Kind, SalvageWrecks, [new ObjectiveDef(SalvageWrecks, "manual", null, SalvageWrecksTarget)]),
    ];

    /// <summary>The period a moment falls in: from the last downtime to the next one (<c>currentPeriod</c>).</summary>
    public static (long AfterMs, long UntilMs) PeriodAt(long nowMs)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime.Date;
        var boundary = new DateTimeOffset(day.AddHours(DowntimeHourUtc), TimeSpan.Zero).ToUnixTimeMilliseconds();
        return nowMs >= boundary ? (boundary, boundary + DayMs) : (boundary - DayMs, boundary);
    }

    public async Task RecordAsync(EventContext context, CancellationToken cancellationToken)
    {
        string goal;
        long owner;
        double amount;
        if (context.Event.Topic == OreMinedEvent.Topic)
        {
            var ore = context.Event.Payload<OreMinedEvent>()
                ?? throw new InvalidDataException($"mining.ore_mined event {context.Event.Id} has no payload");
            if (ore.YieldKind != "ore" || ore.Quantity <= 0)
            {
                return;
            }
            (goal, owner, amount) = (MineOre, ore.CharacterId, ore.Quantity);
        }
        else if (context.Event.Topic == SalvageWreckedEvent.Topic)
        {
            var wreck = context.Event.Payload<SalvageWreckedEvent>()
                ?? throw new InvalidDataException($"salvage.wreck_salvaged event {context.Event.Id} has no payload");
            (goal, owner, amount) = (SalvageWrecks, wreck.CharacterId, 1);
        }
        else
        {
            return;
        }

        var (afterMs, untilMs) = PeriodAt(context.Event.OccurredMs);
        var uniqueKey = $"{goal}:{owner}:{afterMs}";
        var id = await content.FindAsync(context.Connection, context.Transaction, Kind, uniqueKey, cancellationToken)
            ?? (await content.StartAsync(context.Connection, context.Transaction, goal, owner, null, uniqueKey, Math.Max(1, untilMs - scheduler.Now), cancellationToken)).Id;
        await content.ProgressAsync(context.Connection, context.Transaction, id, goal, amount, cancellationToken);
    }
}
