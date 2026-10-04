# The shared foundations: use these, do not build your own

Six pieces of the server are shared by every feature. **A new feature (a mission, an anomaly, crimewatch, a structure, industry, fleet
boosts, …) must use them. It must not start its own timer, keep its own event list, keep its own table of running content, or add a
branch to a big switch.** That is what keeps the code small enough to change.

| I need to… | Use | Project |
|---|---|---|
| do something **later** and it must survive a restart (a deadline, a periodic task) | the **Durable Scheduler** | `Eve.Scheduler` |
| wait a moment for something that lives **in this process only** (a pilot's protection, a module reload) | **LocalTimers** | `Eve.Scheduler` |
| **tell the rest of the server** that something happened, or react to it | the **Event Bus** (transactional outbox) | `Eve.Events` |
| store "A has some relation to B" (standing, war, security status, corporation/alliance/faction membership) | the **Relation Store** | `Eve.Relations` |
| run something with **objectives** and a **deadline** (mission, anomaly, incursion, tutorial) | the **Content Runtime** | `Eve.Content` |
| make a module/item **do something each cycle** (an effect with code) | the **Effect Registry** | `Eve.Dogma` + `Eve.Services.Space/Effects` |
| answer a client call with a **list of records** from the SDE or PostgreSQL | the **Rowset Read Side** | `Eve.ReadSide` |

They sit on each other: events and relations use the scheduler; content uses both. All of them live in PostgreSQL (one schema each,
see [Where they live](#where-they-live)), so a restart loses nothing.

## The rules that make them work

1. **Write your change and its announcement in one transaction.** Open one `NpgsqlTransaction`; do your update; `PublishAsync` the event
   and `ScheduleAsync` the job *with that connection and transaction*; commit. The event and the job then exist exactly when your change
   does. Never "update, then publish" as two steps: a crash between them loses the event.
2. **Handlers run inside a transaction too.** A job handler and an event handler get `Connection`/`Transaction`. What they write through
   those is committed together with "this job is done" / "this event is handled". A handler that only writes to PostgreSQL therefore
   runs **exactly once**. A handler that also touches something outside PostgreSQL (the simulation, a socket) runs **at least once**:
   make it safe to run twice.
3. **Throwing means "try again later".** The scheduler and the bus retry with a back-off; after the last try a job is parked as `failed`
   and an event is set aside as a dead letter (the subscriber goes on). Do not catch and swallow an error you want retried.
4. **Keep transactions that publish short.** A publishing transaction holds a shared advisory lock until it commits and the pump does not
   deliver past an open publisher, so a publishing transaction that stays open holds back every event after it. (Transactions that do not
   publish, anywhere in the database, delay nothing.)
5. **Time is injected.** Every foundation takes the clock from `DurableScheduler.Now` (milliseconds since the Unix epoch). Tests move a
   fake clock; nothing calls `DateTime.UtcNow` for game time.
6. **Names are contracts.** A job *kind* (`space.bounty.pay`), an event *topic* (`npc.killed`), a subscriber *name*, a relation *kind*
   (`standing`) and a content *definition id* are stored in the database. Choose them once, `lower.dotted`, and never rename one without
   a migration.

---

## 1. Durable Scheduler (`Eve.Scheduler`)

A job is a row: *kind* (which handler), optional *key* (what it is about), *payload* (JSON), *due time*. A runner claims due jobs
(`FOR UPDATE SKIP LOCKED`, with a lease), runs the handler, retries failures, and deletes the job when it is done.

**Register a handler once, at start-up** (the host starts the runner after everything has registered):

```csharp
scheduler.Register("anomaly.despawn", async (job, ct) =>
{
    var p = job.Payload<Despawn>()!;                       // record Despawn(long SystemId, long SiteId)
    await sites.RemoveAsync(job.Connection, job.Transaction, p.SiteId, ct);   // joins the job's transaction
});
```

**Schedule a job** — in the transaction of the change it belongs to:

```csharp
await using var c = await source.OpenConnectionAsync();
await using var tx = await c.BeginTransactionAsync();
var site = await sites.CreateAsync(c, tx, systemId);
await scheduler.ScheduleAsync(c, tx, new JobSpec("anomaly.despawn", scheduler.Now + 3_600_000)
{
    Key = $"site:{site.Id}",                               // one waiting job per (kind, key)
    Payload = new Despawn(systemId, site.Id),
});
await tx.CommitAsync();
scheduler.Wake();                                          // optional: run it without waiting for the next poll
```

What you get:

| You want | Do |
|---|---|
| restart or replace a timer | schedule again with the same `Key` (default `OnDuplicate.Replace`) |
| "make sure it exists" at start-up | `OnDuplicate.Keep`, or `EnsureRecurringAsync(kind, key, every)` |
| only ever pull a deadline in / push it out | `OnDuplicate.Earlier` / `OnDuplicate.Later` |
| cancel it | `CancelAsync(kind, key)` (or the overload with your connection and transaction) |
| a periodic task | `JobSpec.EveryMs`, or `EnsureRecurringAsync`. Missed runs are skipped, not replayed; a periodic job that fails is retried at its next time and never parked |
| a state machine whose next step is a time | in the handler call `job.RunAgainAt(time, newPayload)` |
| follow-up work | in the handler `await job.ScheduleAsync(spec)` (same transaction) |
| look at / revive failed work | `ListAsync(kind, "failed")`, `RetryAsync(id)` |

**Do not** create `System.Threading.Timer`, `PeriodicTimer`, `Task.Delay` loops or `Task.Run` polling for game time. Timers that are
still in the code are listed under [Migrating existing timers](#migrating-existing-timers).

**Test it** with a fake clock and no background loop: `await scheduler.RunDueAsync()` runs everything that is due now.
See `dotnet/tests/Eve.Scheduler.Tests/Rig.cs`.

### LocalTimers: the in-memory half of the scheduler

Some waits belong to something that exists only in this process: a pilot's undock protection, a module that is reloading, the end of the skill
queue of a character who is logged in. They need no database row (when the process stops the pilot, the module and the session are gone too),
so they use `LocalTimers`, the server's one in-memory timer service:

```csharp
_timers.Set($"space.protection:{pilotId}", TimeSpan.FromSeconds(30), () => EndProtection(pilot));   // replaces a timer of the same key
_timers.Cancel($"space.protection:{pilotId}");                                                     // the pilot ended it first
```

An action that throws is logged and does not stop anything; `Dispose()` stops all timers; a test passes a delay it controls
(`new LocalTimers(delay: fakeDelay)`). **Rule of thumb:** it must survive a restart or be seen by another process → `DurableScheduler`;
it concerns a live object of this process only → `LocalTimers`. Either way, no `System.Threading.Timer` and no `Task.Delay` loop.

## 2. Event Bus with a transactional outbox (`Eve.Events`)

An event is a row in the outbox, written in the publisher's transaction. The bus's *pump loop* (started with the foundations) delivers
each event, in order, to every subscriber whose topics match, within a quarter of a second. Each subscriber has its own cursor.

**Publish** in the same transaction as the change:

```csharp
await bus.PublishAsync(c, tx, "npc.killed", new NpcKilled(characterId, typeId, solarSystemId), key: $"char:{characterId}");
```

**Subscribe** once at start-up (before `Foundations.StartAsync`). The name is the subscriber's identity: never rename it.

```csharp
bus.Subscribe("bounty.credit", ["npc.killed"], async (ctx, ct) =>
{
    var k = ctx.Event.Payload<NpcKilled>()!;
    await bounties.AddAsync(ctx.Connection, ctx.Transaction, k.CharacterId, k.TypeId, ct);   // exactly once
    await ctx.PublishAsync("bounty.added", new { characterId = k.CharacterId });               // joins the same transaction
});
```

Topics: an exact name (`npc.killed`), a prefix with a star (`npc.*`), or `*`. Topic names are `domain.event_in_past_tense`. Payloads are
plain records (camel-case JSON): keep them small and put ids in them, not objects.

Behaviour you can rely on: events of a subscriber arrive in publish order; a failing event blocks only that subscriber (back-off, then
a dead letter after `MaxAttempts`, then it goes on); `StartAt.Now` (default) means a new subscriber sees only new events,
`StartAt.Beginning` replays what the log still holds; read events are purged after a day.

**Do not** add `event Action<…>` members to a service for another feature to hook (the existing ones in `SpaceSessions` are being moved:
see [Migrating](#migrating-existing-timers)), and do not keep a list of listeners.

**Test it:** `await bus.PumpAsync()` delivers what is waiting (call it once after creating the subscribers so they start "now"; do not start
the loop in a test). See `dotnet/tests/Eve.Events.Tests`. If you publish with your own transaction, call `bus.Wake()` after the commit to
have it delivered at once instead of at the next poll.

## 3. Relations (`Eve.Relations`)

One table for every "subject has a relation to object": kind, subject id, object id, a number, free attributes, valid from/until.

| Kind (`RelationKinds.*`) | subject → object | value |
|---|---|---|
| `standing` | owner → owner | −10 … 10 |
| `security_status` | character → `RelationStore.World` (0) | −10 … 10 |
| `war` | aggressor → defender | attributes: mutual, open for allies…; `until` is the end of a retracted war |
| `war.ally` | ally → id of the `war` relation | attributes say which side |
| `affiliation.corporation` | character → corporation | one per subject |
| `affiliation.alliance` | corporation → alliance | one per subject |
| `affiliation.faction` | corporation / alliance / character → faction | one per subject |

```csharp
await relations.SetAsync(c, tx, "standing", characterId, corporationId, 3.5, reason: "mission completed");   // clamped to the kind's range
await relations.AdjustAsync(c, tx, "security_status", characterId, RelationStore.World, -0.5, reason: "killed a pilot");
await relations.SetAsync(c, tx, "war", aggressor, defender, 0, "declared", attrs: new { mutual = false }, untilMs: endsAt);  // ends by itself
var best = await relations.BestAsync("standing", await relations.ChainOfAsync(characterId), [agentCorporation]);
var atWar = await relations.AtWarAsync(await relations.ChainOfAsync(a), await relations.ChainOfAsync(b));
```

Every write keeps a history line and publishes `relation.<kind>.changed` or `relation.<kind>.ended` on the bus. React to those instead of
polling: for example crimewatch subscribes to `relation.war.*`. A relation with `untilMs` is ended by a scheduler job (`expired`).

A new kind of relation is `relations.Define(new RelationKind("name") { Min, Max, Cardinality })` (or an entry in `RelationKinds`), never a
new table. How values combine (best of the chain, worst, skill bonuses) is the feature's rule; `BestAsync` is the common one.

## 4. Content / Objectives Runtime (`Eve.Content`)

For anything with objectives and maybe a deadline. You write a **definition**; the runtime keeps the instances, advances objectives
from events, expires them, and announces how they ended.

```csharp
content.Define(new ContentDefinition("mission", "kill-the-pirates", [
    new ObjectiveDef("kill", "counter", new { topic = "npc.killed", where = new { typeId = new[] { 100, 101 } } }, Target: 5),
    new ObjectiveDef("report", "manual"),
]) { DurationMs = 3_600_000, OnFail = [new ActionRef("emit", new { topic = "mission.penalty", payload = new { standing = -0.5 } })] });
// at start-up, after all Define/RegisterObjectiveType/RegisterAction calls:   await content.AttachAsync();   (Foundations.StartAsync does it)

var started = await content.StartAsync(c, tx, "kill-the-pirates", characterId, data: new { agentId }, uniqueKey: $"agent:{agentId}:{characterId}");
await content.ProgressAsync(c, tx, started.Id, "report");     // a "manual" objective: the player talked to the agent
```

Built-in objective types: `counter` (counts events of a topic; `where` filters the payload, `"$owner"` and `"$instance"` stand for the
content's owner and id, `amountField` adds a number from the event, `scopeField` names the payload field that identifies the owner,
default `characterId`), `forbid` (an event fails the content), `manual`, `timer` (`delayMs`). Anything else is a small
`ObjectiveType` subclass registered with `RegisterObjectiveType`.

Order is `Sequential` (default) or `Parallel` (`Required: false` marks optional objectives). Use `owner` = the character, corporation,
system or fleet it belongs to; for an anomaly the owner is the solar system and its kills carry `solarSystemId` and the instance id.

When content ends, the runtime publishes `content.<kind>.completed|failed|expired|abandoned` (and `.started`, `.objective_completed`). **Pay
rewards and apply consequences in a subscriber to those topics** (or an `ActionRef`), in the same transaction, so a mission, an anomaly
and an incursion all end the same way and none of them keeps its own list of what is running.

## 5. Dogma Effect Registry (`Eve.Dogma`, `Eve.Services.Space/Effects`)

Static data describes *modifiers* (a skill gives +5 % to …); the engine in `Eve.Dogma` applies those. What a module *does* each cycle
(shoot, repair, web, jam, burn the afterburner) is code, and it is a **handler** found by effect name:

```csharp
internal sealed class MyEffect : ShipEffect                       // Eve.Services.Space/Effects
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["myEffectName"];     // effectName of the static data
    protected override string? Refusal(ModuleEffectContext m) => /* "NO_AMMO" … */ null;     // optional, before the target check
    protected override SimEffect Build(ModuleEffectContext m) { var (duration, capacitor, durationAttr) = m.Cycle(); … }
}
// register it in ModuleEffects.Default
```

* One more **projected** effect (something done to the locked target each cycle) is one line in the table of `ProjectedEffect`.
* Several handlers may share a name; `Handles(context)` decides by the item (a turret's `targetAttack` and a drone's).
* `ModulePackages.Of` keeps only what every effect needs (module exists, is online, a target when required). Do not put effect logic there.
* **What is not done yet** is a list, not a guess: `ModuleEffects.Default.Uncovered(statics)` returns the activation and target effects of
  the static data that have no handler. `ModuleEffectsTests` fails if a handler names an effect that does not exist.
* The generic mechanism (`EffectRegistry<THost, TResult>`, `IEffectHandler`) is in `Eve.Dogma` and knows nothing about space; a host
  with another result type (structures, drones) makes its own registry.

## 6. Rowset Read Side (`Eve.ReadSide`)

When a client call returns a list of records, declare the query and ask for the shape; do not write loops that build `PyValue`s.

```csharp
reads.Register(RowsetQuery.Sde("shipGroups", "groups",
        Column.Key("groupID", ColumnType.Int), Column.English("groupName", "name"))
    with { Where = "(doc->>'categoryID')::int = $1" });

var rows = await reads.RowsAsync("shipGroups", [6]);
return rows.ToRowset();          // or ToPackedRows(), ToIndexedRowset(), ToKeyValsByKey(), ToKeyVal()
```

* `RowsetQuery.Sde(...)` reads a table of the static data (`sde.<table>`; fields come from the record's JSON with `Column.Field(name,
  type, "path", "to", "field")`) and caches the result, because the static data does not change while the server runs.
* For a module's own tables, write `new RowsetQuery("openOrders", "{market}.orders", [Column.Of("orderID", ColumnType.Long, "id"), …])
  { Where = "owner_id = $1" }` and give the reader the schema: `new ReadSide(source, new Dictionary<string,string>{["market"]="market"})`.
  Never cache what players change.
* Every column is cast to its type, so the reader does not guess; the value of a request is always a `$n` parameter, never text in the SQL.
* `ColumnType` carries the client's DBTYPE (`Int` 3, `Long` 20, `Real` 5, `Text` 130, `Bool` 11, `FileTime` 64). A null becomes `None`.

---

## A feature, end to end: an anomaly

1. `content.Define(new ContentDefinition("anomaly", "combat-site-1", [counter on npc.killed where site == "$instance", scopeField solarSystemId]) { DurationMs = … })`.
2. The director (a scheduler job `anomaly.spawn`, recurring) starts instances with `uniqueKey: "system:{id}:slot:{n}"` and spawns the NPCs,
   with the instance id in their identity.
3. The simulation reports kills; the host publishes `npc.killed { solarSystemId, site, count }` in the transaction that records the kill.
4. The content runtime advances the objective. When it completes it publishes `content.anomaly.completed`; a subscriber `anomaly.cleanup`
   removes what is left and schedules the respawn (`ScheduleAsync`, key `slot:{n}`).
5. If nobody clears it, the runtime's own deadline job publishes `content.anomaly.expired`; the same subscriber cleans up.

No timer, no event list, no table of running anomalies was written for this.

## Where they live

| Foundation | PostgreSQL schema | Environment variable | Class |
|---|---|---|---|
| Scheduler | `scheduler` | `EVE_SCHEDULER_SCHEMA` | `DurableScheduler` |
| Event bus | `events` | `EVE_EVENTS_SCHEMA` | `EventBus` |
| Relations | `relations` | `EVE_RELATIONS_SCHEMA` | `RelationStore` |
| Content | `content` | `EVE_CONTENT_SCHEMA` | `ContentRuntime` |

`Eve.Host/Foundations.cs` creates them and `Program.cs` hands them to the features (`CharacterServicesAsync(…, foundations)`). Take what
you need as a constructor argument. Register handlers and subscribers **before** `foundations.StartAsync()`.

Each project follows the module rules (ADR 0003): its own schema, embedded SQL migrations that are never edited once released, an
injected clock, and a test project with a scratch schema (`PgFact` skips without `EVE_PG_URL`; look at `Rig.cs` in each test project).

## What has been moved, and what stays

Moved onto the foundations: the can and wreck sweeper (`space.containers.sweep`) and the NPC bounty payout (`space.bounty.pay`) are recurring jobs of the
scheduler; the undock protection and jump cloak of a pilot (`SpaceSessions`), the skill-queue announcement (`TrainingAnnouncer`) and a module's reload
(`DogmaIMService`) are `LocalTimers`; the module effects are handlers of the registry; `SpaceSessions` announces `npc.killed`, `ship.lost` and
`bounty.paid` on the event bus (the C# events `NpcDestroyed`, `ShipLost`, `BountyPaidOut` remain for a synchronous call; new code subscribes to the bus).

Deliberately **not** moved (core processing with its own design): the game store's write-behind flush and lease renewal (`Eve.GameStore`), the
simulation link's reconnect loop (`SimClient`), the ordered per-session ammunition write queue (`ChargeSpentQueue`), the retry loops of the market and
corporation transactions (serialisation retries, not game time), and everything in the Rust simulation (tick, destiny, behaviours). The startup
catalog loads from the SDE (`StationCatalog`, `DogmaStatic`, `CreationCatalog`…) read typed documents into memory once; the read side is for the
calls that return lists of records to the client.

Still to move when their features are next touched (do not add new ones): the durable Alliance transitions and war holds of the corporation module
(→ scheduler jobs and `war`/`affiliation.*` relations), and the corporation, alliance and character affiliation tables (→ `affiliation.*` relations, with
`relation.affiliation.*.changed` events replacing `AffiliationChangedAsync`).

## Checklist for a pull request

* No `Timer`, `PeriodicTimer`, `Task.Delay` loop or `Task.Run` poll for game time. → `DurableScheduler` (survives a restart) or `LocalTimers` (this process only).
* No new `event`/callback for another feature to hook. → bus.
* No new table that is "A and B with a number". → relation kind.
* No new table of "running things with goals and a deadline". → content definition.
* No new `if (effect.Name == …)` for a module. → effect handler.
* No hand-built rowset loop. → `RowsetQuery`.
* A change plus its event/job is **one** transaction. A handler that can run twice is idempotent.
* Tests use the fake clock (`RunDueAsync`, `PumpAsync`) and mutate-check the rule that matters.
