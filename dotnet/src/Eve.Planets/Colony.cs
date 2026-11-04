namespace Eve.Planets;

/// <summary>One pin: a command center, an extraction control unit, a processor, a storage facility or a launchpad.</summary>
public sealed class Pin
{
    public required long Id { get; init; }
    public required int TypeId { get; set; }
    public required PinKind Kind { get; init; }
    public required long OwnerId { get; init; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>The last time the pin ran (the cycle start for an ECU or processor, the last transfer for storage).</summary>
    public long? LastRunTime { get; set; }
    /// <summary>-1 disabled, 0 idle, 1 active (basePin.py STATE_*).</summary>
    public int State { get; set; }
    public Dictionary<int, long> Contents { get; set; } = [];

    // extraction control unit: the program and its heads
    public int? ProgramType { get; set; }
    public long QtyPerCycle { get; set; }
    public long CycleTime { get; set; }
    public long? InstallTime { get; set; }
    public long? ExpiryTime { get; set; }
    public double HeadRadius { get; set; }
    public List<Head> Heads { get; set; } = [];

    // processor: the schematic, its cycle and the demands and products of one cycle
    public int? SchematicId { get; set; }
    public long ProcessCycle { get; set; }
    public Dictionary<int, long> Demands { get; set; } = [];
    public Dictionary<int, long> Products { get; set; } = [];
    public bool HasReceivedInputs { get; set; } = true;
    public bool ReceivedInputsLastCycle { get; set; } = true;

    // command center and launchpad
    public long? LastLaunchTime { get; set; }
}

/// <summary>A link between two pins (the endpoints are ordered: <see cref="End1"/> is the lower id).</summary>
public sealed class Link
{
    public required long End1 { get; init; }
    public required long End2 { get; init; }
    public required int TypeId { get; init; }
    public int Level { get; set; }
}

/// <summary>A route: a commodity moved from a source pin along a path of pins, <see cref="Quantity"/> a cycle.</summary>
public sealed class Route
{
    public required long Id { get; init; }
    public required long OwnerId { get; init; }
    public required int TypeId { get; init; }
    public required long Quantity { get; init; }
    public required IReadOnlyList<long> Path { get; init; }
    public long Source => Path[0];
    public long Destination => Path[^1];
}

/// <summary>
/// A colony: one character's pins, links and routes on one planet, with the simulation of the client's <c>BaseColony</c>.
/// <see cref="RunTo"/>; the result depends only on the stored state and the time.
/// </summary>
public sealed partial class Colony(PlanetCatalog catalog, IResourceField field, long planetId, int planetTypeId, double radius, long ownerId)
{
    public PlanetCatalog Catalog { get; } = catalog;
    public IResourceField Field { get; } = field;
    public long PlanetId { get; } = planetId;
    public int PlanetTypeId { get; } = planetTypeId;
    public double Radius { get; } = radius;
    public long OwnerId { get; } = ownerId;
    public int Level { get; set; }
    public long CurrentSimTime { get; set; }
    public Dictionary<long, Pin> Pins { get; } = [];
    public Dictionary<(long, long), Link> Links { get; } = [];
    public Dictionary<long, Route> Routes { get; } = [];
    /// <summary>The last route id given out; the colony keeps it, so ids stay unique across the colony's life.</summary>
    public long RouteCounter { get; set; }

    /// <summary>The next route id of the colony.</summary>
    public long NewRouteId() => ++RouteCounter;

    /// <summary>The command center, or null when the colony has none (the rest of the network is then not allowed).</summary>
    public Pin? CommandCenter => Pins.Values.FirstOrDefault(p => p.Kind == PinKind.CommandCenter);

    private readonly PriorityQueue<long, (long Time, long PinId)> _queue = new();
    private readonly Dictionary<long, long> _scheduled = [];

    // ----- pin rules: the client's BasePin, ECU, processor, storage and spaceport classes -----

    private static bool IsStorageLike(Pin p) => p.Kind is PinKind.Storage or PinKind.Launchpad or PinKind.CommandCenter;
    private static bool IsProcessor(Pin p) => p.Kind == PinKind.Processor;
    private static bool IsConsumer(Pin p) => p.Kind == PinKind.Processor;
    private static bool IsProducer(Pin p) => p.Kind is PinKind.Ecu or PinKind.Processor;

    private bool IsActive(Pin p) => p.Kind switch
    {
        PinKind.Ecu => p.ProgramType is not null && p.State > 0,
        PinKind.Processor => p.State > 0,
        _ => false,
    };

    private bool CanActivate(Pin p) => p.Kind switch
    {
        PinKind.Ecu => p.State >= 1 && p.ProgramType is not null,
        PinKind.Processor => p.State >= 0 && p.SchematicId is not null
            && (IsActive(p) || p.HasReceivedInputs || p.ReceivedInputsLastCycle || HasEnoughInputs(p)),
        // storage-like pins never run (StoragePin.CanActivate is False)
        _ => false,
    };

    private static bool HasEnoughInputs(Pin p)
    {
        foreach (var (type, qty) in p.Demands)
            if (!p.Contents.TryGetValue(type, out var have) || qty > have) return false;
        return true;
    }

    private long? NextRunTime(Pin p)
    {
        if (p.Kind == PinKind.Processor && !IsActive(p) && HasEnoughInputs(p)) return null;
        var cycle = CycleOf(p);
        if (p.LastRunTime is null || cycle is null) return null;
        return p.LastRunTime.Value + cycle.Value;
    }

    private long? CycleOf(Pin p) => p.Kind switch
    {
        PinKind.Ecu => p.CycleTime > 0 ? p.CycleTime : null,
        PinKind.Processor => p.ProcessCycle > 0 ? p.ProcessCycle : null,
        _ => null,
    };

    private bool CanRun(Pin p, long runTime)
    {
        if (!CanActivate(p)) return false;
        if (p.Kind == PinKind.Processor && !IsActive(p) && !CanActivate(p)) return false;
        var next = NextRunTime(p);
        return next is null || next <= runTime;
    }

    /// <summary>BasePin.Run / ECU.Run / ProcessPin.Run: returns the products of this run.</summary>
    private Dictionary<int, long> RunPin(Pin p, long runTime)
    {
        switch (p.Kind)
        {
            case PinKind.Ecu:
            {
                p.LastRunTime = runTime;
                var products = new Dictionary<int, long>();
                if (p.ProgramType is null) return products;
                if (IsActive(p))
                {
                    var (decay, noise) = (Catalog.Type(p.TypeId).Attr(PlanetAttr.EcuDecayFactor), Catalog.Type(p.TypeId).Attr(PlanetAttr.EcuNoiseFactor));
                    products[p.ProgramType.Value] = PlanetRules.EcuOutput(runTime, p.QtyPerCycle, p.CycleTime, p.InstallTime ?? 0, decay, noise);
                    if (p.ExpiryTime <= runTime) p.State = 0;
                }
                return products;
            }
            case PinKind.Processor:
            {
                var products = IsActive(p) ? new Dictionary<int, long>(p.Products) : new Dictionary<int, long>();
                var canConsume = HasEnoughInputs(p);
                if (canConsume)
                {
                    foreach (var (type, qty) in p.Demands) RemoveCommodity(p, type, qty);
                    p.State = 1;
                }
                else p.State = 0;
                p.ReceivedInputsLastCycle = p.HasReceivedInputs;
                p.HasReceivedInputs = false;
                p.LastRunTime = runTime;
                return products;
            }
            default:
                p.LastRunTime = runTime;
                p.State = Math.Min(p.State, 0);
                return [];
        }
    }

    /// <summary>The cargo a pin holds (the type's capacity); processors and ECUs hold no commodity stock of their own.</summary>
    private double? Capacity(Pin p) => p.Kind is PinKind.Processor or PinKind.Ecu ? null : Catalog.Type(p.TypeId).Capacity;

    private double UsedVolume(Pin p) => p.Contents.Sum(kv => Catalog.Type(kv.Key).Volume * kv.Value);

    private double FreeSpace(Pin p) => (Capacity(p) ?? 0) - UsedVolume(p);

    /// <summary>ProcessPin.GetInputBufferState: how empty the inputs are, 0 full to 1 empty.</summary>
    private static double InputBufferState(Pin p)
    {
        double ratio = 0;
        foreach (var (type, qty) in p.Demands) ratio += (double)p.Contents.GetValueOrDefault(type) / qty;
        return 1 - ratio / p.Demands.Count;
    }

    /// <summary>BasePin.CanAccept / ProcessPin.CanAccept: how much of a commodity the pin takes now.</summary>
    private long CanAccept(Pin p, int type, long qty)
    {
        if (p.Kind == PinKind.Ecu) return 0;
        if (p.Kind == PinKind.Processor)
        {
            if (!p.Demands.TryGetValue(type, out var demand)) return 0;
            var remaining = demand - p.Contents.GetValueOrDefault(type);
            return remaining < qty ? remaining : qty;
        }
        if (p.State < 0) return 0;
        var cap = Capacity(p);
        if (cap is null) return Math.Max(0, qty);
        var volume = Catalog.Type(type).Volume;
        var remainingVolume = Math.Max(0, cap.Value - UsedVolume(p));
        if (volume * qty > remainingVolume || qty == -1) return (long)(remainingVolume / volume);
        return qty;
    }

    private long AddCommodity(Pin p, int type, long qty)
    {
        var add = CanAccept(p, type, qty);
        if (add < 1) return 0;
        p.Contents[type] = p.Contents.GetValueOrDefault(type) + add;
        if (p.Kind == PinKind.Processor && add > 0) p.HasReceivedInputs = true;
        return add;
    }

    private long RemoveCommodity(Pin p, int type, long qty)
    {
        if (!p.Contents.TryGetValue(type, out var have)) return 0;
        long removed;
        if (have <= qty)
        {
            removed = have;
            p.Contents.Remove(type);
        }
        else
        {
            removed = qty;
            p.Contents[type] = have - qty;
        }
        return removed;
    }

    // ----- the schedule: the client's SchedulePin / PrimeSimulation / RunSimulation -----

    private void Schedule(Pin p)
    {
        var next = NextRunTime(p);
        if (_scheduled.TryGetValue(p.Id, out var existing))
        {
            if (next is null || next < existing) _scheduled.Remove(p.Id);
            else return;
        }
        var when = next is null || next < CurrentSimTime ? CurrentSimTime : next.Value;
        _queue.Enqueue(p.Id, (when, p.Id));
        _scheduled[p.Id] = when;
    }

    /// <summary>
    /// RunSimulation (baseColony.py:169): every pin that can run before <paramref name="end"/> is evaluated in time order,
    /// then the colony's time is <paramref name="end"/>. Advancing to a time that is not later than the current one changes nothing.
    /// </summary>
    public void RunTo(long end)
    {
        _queue.Clear();
        _scheduled.Clear();
        foreach (var pin in Pins.Values.OrderBy(p => p.Id))
            if (CanRun(pin, end)) Schedule(pin);
        while (_queue.TryDequeue(out var pinId, out var key))
        {
            var (time, _) = key;
            if (!_scheduled.TryGetValue(pinId, out var due) || due != time) continue;
            if (time >= end) break;
            _scheduled.Remove(pinId);
            CurrentSimTime = time;
            if (!Pins.TryGetValue(pinId, out var pin)) continue;
            if (pin.Kind == PinKind.Ecu && CycleOf(pin) is null) break;
            if (!CanRun(pin, time)) continue;
            EvaluatePin(pin);
        }
        CurrentSimTime = Math.Max(CurrentSimTime, end);
    }

    private void EvaluatePin(Pin pin)
    {
        if (!CanActivate(pin) && !IsActive(pin)) return;
        var commodities = RunPin(pin, CurrentSimTime);
        if (IsConsumer(pin)) RouteInput(pin);
        if (IsActive(pin) || CanActivate(pin)) Schedule(pin);
        if (commodities.Count == 0) return;
        RouteOutput(pin, commodities);
    }

    // ----- routing: the client's RouteCommodityOutput / RouteCommodityInput / TransferCommodities -----

    /// <summary>The routes of a source, merged per destination and commodity (ColonyData.megaRoutesBySource), sorted as the client heaps them.</summary>
    private (List<(long Dest, int Type, long Qty)> Processors, List<(long Dest, int Type, long Qty)> Storages) SortedRoutes(long sourceId, IReadOnlyDictionary<int, long> commodities)
    {
        var merged = new SortedDictionary<(long, int), long>();
        foreach (var route in Routes.Values.Where(r => r.Source == sourceId))
        {
            if (!commodities.ContainsKey(route.TypeId)) continue;
            merged[(route.Destination, route.TypeId)] = merged.GetValueOrDefault((route.Destination, route.TypeId)) + route.Quantity;
        }
        var processors = new List<((double, long, int, long) Key, (long Dest, int Type, long Qty) Entry)>();
        var storages = new List<((double, long, int, long) Key, (long Dest, int Type, long Qty) Entry)>();
        foreach (var ((dest, type), qty) in merged)
        {
            if (qty == 0) continue;
            var destPin = Pins[dest];
            if (IsProcessor(destPin))
                processors.Add(((InputBufferState(destPin), dest, type, qty), (dest, type, qty)));
            else
                storages.Add(((FreeSpace(destPin), dest, type, qty), (dest, type, qty)));
        }
        return (processors.OrderBy(x => x.Key).Select(x => x.Entry).ToList(), storages.OrderBy(x => x.Key).Select(x => x.Entry).ToList());
    }

    private void RouteOutput(Pin source, Dictionary<int, long> commodities)
    {
        var pinsReceiving = new SortedDictionary<long, Dictionary<int, long>>();
        var done = false;
        var (processors, storages) = SortedRoutes(source.Id, commodities);
        foreach (var (isStorage, list) in new[] { (false, processors), (true, storages) })
        {
            if (done) break;
            for (var i = 0; i < list.Count; i++)
            {
                var (dest, type, qty) = list[i];
                double? maxAmount = null;
                if (isStorage)
                    maxAmount = Math.Ceiling((double)commodities.GetValueOrDefault(type) / (list.Count - i - 1 + 1));
                var (moved, amount) = Transfer(source, Pins[dest], type, qty, commodities, maxAmount);
                if (moved != 0 && commodities.ContainsKey(moved))
                {
                    commodities[moved] -= amount;
                    if (commodities[moved] <= 0) commodities.Remove(moved);
                }
                if (amount > 0)
                {
                    if (!pinsReceiving.TryGetValue(dest, out var got)) pinsReceiving[dest] = got = [];
                    got[moved] = got.GetValueOrDefault(moved) + amount;
                }
                if (commodities.Count == 0)
                {
                    done = true;
                    break;
                }
            }
        }

        foreach (var (recvId, added) in pinsReceiving)
        {
            var receiving = Pins[recvId];
            if (IsConsumer(receiving)) Schedule(receiving);
            if (!IsStorageLike(source) && IsStorageLike(receiving)) RouteOutput(receiving, new Dictionary<int, long>(added));
        }
    }

    private void RouteInput(Pin destination)
    {
        foreach (var route in Routes.Values.Where(r => r.Destination == destination.Id).OrderBy(r => r.Id).ToList())
        {
            if (!Pins.TryGetValue(route.Source, out var source) || !IsStorageLike(source)) continue;
            if (source.Contents.Count == 0) continue;
            var stored = new Dictionary<int, long>(source.Contents);
            Transfer(source, destination, route.TypeId, route.Quantity, stored, null);
        }
    }

    /// <summary>TransferCommodities: moves up to qty (and maxAmount) of a commodity; a storage source gives up what moved.</summary>
    private (int Type, long Amount) Transfer(Pin source, Pin dest, int type, long qty, IDictionary<int, long> commodities, double? maxAmount)
    {
        if (!commodities.TryGetValue(type, out var available)) return (0, 0);
        var amount = Math.Min(available, qty);
        if (maxAmount is { } cap) amount = (long)Math.Floor(Math.Min(cap, amount));
        if (amount <= 0) return (0, 0);
        var moved = AddCommodity(dest, type, amount);
        if (IsStorageLike(source)) RemoveCommodity(source, type, moved);
        return (type, moved);
    }

    /// <summary>The client's OnRouteCreated: a route created now moves what its source holds, and wakes its destination.</summary>
    private void OnRouteCreated(Route route)
    {
        var source = Pins[route.Source];
        var dest = Pins[route.Destination];
        if (IsStorageLike(source))
            Transfer(source, dest, route.TypeId, route.Quantity, new Dictionary<int, long>(source.Contents), null);
        else
            StimulateIdle(source);
        if (!IsStorageLike(dest) && !IsActive(dest) && CanActivate(dest)) Schedule(dest);
    }

    /// <summary>StimulateIdlePin: a pin whose inputs or stock changed gets its turn.</summary>
    private void StimulateIdle(Pin pin)
    {
        if (IsStorageLike(pin)) RouteOutput(pin, new Dictionary<int, long>(pin.Contents));
        if (IsConsumer(pin)) pin.HasReceivedInputs = true;
        if (!IsActive(pin) && CanActivate(pin)) Schedule(pin);
    }

    // ----- power and CPU: ColonyData's totals and the pins' and links' usage -----

    public double CpuUsage => Pins.Values.Sum(p => PinCpuUsage(p)) + Links.Values.Sum(l => LinkCpuUsage(l));
    public double CpuSupply => Pins.Values.Sum(p => PinCpuOutput(p));
    public double PowerUsage => Pins.Values.Sum(p => PinPowerUsage(p)) + Links.Values.Sum(l => LinkPowerUsage(l));
    public double PowerSupply => Pins.Values.Sum(p => PinPowerOutput(p));

    public double PinCpuUsage(Pin p, int? heads = null) => p.Kind == PinKind.Ecu
        ? Catalog.Type(p.TypeId).Attr(PlanetAttr.CpuLoad) + Catalog.Type(p.TypeId).Attr(PlanetAttr.ExtractorHeadCpu) * (heads ?? p.Heads.Count)
        : Catalog.Type(p.TypeId).Attr(PlanetAttr.CpuLoad);

    public double PinPowerUsage(Pin p, int? heads = null) => p.Kind == PinKind.Ecu
        ? Catalog.Type(p.TypeId).Attr(PlanetAttr.PowerLoad) + Catalog.Type(p.TypeId).Attr(PlanetAttr.ExtractorHeadPower) * (heads ?? p.Heads.Count)
        : Catalog.Type(p.TypeId).Attr(PlanetAttr.PowerLoad);

    public double PinCpuOutput(Pin p) => p.Kind == PinKind.CommandCenter ? PlanetRules.CommandCenterCpu(Level) : Catalog.Type(p.TypeId).Attr(PlanetAttr.CpuOutput);

    public double PinPowerOutput(Pin p) => p.Kind == PinKind.CommandCenter ? PlanetRules.CommandCenterPower(Level) : Catalog.Type(p.TypeId).Attr(PlanetAttr.PowerOutput);

    /// <summary>GetCpuUsageForLink (planetCommon.py:58-61) with the link's length from the surface.</summary>
    public double LinkCpuUsage(Link l, int? level = null) => LinkUsage(l, level ?? l.Level, PlanetAttr.CpuLoad, PlanetAttr.CpuLoadPerKm, PlanetAttr.CpuLoadLevelModifier);

    public double LinkPowerUsage(Link l, int? level = null) => LinkUsage(l, level ?? l.Level, PlanetAttr.PowerLoad, PlanetAttr.PowerLoadPerKm, PlanetAttr.PowerLoadLevelModifier);

    private double LinkUsage(Link l, int level, int baseAttr, int perKmAttr, int modifierAttr)
    {
        var t = Catalog.Type(l.TypeId);
        var km = LinkLengthMeters(l) / 1000.0;
        return t.Attr(baseAttr) + Math.Ceiling(t.Attr(perKmAttr) * km * Math.Pow(level + 1.0, t.Attr(modifierAttr)));
    }

    public double LinkLengthMeters(Link l)
    {
        var a = Pins[l.End1];
        var b = Pins[l.End2];
        return DistanceBetween(a, b);
    }

    public double DistanceBetween(Pin a, Pin b) =>
        new SurfacePoint(Radius, a.Longitude, a.Latitude).DistanceTo(new SurfacePoint(Radius, b.Longitude, b.Latitude));

    /// <summary>The bandwidth a link carries at its level: the logistical capacity doubled per level (baseLink.py:113).</summary>
    public double LinkBandwidth(Link l, int? level = null) =>
        Catalog.Type(l.TypeId).Attr(PlanetAttr.LogisticalCapacity) * Math.Pow(2.0, level ?? l.Level);

    /// <summary>The bandwidth the routes through a link use now (baseLink.RemoveRoute / AddRoute, recomputed).</summary>
    public double LinkBandwidthUsage(Link l) => Routes.Values.Where(r => TransitsLink(r, l)).Sum(RouteBandwidth);

    private static bool TransitsLink(Route r, Link l)
    {
        for (var i = 1; i < r.Path.Count; i++)
        {
            var a = r.Path[i - 1];
            var b = r.Path[i];
            if ((a == l.End1 && b == l.End2) || (a == l.End2 && b == l.End1)) return true;
        }
        return false;
    }

    /// <summary>Route.GetBandwidthUsage (baseRoute.py:76-97).</summary>
    public double RouteBandwidth(Route r)
    {
        var volume = Catalog.Type(r.TypeId).Volume * r.Quantity;
        var cycle = RouteCycleTime(r);
        return cycle == 0 ? volume : PlanetRules.Bandwidth(volume, cycle);
    }

    private long RouteCycleTime(Route r)
    {
        var source = Pins[r.Source];
        if (IsProducer(source)) return CycleOf(source) ?? 0;
        if (IsStorageLike(source))
        {
            var dest = Pins[r.Destination];
            return IsConsumer(dest) ? CycleOf(dest) ?? 0 : 0;
        }
        return 0;
    }
}
