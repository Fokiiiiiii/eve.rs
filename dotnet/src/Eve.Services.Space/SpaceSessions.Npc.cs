using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>The item id of the first hull of <c>/deathtest</c>: the reference numbers its transient ships from about here (the ids of the items in the database are far below).</summary>
    private const long HullIdBase = 4_200_000_000_000_000;

    /// <summary>How far from the pilot the hulls of <c>/deathtest</c> come, how soon they blow up, how many there are when the pilot does not say (the reference's).</summary>
    public const double DeathTestRadiusMeters = 20_000;
    public const double DeathTestDelaySeconds = 2.0;

    private long _hulls;
    private ulong _spawnRequests;

    /// <summary>
    /// <c>buildRandomDirection</c> of the reference's <c>shipDestruction.js</c> (a direction near the base direction, turned by a random angle and lifted a little),
    /// with the base direction along x: the simulation, not the host, knows which way the pilot's ship faces, and where the hulls come is random anyway.
    /// </summary>
    private static double[] RandomDirection()
    {
        var angle = Random.Shared.NextDouble() * Math.PI * 2;
        var vertical = (Random.Shared.NextDouble() - 0.5) * 0.3;
        double[] direction = [1 + Math.Cos(angle), vertical, Math.Sin(angle)];
        var length = Math.Sqrt(direction.Sum(x => x * x));
        return length > 0 && double.IsFinite(length) ? [.. direction.Select(x => x / length)] : [1, 0, 0];
    }

    /// <summary><c>buildShipDeathPositions</c>: places 3 km to <paramref name="radius"/> from the anchor, no two within 1.5 km (48 tries each).</summary>
    private static List<double[]> HullPlaces(int count, double radius)
    {
        var places = new List<double[]>();
        var reach = Math.Max(3_000, radius);
        for (var index = 0; index < count; index++)
        {
            double[]? accepted = null;
            for (var attempt = 0; attempt < 48 && accepted is null; attempt++)
            {
                var direction = RandomDirection();
                var offset = 3_000 + Random.Shared.NextDouble() * Math.Max(0, reach - 3_000);
                double[] candidate = [direction[0] * offset, direction[1] * offset, direction[2] * offset];
                var collides = places.Any(other => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(other[i] - candidate[i], 2))) < 1_500);
                if (!collides)
                {
                    accepted = candidate;
                }
            }
            if (accepted is null)
            {
                break;
            }
            places.Add(accepted);
        }
        return places;
    }

    public async Task<HullField?> DeathTestAsync(ClientSession session, int? shipTypeId, int count, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        var snapshot = await _ships.LoadAsync(pilot.CharacterId, cancellationToken);
        var typeId = shipTypeId ?? snapshot?.Ship.TypeId ?? 0;
        // the wreck is the caller's to loot, as the reference has it
        var credit = snapshot is { } flown
            ? new SimCredit(flown.Character.Id, flown.Character.CorporationId, flown.Character.AllianceId ?? 0, flown.Character.FactionId ?? 0)
            : new SimCredit(pilot.CharacterId, 0, 0, 0);
        await _statics.EnsureTypesAsync([typeId], cancellationToken);
        if (_statics.Type(typeId) is not { } type)
        {
            return null;
        }
        var spawned = new List<long>();
        foreach (var place in HullPlaces(count, DeathTestRadiusMeters))
        {
            var shipId = HullIdBase + Interlocked.Increment(ref _hulls);
            var spawn = new SimSpawn(
                NextSpawnRequest(), pilot.SolarSystemId, SimShips.OfType(shipId, type), pilot.ShipId, place, [0, 0, 0], RandomDirection(), "",
                SimNpcIdentity.Default with { Hull = true });
            if (await SpawnAsync(spawn, "deathtest", cancellationToken))
            {
                spawned.Add(shipId);
            }
        }
        if (spawned.Count == 0)
        {
            return null;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(DeathTestDelaySeconds));
            foreach (var shipId in spawned)
            {
                _sim.Destroy(shipId, credit);
            }
        }, CancellationToken.None);
        return new HullField(type.Name, spawned.Count, DeathTestRadiusMeters, DeathTestDelaySeconds);
    }

    private ulong NextSpawnRequest() => Interlocked.Increment(ref _spawnRequests);

    /// <summary>Asks the simulation to make a ship that nobody flies; false when it refused (the reason is logged), a message for the client when it cannot be reached.</summary>
    private async Task<bool> SpawnAsync(SimSpawn spawn, string what, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _sim.SpawnAsync(spawn, cancellationToken);
            if (reply.Session is not null)
            {
                return true;
            }
            _log?.Invoke($"{what}: the ship was refused: {reply.Error}");
            return false;
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"{what}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
    }

    // ── NPCs ─────────────────────────────────────────────────────────────────

    /// <summary>The item id of the first NPC ship of <c>/npc</c> (transient, like the hulls of <c>/deathtest</c>, below the ids the simulation gives wrecks).</summary>
    private const long NpcIdBase = 4_100_000_000_000_000;

    /// <summary>The tree an NPC of <c>/npc</c> runs (the simulation's <c>BELT_RAT</c>, deviation 46): it waits until a ship comes within its range and then fights it.</summary>
    public const string NpcBehavior = "belt rat";

    /// <summary>The standings at which an NPC treats others as enemies or as friends (the reference gives every NPC these limits).</summary>
    private const long NpcHostileThreshold = -11;
    private const long NpcFriendlyThreshold = 11;

    /// <summary>How many ships an NPC can hold locked at least (the reference's <c>MIN_NATIVE_NPC_LOCK_SLOTS</c>).</summary>
    private const int NpcMinimumLockSlots = 1;

    private long _npcs;

    /// <summary>The NPCs that can be spawned (null when there are none).</summary>
    public NpcSelector? Npcs { get; init; }

    /// <summary>The rats of the asteroid belts a pilot arrives at (null: no belt has any).</summary>
    public BeltRats? BeltRats { get; init; }

    /// <summary>
    /// What <c>spreadMeters</c> of a batch of belt rats is in the reference (the chance either way of how far from the belt each comes): not the default one, which grows with the size of the batch.
    /// </summary>
    private const double BeltRatSpreadMeters = 8_000;

    private readonly SemaphoreSlim _beltGate = new(1, 1);
    private Task? _beltHulls;
    private long _beltGroups;

    /// <summary>What an NPC leaves when it is destroyed: the host makes its wreck with what its loot table gave (null: the scene makes an empty wreck).</summary>
    public NpcWrecks? NpcWrecks { get; init; }

    /// <summary>What the host knows of an NPC it made: what it was worth, what it was and where (the simulation tells it only that the ship is gone).</summary>
    private sealed record NpcShip(NpcProfile Profile, int TypeId, int SolarSystemId, long BeltId = 0, long Group = 0, long SiteId = 0);

    private readonly ConcurrentDictionary<long, NpcShip> _npcShips = new();

    // ── bounties ─────────────────────────────────────────────────────────────

    private PgBountyStore? _bounties;
    private int _paying;

    /// <summary>Raised for each bounty that was paid into a wallet (the wallet's owner is told of its new balance by whoever announces wallets).</summary>
    public event Action<BountyPaid>? BountyPaidOut;

    /// <summary>
    /// Keeps the bounties NPCs are worth: a kill whose last blow was a pilot's goes to <paramref name="store"/>. What has come due is paid by <see cref="PayBountiesAsync"/>, which the
    /// host runs as a recurring job of the durable scheduler (<c>space.bounty.pay</c>); a test calls it.
    /// </summary>
    public void UseBounties(PgBountyStore store) => _bounties = store;

    /// <summary>The Encounter Surveillance Systems: a bounty earned where one is online pays its Main Bank share into it.</summary>
    public PgEssStore? Ess { get; set; }

    /// <summary>The sessions of the pilots in a solar system, for what the system is told (the ESS's changes, its thefts).</summary>
    public IReadOnlyList<ClientSession> PilotSessionsIn(int solarSystemId) =>
        [.. _pilots.Values.Where(pilot => pilot.SolarSystemId == solarSystemId).Select(pilot => pilot.Session)];

    /// <summary>Whether the character flies in the solar system now.</summary>
    public bool IsPilotIn(long characterId, int solarSystemId) =>
        _pilots.Values.Any(pilot => pilot.CharacterId == characterId && pilot.SolarSystemId == solarSystemId);

    /// <summary>
    /// The pilot of a character in a solar system as the simulation sees it, for an ESS link: its ship, whether the ship is one that may link (the SDE's type
    /// list 231), and where the ship is from the ESS ball. Null when the character is not in the system, or the simulation does not say where its ship is.
    /// </summary>
    public async Task<EssHolder?> EssHolderAsync(int solarSystemId, long characterId, CancellationToken cancellationToken)
    {
        if (_pilots.Values.FirstOrDefault(pilot => pilot.CharacterId == characterId && pilot.SolarSystemId == solarSystemId) is not { } pilot)
        {
            return null;
        }
        var session = pilot.Session;
        var shipId = ShipIdOf(session);
        var typeId = ShipTypeIdOf(session);
        if (shipId is not > 0 || typeId is not > 0)
        {
            return null;
        }
        if (await PositionOfAsync(session, shipId.Value, cancellationToken) is not { } position)
        {
            return null;
        }
        var linkable = await _statics.TypeListNamesAsync(EssRules.LinkableShipsTypeList, typeId.Value, cancellationToken);
        var dx = position.X - EssRules.BallX;
        var dy = position.Y - EssRules.BallY;
        var dz = position.Z - EssRules.BallZ;
        return new EssHolder(true, linkable, Math.Sqrt(dx * dx + dy * dy + dz * dz), shipId.Value);
    }

    /// <summary>Pays the bounties that are due (one payout at a time); each is raised as <see cref="BountyPaidOut"/>.</summary>
    public async Task<IReadOnlyList<BountyPaid>> PayBountiesAsync(CancellationToken cancellationToken = default)
    {
        if (_bounties is not { } store || Interlocked.Exchange(ref _paying, 1) == 1)
        {
            return [];
        }
        try
        {
            var paid = await store.PayDueAsync(_nowUnixMs(), cancellationToken);
            foreach (var payment in paid)
            {
                _log?.Invoke($"bounty paid: {payment.Amount} ISK for {payment.Kills} kill(s) to character {payment.CharacterId}");
                BountyPaidOut?.Invoke(payment);
                Announce("bounty.paid", new { characterId = payment.CharacterId, amount = payment.Amount, kills = payment.Kills }, $"char:{payment.CharacterId}");
            }
            return paid;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the bounties could not be paid: {error.Message}");
            return [];
        }
        finally
        {
            Volatile.Write(ref _paying, 0);
        }
    }

    /// <summary>
    /// An NPC of ours was destroyed: when the last blow was a pilot's ship, its bounty goes to that pilot's character and the client is told what was added and
    /// when it will be paid (<c>OnBountyAddedToPayout</c>).
    /// </summary>
    private async Task FinishNpcDeathAsync(NpcShip npc, SimDestroyed destroyed)
    {
        var killer = _pilots.Values.FirstOrDefault(pilot => pilot.ShipId == destroyed.KillerShipId);
        await LeaveNpcWreckAsync(npc, destroyed, killer);
        try
        {
            await PayNpcBountyAsync(npc, destroyed, killer);
        }
        finally
        {
            var kill = new NpcKill(killer?.CharacterId ?? 0, npc.TypeId, npc.Profile, npc.SolarSystemId, destroyed.ShipId);
            lock (_npcDestroyedGate)
            {
                Raise(NpcDestroyed, kill, nameof(NpcDestroyed));
                Announce("npc.killed", new { characterId = kill.FinalBlowCharacterId, typeId = kill.NpcTypeId, profile = kill.Profile.Id, solarSystemId = kill.SolarSystemId, shipId = kill.ShipId, site = npc.SiteId == 0 ? (long?)null : npc.SiteId }, $"system:{kill.SolarSystemId}");
            }
        }
    }

    private async Task PayNpcBountyAsync(NpcShip npc, SimDestroyed destroyed, Pilot? killer)
    {
        if (_bounties is not { } store || killer is null || NpcBounty.AmountOf(npc.Profile.Bounty) is not { } amount)
        {
            return;
        }
        // in a system with an ESS the Main Bank takes its share of the bounty; the share commits with the pilot's bucket or not at all
        var essOnline = Ess is not null && await Ess.IsOnlineAsync(npc.SolarSystemId);
        var (pilotShare, mainShare, reserveShare) = EssRules.SplitBounty(amount, essOnline);
        var essShare = mainShare + reserveShare;
        var now = _nowUnixMs();
        if (pilotShare <= 0)
        {
            await Ess!.CreditAsync(npc.SolarSystemId, mainShare, reserveShare);
            return;
        }
        BountyAdded added;
        try
        {
            added = await store.RecordAsync(
                new BountyKill(killer.CharacterId, npc.SolarSystemId, npc.TypeId, destroyed.ShipId, pilotShare), now,
                inTransaction: essShare > 0 && Ess is { } ess ? (connection, transaction, token) => ess.CreditBountyShareAsync(connection, transaction, npc.SolarSystemId, mainShare, reserveShare, token) : null);
        }
        catch (InvalidOperationException) when (essShare > 0)
        {
            // the ESS went away between the check and the credit: the pilot takes the whole bounty, as in a system without one
            added = await store.RecordAsync(new BountyKill(killer.CharacterId, npc.SolarSystemId, npc.TypeId, destroyed.ShipId, amount), now);
        }
        if (!added.AlreadyRecorded)
        {
            // `util.KeyVal`; the payout time is a FILETIME, past what a JavaScript number holds, so it goes as a long
            killer.Session.SendNotification(
                "OnBountyAddedToPayout", "charid",
                Py.KeyVal(
                    ("enemyTypeID", new PyInt(added.EnemyTypeId)), ("amount", new PyInt((int)added.Amount)), ("payoutTime", new PyLong(added.PayoutTime)),
                    ("isModified", added.IsModified ? PyBool.True : PyBool.False)));
        }
    }

    /// <summary>
    /// The wreck of an NPC that asked the host to make it (<see cref="SimNpcIdentity.HostWreck"/>): an item of the pilot who dealt the last blow (of the NPC's corporation when no pilot did) with what
    /// its loot table gave, put in space where the ship was for that pilot to loot. A wreck the scene made (its id is told) already has nothing in it, and is left as it is.
    /// </summary>
    private async Task LeaveNpcWreckAsync(NpcShip npc, SimDestroyed destroyed, Pilot? killer)
    {
        if (NpcWrecks is not { } wrecks || destroyed.WreckId != 0 || destroyed.WreckKind is not { } kind)
        {
            return;
        }
        try
        {
            var character = killer is null ? null : await _ships.CharacterAsync(killer.CharacterId);
            var made = await wrecks.LeaveAsync(kind.TypeId, npc.Profile.LootTableId, character?.Id ?? npc.Profile.CorporationId, npc.SolarSystemId);
            var credit = character is null ? new SimCredit(0, 0, 0, 0) : new SimCredit(character.Id, character.CorporationId, character.AllianceId ?? 0, character.FactionId ?? 0);
            PlaceWreck(new SimPlaceWreck(npc.SolarSystemId, made.Wreck.Id, WreckGroup, kind, destroyed.Position, destroyed.Direction, destroyed.Radius, destroyed.ShipId, credit, made.Contents.Count == 0));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the wreck of NPC ship {destroyed.ShipId} could not be made: {error.Message}");
        }
    }

    /// <summary><c>buildRandomUnitVector</c> of the reference's <c>npcAnchors.js</c>: a direction chosen evenly over the sphere.</summary>
    private static double[] RandomUnitVector()
    {
        var theta = Random.Shared.NextDouble() * Math.PI * 2;
        var u = Random.Shared.NextDouble() * 2 - 1;
        var planar = Math.Sqrt(Math.Max(0, 1 - u * u));
        return [Math.Cos(theta) * planar, u, Math.Sin(theta) * planar];
    }

    /// <summary>
    /// <c>buildSpawnStateForDefinition</c>: how far from the anchor the NPC of a batch comes (what its profile says, a random amount either way that grows with the
    /// size of the batch, and a step for each one before it so they are not on top of each other), at least a kilometre.
    /// </summary>
    internal static double NpcSpawnDistance(NpcProfile profile, int index, int total, double random) =>
        NpcSpawnDistance(Math.Max(1_000, profile.SpawnDistanceMeters), Math.Min(6_000, 800 + total * 600), NpcFormationSpacing, index, random);

    /// <summary>As above, from the distance, the window of chance either way and the step between ones of a batch that are given.</summary>
    internal static double NpcSpawnDistance(double baseDistance, double window, double step, int index, double random)
    {
        var jitter = (random * 2 - 1) * window;
        var spacing = Math.Min(12_000, index * step);
        return Math.Max(1_000, baseDistance + jitter + spacing);
    }

    /// <summary>The step between the NPCs of a batch (<c>formationSpacingMeters</c> of the reference's default).</summary>
    private const double NpcFormationSpacing = 1_250;

    /// <summary>
    /// What a batch of NPCs comes into space around: a ship (the batch is placed from where it is), or a place of the system (a belt). A distance of 0 is the profile's own;
    /// a window of chance either way that is not given grows with the size of the batch.
    /// </summary>
    private sealed record SpawnAnchor(int SolarSystemId, long ShipId, double[] Origin, double BaseDistance = 0, double? Window = null, long BeltId = 0, long Group = 0, long SiteId = 0);

    /// <summary>The hulls of NPCs are known before they are armed, and the missiles the hulls launch with them.</summary>
    private async Task EnsureNpcTypesAsync(IEnumerable<int> shipTypes, CancellationToken cancellationToken)
    {
        var hulls = shipTypes.Distinct().ToArray();
        await _statics.EnsureTypesAsync(hulls, cancellationToken).ConfigureAwait(false);
        var missiles = hulls.Select(hull => _statics.Type(hull)).OfType<TypeDef>().Select(NpcArmament.MissileTypeOf).Where(type => type > 0).Distinct().ToArray();
        if (missiles.Length > 0)
        {
            await _statics.EnsureTypesAsync(missiles, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spawns the NPCs of <paramref name="profiles"/> around <paramref name="anchor"/>, each armed with what its hull carries, and remembers what the host must know of
    /// them. What came into space is told: a batch that was cut short by the simulation is as long as what it spawned.
    /// </summary>
    private async Task<(List<(string Name, int Count)> Composition, int Unarmed)> SpawnGroupAsync(
        IReadOnlyList<NpcProfile> profiles,
        SpawnAnchor anchor,
        CancellationToken cancellationToken,
        long hostileResponseThreshold = NpcHostileThreshold,
        double aggressionRangeOverride = 0,
        List<long>? shipIds = null)
    {
        var composition = new List<(string Name, int Count)>();
        var unarmed = 0;
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var type = _statics.Type(profile.ShipTypeId)!;
            var shipId = NpcIdBase + Interlocked.Increment(ref _npcs);
            var weapon = NpcArmament.HullWeapon(type, shipId, _statics);
            var aggression = Math.Max(NpcArmament.AggressionRange(type, _statics), aggressionRangeOverride);
            var identity = new SimNpcIdentity(hostileResponseThreshold, NpcFriendlyThreshold, aggression, weapon is null ? [] : [weapon], null, HostWreck: NpcWrecks is not null);
            var hull = SimShips.OfType(shipId, type);
            var ship = hull with
            {
                OwnerId = profile.CorporationId, CorporationId = (int)profile.CorporationId, AllianceId = (int)profile.AllianceId, WarFactionId = profile.FactionId,
                SecurityStatus = profile.SecurityStatus,
                // an entity type has no lock range (it has `entityAttackRange` instead) and sometimes no lock count: with 0 of either it could never lock what it
                // attacks, so it locks as far as it attacks and holds at least one (`applyNativeNpcHullCombatOverrides` of the reference)
                MaxTargetRange = Math.Max(hull.MaxTargetRange, aggression),
                MaxLockedTargets = Math.Max(NpcMinimumLockSlots, hull.MaxLockedTargets),
            };
            var distance = NpcSpawnDistance(anchor.BaseDistance > 0 ? anchor.BaseDistance : Math.Max(1_000, profile.SpawnDistanceMeters), anchor.Window ?? Math.Min(6_000, 800 + profiles.Count * 600), NpcFormationSpacing, index, Random.Shared.NextDouble());
            var direction = RandomUnitVector();
            double[] place = [anchor.Origin[0] + direction[0] * distance, anchor.Origin[1] + direction[1] * distance, anchor.Origin[2] + direction[2] * distance];
            var spawn = new SimSpawn(NextSpawnRequest(), anchor.SolarSystemId, ship, anchor.ShipId, place, [0, 0, 0], RandomUnitVector(), NpcBehavior, identity);
            if (!await SpawnAsync(spawn, "npc", cancellationToken))
            {
                break;
            }
            _npcShips[shipId] = new NpcShip(profile, type.TypeId, anchor.SolarSystemId, anchor.BeltId, anchor.Group, anchor.SiteId);
            shipIds?.Add(shipId);
            unarmed += weapon is null ? 1 : 0;
            var name = profile.Name.Length > 0 ? profile.Name : profile.Id;
            var at = composition.FindIndex(part => part.Name == name);
            if (at < 0)
            {
                composition.Add((name, 1));
            }
            else
            {
                composition[at] = (name, composition[at].Count + 1);
            }
        }
        return (composition, unarmed);
    }

    /// <summary>
    /// A pilot's warp ended at a belt (<c>maybeSpawnAsteroidBeltRatsForArrival</c> of the reference): the belt may have a group of rats come to it, by the rolls the director makes. What came of the
    /// arrival is returned (null when the rats do not apply: there are none, the pilot is not in space or its system is not known). Arrivals are taken one at a time, so that two pilots at the
    /// same belt cannot both bring a group.
    /// </summary>
    internal async Task<BeltDecision?> BeltArrivalAsync(ulong session, long beltId, IReadOnlyList<double> beltPosition, CancellationToken cancellationToken = default)
    {
        if (BeltRats is not { } rats || !_pilots.TryGetValue(session, out var pilot) || _systems.Get(pilot.SolarSystemId) is not { } place)
        {
            return null;
        }
        await _beltGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // the hulls the rats can have are known before one is chosen
            if (_beltHulls is { IsFaulted: true })
            {
                _beltHulls = null;
            }
            await (_beltHulls ??= EnsureNpcTypesAsync(rats.PoolShipTypes(), CancellationToken.None)).ConfigureAwait(false);
            var system = new BeltSystem(place.Id, place.Security, place.RegionId, place.FactionId);
            var active = _npcShips.Values.Where(npc => npc.SolarSystemId == system.SolarSystemId && npc.BeltId == beltId && npc.Group != 0).Select(npc => npc.Group).Distinct().Count();
            var now = _nowUnixMs();
            var decision = rats.Decide(system, beltId, now, active, Random.Shared.NextDouble);
            if (decision is not { Outcome: BeltOutcome.Spawn, Plan: { } plan })
            {
                if (decision.Outcome == BeltOutcome.PlanFailed)
                {
                    _log?.Invoke($"no belt rat could be made for {decision.Faction} in system {system.SolarSystemId}");
                }
                return decision;
            }
            await EnsureNpcTypesAsync(plan.Profiles.Select(profile => profile.ShipTypeId), cancellationToken).ConfigureAwait(false);
            var anchor = new SpawnAnchor(system.SolarSystemId, 0, [.. beltPosition], rats.Config.SpawnDistanceMeters, BeltRatSpreadMeters, beltId, Interlocked.Increment(ref _beltGroups));
            var (composition, _) = await SpawnGroupAsync(plan.Profiles, anchor, cancellationToken).ConfigureAwait(false);
            if (composition.Count > 0)
            {
                rats.Spawned(system.SolarSystemId, beltId, now);
                _log?.Invoke($"belt {beltId} of system {system.SolarSystemId}: {plan.Faction} rats ({plan.Band}): {string.Join(", ", composition.Select(part => $"{part.Count} {part.Name}"))}");
            }
            return decision;
        }
        finally
        {
            _beltGate.Release();
        }
    }

    /// <summary>The distance and the spread of a cosmic site's wave around its place (the NPCs come into space around it, not around a ship).</summary>
    private const double SiteNpcDistanceMeters = 5_000, SiteNpcSpreadMeters = 2_000;

    /// <summary>
    /// The NPCs of one wave of a cosmic site, made at the site's place: the wave's query rolled for <paramref name="amount"/> and placed as a batch is, around
    /// the place. Returns how many came into space (the simulation may cut a batch short).
    /// </summary>
    internal async Task<int> SpawnSiteWaveAsync(int solarSystemId, long siteId, double[] position, string query, int amount, CancellationToken cancellationToken)
    {
        if (amount <= 0 || Npcs is not { } selector)
        {
            return 0;
        }
        var (selection, error, _) = selector.Select(query);
        if (selection is null)
        {
            _log?.Invoke($"site {siteId}: the wave {query} names no NPC ({error})");
            return 0;
        }
        await EnsureNpcTypesAsync(selection.ShipTypes, cancellationToken).ConfigureAwait(false);
        var plan = selector.Roll(selection, amount, type => NpcHull.HasHitPoints(_statics.Type(type)), Random.Shared.NextDouble);
        if (!plan.Succeeded)
        {
            _log?.Invoke($"site {siteId}: the wave {query} could not be rolled: {plan.Error}");
            return 0;
        }
        await EnsureNpcTypesAsync(plan.Profiles.Select(profile => profile.ShipTypeId), cancellationToken).ConfigureAwait(false);
        var anchor = new SpawnAnchor(solarSystemId, 0, position, SiteNpcDistanceMeters, SiteNpcSpreadMeters, SiteId: siteId);
        var (composition, _) = await SpawnGroupAsync(plan.Profiles, anchor, cancellationToken).ConfigureAwait(false);
        return composition.Sum(part => part.Count);
    }

    /// <summary>Whether the host holds an NPC of a cosmic site in the scene (the host forgets them when a scene is made again).</summary>
    internal bool HasSiteNpcs(long siteId) => siteId != 0 && _npcShips.Values.Any(npc => npc.SiteId == siteId);

    /// <summary>Whether a pilot is in the system: the simulation keeps a scene of a system only while one is.</summary>
    public bool HasPilotsIn(int solarSystemId) => _pilots.Values.Any(pilot => pilot.SolarSystemId == solarSystemId);

    /// <summary>A scene has been made for a system (a ship has come into a system nobody was in): the NPCs the host has kept of it were in the one before, which is gone.</summary>
    internal void ForgetNpcsOf(int solarSystemId)
    {
        foreach (var (id, npc) in _npcShips)
        {
            if (npc.SolarSystemId == solarSystemId)
            {
                _npcShips.TryRemove(id, out _);
            }
        }
    }

    public async Task<NpcSpawnReport> SpawnNpcsAsync(ClientSession session, string query, int amount, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return NpcSpawnReport.Failed(NpcSpawnReport.NotInSpace);
        }
        if (Npcs is not { } selector)
        {
            return NpcSpawnReport.Failed(NpcSpawnReport.SpawnFailed);
        }
        var (selection, selectionError, suggestions) = selector.Select(query);
        if (selection is null)
        {
            return NpcSpawnReport.Failed(selectionError!, suggestions);
        }
        await EnsureNpcTypesAsync(selection.ShipTypes, cancellationToken);
        var plan = selector.Roll(selection, amount, type => NpcHull.HasHitPoints(_statics.Type(type)), Random.Shared.NextDouble);
        if (!plan.Succeeded)
        {
            return NpcSpawnReport.Failed(plan.Error!, plan.Suggestions);
        }

        var (composition, unarmed) = await SpawnGroupAsync(plan.Profiles, new SpawnAnchor(pilot.SolarSystemId, pilot.ShipId, [0, 0, 0]), cancellationToken);
        return composition.Count == 0
            ? NpcSpawnReport.Failed(NpcSpawnReport.SpawnFailed)
            : new NpcSpawnReport(null, [], selection.Name, composition, plan.Profiles.Count, unarmed);
    }


    /// <summary>Spawns the authored response pool around an active high-sec criminal; its response timer guarantees the criminal ship is lost.</summary>
    public async Task<bool> RespondToCriminalAsync(
        long attackerCharacterId,
        long solarSystemId,
        long attackerShipId,
        CancellationToken cancellationToken = default)
    {
        var pilot = _pilots.Values.FirstOrDefault(candidate =>
            candidate.CharacterId == attackerCharacterId
            && candidate.SolarSystemId == solarSystemId
            && (attackerShipId <= 0 || candidate.ShipId == attackerShipId));
        if (pilot is null || pilot.TypeId == CharacterShips.CapsuleTypeId
            || Npcs is not { } selector)
        {
            return false;
        }

        var (selection, error, suggestions) = selector.Select("concord_response_fleet");
        if (selection is null)
        {
            _log?.Invoke($"CONCORD response pool is unavailable: {error}; {string.Join(", ", suggestions)}");
            return false;
        }
        await EnsureNpcTypesAsync(selection.ShipTypes, cancellationToken);
        var plan = selector.Roll(
            selection,
            3,
            type => NpcHull.HasHitPoints(_statics.Type(type)),
            Random.Shared.NextDouble);
        if (!plan.Succeeded)
        {
            _log?.Invoke($"CONCORD response could not be rolled: {plan.Error}");
            return false;
        }

        var position = await PositionOfAsync(pilot.Session, pilot.ShipId, cancellationToken);
        if (position is not { } targetPoint)
        {
            return false;
        }
        var (composition, _) = await SpawnGroupAsync(
            plan.Profiles,
            new SpawnAnchor(
                pilot.SolarSystemId,
                pilot.ShipId,
                [targetPoint.X, targetPoint.Y, targetPoint.Z],
                BaseDistance: 8_000,
                Window: 4_000),
            cancellationToken,
            hostileResponseThreshold: 0,
            aggressionRangeOverride: 50_000);
        if (composition.Count == 0)
        {
            return false;
        }

        var markedShip = pilot.ShipId;
        _concordDestroyed[markedShip] = 0;
        _timers.Set($"space:concord:mark:{markedShip}", TimeSpan.FromMinutes(2), () =>
        {
            _concordDestroyed.TryRemove(markedShip, out _);
        });
        var creditProfile = plan.Profiles[0];
        _timers.Set(
            $"space:concord:punishment:{attackerCharacterId}:{solarSystemId}",
            TimeSpan.FromSeconds(15),
            () => FinishConcordPunishmentAsync(
                attackerCharacterId,
                solarSystemId,
                new SimCredit(0, creditProfile.CorporationId, creditProfile.AllianceId, creditProfile.FactionId)));
        return true;
    }

    private Task FinishConcordPunishmentAsync(long characterId, long solarSystemId, SimCredit credit)
    {
        var current = _pilots.Values.FirstOrDefault(pilot =>
            pilot.CharacterId == characterId
            && pilot.SolarSystemId == solarSystemId
            && pilot.TypeId != CharacterShips.CapsuleTypeId);
        if (current is null)
        {
            return Task.CompletedTask;
        }
        _concordDestroyed[current.ShipId] = 0;
        _sim.Destroy(current.ShipId, credit);
        return Task.CompletedTask;
    }


    /// <summary>Spawns the configured CONCORD response fleet around an active criminal and schedules the ship loss after the response ships engage.</summary>


    /// <summary>One order about a pilot's modules or targets is asked of the simulation at a time (the answers are matched by what was asked); an unreachable simulation is a message the client shows.</summary>
    private async Task<CommandReply> AskSimAsync(ulong id, Func<Task<CommandReply>> ask, ClientSession session)
    {
        var gate = _targeting.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await ask();
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"modules of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        finally
        {
            gate.Release();
        }
    }
}
