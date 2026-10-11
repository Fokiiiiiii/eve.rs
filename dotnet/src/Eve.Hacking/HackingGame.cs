namespace Eve.Hacking;

/// <summary>One hacking attempt as the analyzer starts it: the virus stats are the module's values after dogma.</summary>
public sealed record HackingAttempt(
    long TargetId,
    int ModuleTypeId,
    int GameType,
    int Difficulty,
    int VirusCoherence,
    int VirusStrength,
    int VirusSlots,
    int Seed);

/// <summary>One event of the client's <c>OnHackingTurnComplete</c> list: the client's event id and its named fields.</summary>
public sealed record HackingEvent(int EventId, IReadOnlyDictionary<string, object?> Data);

public enum HackingOutcome { Active, Won, Lost }

/// <summary>An installed utility element. A slot holds one; an element in use keeps its slot until it runs out.</summary>
public sealed class UtilityItem(int id, int subtype)
{
    public int Id { get; } = id;
    public int Subtype { get; } = subtype;
    public bool InUse { get; set; }
    /// <summary>Turns left (self repair, secondary vector) or hits left (polymorphic shield).</summary>
    public int Remaining { get; set; }
    public Coord? Target { get; set; }
}

/// <summary>
/// The rules of one attempt. A click on a tile is one turn: the virus strikes or picks the tile, the system retaliates, then
/// the turn-end effects run. Every method returns the events the client gets in that call (an ack when nothing happened).
/// </summary>
public sealed class HackingGame
{
    public const int VirusStrengthFloor = 10;
    public const int VirusSuppressorReduction = 15;
    public const int RestorationHealing = 20;
    public const int SecondaryVectorDamage = 20;
    public const int SelfRepairMin = 5;
    public const int SelfRepairMax = 10;
    public const int SelfRepairTurns = 3;
    public const int SecondaryVectorTurns = 3;
    public const int PolymorphicShieldHits = 2;
    public const int DistanceCap = 5;
    public const int MaxSlots = 8;

    private readonly HackingAttempt _attempt;
    private readonly Random _rng;
    private readonly int _baseVirusStrength;
    private int _nextItemId = 1;

    public HackingGame(HackingAttempt attempt, HackingBoard board)
    {
        _attempt = attempt;
        Board = board;
        _rng = new Random(attempt.Seed);
        VirusCoherence = Math.Max(1, attempt.VirusCoherence);
        _baseVirusStrength = VirusStrength = Math.Max(1, attempt.VirusStrength);
        Slots = new UtilityItem?[Math.Clamp(attempt.VirusSlots, 0, MaxSlots)];
    }

    public static HackingGame Create(HackingAttempt attempt) =>
        new(attempt, HackingBoard.Generate(attempt.Difficulty, attempt.Seed));

    public HackingBoard Board { get; }
    public UtilityItem?[] Slots { get; }
    public int VirusId { get; } = 1;
    public int VirusCoherence { get; private set; }
    public int VirusStrength { get; private set; }
    public HackingOutcome Outcome { get; private set; }

    public IReadOnlyList<HackingEvent> Start()
    {
        var events = new List<HackingEvent>
        {
            new(HackingWire.EventGameStyle, Dict(("style", _attempt.GameType))),
        };
        events.AddRange(Board.Tiles.Values.OrderBy(t => t.Id).Select(t => new HackingEvent(HackingWire.EventTileCreated, TileView(t))));
        events.Add(new HackingEvent(HackingWire.EventVirusCreated, Dict(
            ("id", VirusId),
            ("type", HackingWire.TypeVirus),
            ("strength", VirusStrength),
            ("coherence", VirusCoherence),
            ("inventory", InventoryView()))));
        events.Add(new HackingEvent(HackingWire.EventGameStart, Dict(
            ("moduleTypeID", _attempt.ModuleTypeId),
            ("targetID", _attempt.TargetId),
            ("gameType", _attempt.GameType),
            ("difficulty", _attempt.Difficulty))));
        return events;
    }

    public IReadOnlyList<HackingEvent> ClickTile(Coord coord)
    {
        if (Outcome != HackingOutcome.Active
            || !Board.Tiles.TryGetValue(coord, out var tile)
            || !tile.Hidden
            || !Explorable(coord)
            || (tile.Type == HackingWire.TypeUtilityElementTile && FreeSlot() < 0))
            return Ack();

        var events = new List<HackingEvent>();
        tile.Hidden = false;
        events.Add(new HackingEvent(HackingWire.EventTileFlipped, TileView(tile)));
        switch (tile.Type)
        {
            case HackingWire.TypeSegment:
                events.Add(new HackingEvent(HackingWire.EventDistanceIndicator, Dict(
                    ("coord", coord),
                    ("distanceIndicator", DistanceToResource(coord)))));
                break;
            case HackingWire.TypeDataCache:
                OpenCache(tile, events);
                break;
            case HackingWire.TypeUtilityElementTile:
                PickUp(tile, events);
                break;
            default:
                Strike(tile, events);
                break;
        }
        Finish(events, turn: true);
        return events;
    }

    /// <summary>Applies a self repair or a polymorphic shield to the virus. Not a turn.</summary>
    public IReadOnlyList<HackingEvent> UseOnVirus(int index)
    {
        var item = ItemAt(index);
        if (Outcome != HackingOutcome.Active || item is null || item.InUse
            || item.Subtype is not (HackingWire.SubtypeUeSelfRepair or HackingWire.SubtypeUePolymorphicShield))
            return Ack();

        item.InUse = true;
        item.Remaining = item.Subtype == HackingWire.SubtypeUePolymorphicShield ? PolymorphicShieldHits : SelfRepairTurns;
        return [new HackingEvent(HackingWire.EventUeInUse, Dict(("index", index), ("value", true)))];
    }

    /// <summary>Applies a kernel rot (once) or a secondary vector (for three turns) to a core or defensive subsystem. Not a turn.</summary>
    public IReadOnlyList<HackingEvent> UseOnTarget(int index, Coord coord)
    {
        var item = ItemAt(index);
        if (Outcome != HackingOutcome.Active || item is null || item.InUse
            || item.Subtype is not (HackingWire.SubtypeUeKernelRot or HackingWire.SubtypeUeSecondaryVector)
            || !Board.Tiles.TryGetValue(coord, out var target)
            || target.Type is not (HackingWire.TypeCore or HackingWire.TypeDefenseSoftware)
            || target.Coherence <= 0)
            return Ack();

        var events = new List<HackingEvent>();
        if (item.Subtype == HackingWire.SubtypeUeKernelRot)
        {
            target.Coherence /= 2;
            events.Add(new HackingEvent(HackingWire.EventKernelRot, TileView(target)));
            RemoveItem(item, events);
            if (target.Coherence <= 0)
                Destroy(target, events);
        }
        else
        {
            item.InUse = true;
            item.Target = coord;
            item.Remaining = SecondaryVectorTurns;
            events.Add(new HackingEvent(HackingWire.EventUeInUse, Dict(("index", index), ("value", true))));
        }
        Finish(events, turn: false);
        return events;
    }

    /// <summary>The hacker leaves, the window closes (warp, out of range) or the virus is lost to the client.</summary>
    public IReadOnlyList<HackingEvent> Quit()
    {
        if (Outcome != HackingOutcome.Active)
            return Ack();
        var events = new List<HackingEvent>();
        Lose(events);
        return events;
    }

    /// <summary>Whether a hidden tile can be explored: a revealed, unblocked neighbour exists.</summary>
    public bool Explorable(Coord coord) =>
        HackingBoard.Neighbours(coord).Any(n => Board.Tiles.TryGetValue(n, out var t) && !t.Hidden && !t.Blocked);

    private static IReadOnlyList<HackingEvent> Ack() => [new HackingEvent(HackingWire.EventAck, Dict())];

    private void OpenCache(Tile tile, List<HackingEvent> events)
    {
        var (type, subtype) = tile.Contents ?? throw new InvalidOperationException($"data cache {tile.Coord} has no contents");
        tile.Contents = null;
        tile.Type = type;
        tile.Subtype = subtype;
        if (type == HackingWire.TypeDefenseSoftware)
        {
            HackingBoard.ApplyDefenceStats(tile);
            tile.Blocked = true;
        }
        events.Add(new HackingEvent(HackingWire.EventDataCacheOpen, TileView(tile)));
    }

    private void PickUp(Tile tile, List<HackingEvent> events)
    {
        var slot = FreeSlot();
        Slots[slot] = new UtilityItem(_nextItemId++, tile.Subtype);
        tile.Type = HackingWire.TypeSegment;
        tile.Subtype = HackingWire.SubtypeNone;
        events.Add(new HackingEvent(HackingWire.EventUePickedUp, Dict(
            ("coord", tile.Coord),
            ("replacementObject", TileView(tile)),
            ("inventory", InventoryView()))));
    }

    private void Strike(Tile target, List<HackingEvent> events)
    {
        target.Coherence = Math.Max(0, target.Coherence - VirusStrength);
        events.Add(new HackingEvent(HackingWire.EventAttack, Dict(
            ("attackerResult", VirusView()),
            ("defenderResult", TileView(target)))));
        if (target.Coherence <= 0)
        {
            Destroy(target, events);
            return;
        }
        if (target.Type == HackingWire.TypeDefenseSoftware)
            target.Blocked = true;
        Retaliate(target, events);
    }

    private void Retaliate(Tile attacker, List<HackingEvent> events)
    {
        var shield = Slots.FirstOrDefault(s => s is { Subtype: HackingWire.SubtypeUePolymorphicShield, InUse: true });
        if (shield is not null)
        {
            shield.Remaining--;
            events.Add(new HackingEvent(HackingWire.EventPolymorphicShieldHit, Dict(
                ("index", Array.IndexOf(Slots, shield)),
                ("durationRemaining", shield.Remaining))));
            if (shield.Remaining <= 0)
                RemoveItem(shield, events);
            return;
        }

        VirusCoherence = Math.Max(0, VirusCoherence - attacker.Strength);
        events.Add(new HackingEvent(HackingWire.EventAttack, Dict(
            ("attackerResult", TileView(attacker)),
            ("defenderResult", VirusView()))));
        if (VirusCoherence <= 0)
        {
            events.Add(new HackingEvent(HackingWire.EventObjectKilled, VirusView()));
            Lose(events);
        }
    }

    /// <summary>A core or defensive subsystem at zero coherence: a core ends the attempt, a subsystem becomes an empty explored node.</summary>
    private void Destroy(Tile target, List<HackingEvent> events)
    {
        target.Hidden = false;
        target.Coherence = 0;
        if (target.Type == HackingWire.TypeCore)
        {
            Outcome = HackingOutcome.Won;
            events.Add(new HackingEvent(HackingWire.EventObjectKilled, TileView(target)));
            events.Add(new HackingEvent(HackingWire.EventGameWon, Dict(("targetID", _attempt.TargetId))));
            return;
        }
        target.Type = HackingWire.TypeSegment;
        target.Subtype = HackingWire.SubtypeNone;
        target.Strength = 0;
        target.Blocked = false;
        events.Add(new HackingEvent(HackingWire.EventObjectKilled, TileView(target)));
    }

    private void Finish(List<HackingEvent> events, bool turn)
    {
        if (Outcome != HackingOutcome.Active)
            return;
        if (turn)
            TurnEnd(events);
        SyncVirusStrength(events);
    }

    private void TurnEnd(List<HackingEvent> events)
    {
        foreach (var restoration in Board.Tiles.Values.OrderBy(t => t.Id)
                     .Where(t => t is { Type: HackingWire.TypeDefenseSoftware, Subtype: HackingWire.SubtypeDsRestorationNode, Hidden: false })
                     .ToList())
        {
            var alive = Board.Tiles.Values.Where(t => t.Type == HackingWire.TypeDefenseSoftware).OrderBy(t => t.Id).ToList();
            var healed = alive[_rng.Next(alive.Count)];
            healed.Coherence += RestorationHealing;
            events.Add(new HackingEvent(HackingWire.EventRestorationHeal, Dict(
                ("coord", restoration.Coord),
                ("healedObject", TileView(healed)),
                ("healingAmount", RestorationHealing))));
        }

        for (var i = 0; i < Slots.Length; i++)
        {
            var item = Slots[i];
            if (item is not { Subtype: HackingWire.SubtypeUeSelfRepair, InUse: true })
                continue;
            VirusCoherence += _rng.Next(SelfRepairMin, SelfRepairMax + 1);
            item.Remaining--;
            events.Add(new HackingEvent(HackingWire.EventSelfRepair, Dict(
                ("index", i),
                ("durationRemaining", item.Remaining),
                ("target", VirusView()))));
            if (item.Remaining <= 0)
                RemoveItem(item, events);
        }

        for (var i = 0; i < Slots.Length && Outcome == HackingOutcome.Active; i++)
        {
            var item = Slots[i];
            if (item is not { Subtype: HackingWire.SubtypeUeSecondaryVector, InUse: true, Target: { } at })
                continue;
            var target = Board.Tiles[at];
            if (target.Type is not (HackingWire.TypeCore or HackingWire.TypeDefenseSoftware))
            {
                RemoveItem(item, events);
                continue;
            }
            target.Coherence = Math.Max(0, target.Coherence - SecondaryVectorDamage);
            item.Remaining--;
            var data = new Dictionary<string, object?>(TileView(target))
            {
                ["index"] = i,
                ["durationRemaining"] = item.Remaining,
            };
            events.Add(new HackingEvent(HackingWire.EventSecondaryVector, data));
            if (target.Coherence <= 0)
                Destroy(target, events);
            if (item.Remaining <= 0 || target.Coherence <= 0)
                RemoveItem(item, events);
        }
    }

    /// <summary>A revealed virus suppressor lowers the virus strength by 15 each, never below 10 (or below the base value).</summary>
    private void SyncVirusStrength(List<HackingEvent> events)
    {
        var suppressors = Board.Tiles.Values.Count(t => t is { Type: HackingWire.TypeDefenseSoftware, Subtype: HackingWire.SubtypeDsVirusSuppressor, Hidden: false });
        var strength = suppressors == 0
            ? _baseVirusStrength
            : Math.Min(_baseVirusStrength, Math.Max(VirusStrengthFloor, _baseVirusStrength - VirusSuppressorReduction * suppressors));
        if (strength == VirusStrength)
            return;
        VirusStrength = strength;
        events.Add(new HackingEvent(HackingWire.EventVirusSuppressor, Dict(("virus", VirusView()))));
    }

    private void RemoveItem(UtilityItem item, List<HackingEvent> events)
    {
        var index = Array.IndexOf(Slots, item);
        if (index < 0)
            return;
        Slots[index] = null;
        events.Add(new HackingEvent(HackingWire.EventUeRemoved, Dict(
            ("index", index),
            ("inventory", Dict(("inventory", InventoryView()))))));
    }

    private void Lose(List<HackingEvent> events)
    {
        Outcome = HackingOutcome.Lost;
        events.Add(new HackingEvent(HackingWire.EventGameLost, Dict(("targetID", _attempt.TargetId))));
    }

    /// <summary>Steps from an empty node to the nearest core, utility subsystem or data cache, capped at 5 (5 means five or more).</summary>
    private int DistanceToResource(Coord from)
    {
        var distances = HackingBoard.DistancesFrom(from);
        var nearest = Board.Tiles.Values
            .Where(t => t.Type is HackingWire.TypeCore or HackingWire.TypeUtilityElementTile or HackingWire.TypeDataCache)
            .Select(t => distances.TryGetValue(t.Coord, out var d) ? d : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return Math.Min(DistanceCap, nearest);
    }

    private UtilityItem? ItemAt(int index) => index >= 0 && index < Slots.Length ? Slots[index] : null;

    private int FreeSlot() => Array.FindIndex(Slots, s => s is null);

    private IReadOnlyList<object?> InventoryView() =>
        Slots.Select(s => (object?)Dict(
            ("id", s?.Id),
            ("subtype", s?.Subtype ?? HackingWire.SubtypeNone),
            ("info", null))).ToList();

    private Dictionary<string, object?> VirusView() => Dict(
        ("id", VirusId),
        ("type", HackingWire.TypeVirus),
        ("strength", VirusStrength),
        ("coherence", VirusCoherence));

    /// <summary>What a player may see of a tile: a hidden tile shows neither its type nor its numbers.</summary>
    private static Dictionary<string, object?> TileView(Tile t) => t.Hidden
        ? Dict(
            ("id", t.Id), ("type", HackingWire.TypeNone), ("subtype", HackingWire.SubtypeNone), ("coord", t.Coord),
            ("hidden", true), ("blocked", false), ("strength", 0), ("coherence", 0))
        : Dict(
            ("id", t.Id), ("type", t.Type), ("subtype", t.Subtype), ("coord", t.Coord),
            ("hidden", false), ("blocked", t.Blocked), ("strength", t.Strength), ("coherence", t.Coherence));

    private static Dictionary<string, object?> Dict(params (string Key, object? Value)[] data) =>
        data.ToDictionary(p => p.Key, p => p.Value);
}
