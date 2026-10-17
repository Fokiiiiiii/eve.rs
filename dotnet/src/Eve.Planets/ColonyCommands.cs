namespace Eve.Planets;

/// <summary>What a change is allowed to depend on besides the colony: the character's skills, planets and wallet.</summary>
/// <param name="PlanetsOwned">The character's colonies on other planets.</param>
/// <param name="ConsolidationLevel">Interplanetary Consolidation (2495) trained level, -1 when untrained.</param>
/// <param name="CommandCenterSkill">Command Center Upgrades (2505) trained level.</param>
/// <param name="Balance">The character's ISK balance.</param>
public sealed record ColonyContext(int PlanetsOwned, int ConsolidationLevel, int CommandCenterSkill, long Balance);

/// <summary>One change of the command stream (commandStream.py ids 1 to 13). Pin and route ids are real ids: the host maps the client's temporary ids first.</summary>
public abstract record ColonyCommand;
public sealed record CreatePinCommand(long PinId, int TypeId, double Latitude, double Longitude) : ColonyCommand;
public sealed record RemovePinCommand(long PinId) : ColonyCommand;
public sealed record CreateLinkCommand(long End1, long End2, int TypeId, int Level) : ColonyCommand;
public sealed record RemoveLinkCommand(long End1, long End2) : ColonyCommand;
public sealed record SetLinkLevelCommand(long End1, long End2, int Level) : ColonyCommand;
public sealed record CreateRouteCommand(long RouteId, IReadOnlyList<long> Path, int TypeId, long Quantity) : ColonyCommand;
public sealed record RemoveRouteCommand(long RouteId) : ColonyCommand;
public sealed record SetSchematicCommand(long PinId, int SchematicId) : ColonyCommand;
public sealed record UpgradeCommandCenterCommand(long PinId, int Level) : ColonyCommand;
public sealed record AddExtractorHeadCommand(long PinId, int HeadId, double Latitude, double Longitude) : ColonyCommand;
public sealed record KillExtractorHeadCommand(long PinId, int HeadId) : ColonyCommand;
public sealed record MoveExtractorHeadCommand(long PinId, int HeadId, double Latitude, double Longitude) : ColonyCommand;
public sealed record InstallProgramCommand(long PinId, int? TypeId, double HeadRadius) : ColonyCommand;
public sealed record GmForceInstallProgramCommand(long PinId, int TypeId, long CycleTime, long LifetimeTicks, long QtyPerCycle, double HeadRadius) : ColonyCommand;

/// <summary>The result of a batch: the ISK it costs (non-command pins, command center upgrades) and whether the command center went.</summary>
public sealed record CommandOutcome(long IskCost, bool Abandoned);

public sealed partial class Colony
{
    private const long NoCost = 0;

    /// <summary>
    /// UserUpdateNetwork: applies a batch in order, each change validated against the state the earlier ones left. A refusal
    /// rejects the whole batch (the caller discards the colony state it loaded). The colony must already be advanced to "now".
    /// </summary>
    public CommandOutcome ApplyCommands(IEnumerable<ColonyCommand> commands, ColonyContext ctx)
    {
        var cost = NoCost;
        var abandoned = false;
        foreach (var command in commands)
        {
            switch (command)
            {
                case CreatePinCommand c:
                    cost += CreatePin(c.PinId, c.TypeId, c.Latitude, c.Longitude, ctx);
                    break;
                case RemovePinCommand c:
                    if (RemovePin(c.PinId)) abandoned = true;
                    break;
                case CreateLinkCommand c:
                    CreateLink(c.End1, c.End2, c.TypeId, c.Level);
                    break;
                case RemoveLinkCommand c:
                    RemoveLink(c.End1, c.End2);
                    break;
                case SetLinkLevelCommand c:
                    SetLinkLevel(c.End1, c.End2, c.Level);
                    break;
                case CreateRouteCommand c:
                    CreateRoute(c.RouteId, c.Path, c.TypeId, c.Quantity);
                    break;
                case RemoveRouteCommand c:
                    RemoveRoute(c.RouteId);
                    break;
                case SetSchematicCommand c:
                    SetSchematic(c.PinId, c.SchematicId);
                    break;
                case UpgradeCommandCenterCommand c:
                    cost += UpgradeCommandCenter(c.PinId, c.Level, ctx);
                    break;
                case AddExtractorHeadCommand c:
                    AddExtractorHead(c.PinId, c.HeadId, c.Latitude, c.Longitude);
                    break;
                case KillExtractorHeadCommand c:
                    KillExtractorHead(c.PinId, c.HeadId);
                    break;
                case MoveExtractorHeadCommand c:
                    MoveExtractorHead(c.PinId, c.HeadId, c.Latitude, c.Longitude);
                    break;
                case InstallProgramCommand c:
                    InstallProgram(c.PinId, c.TypeId, c.HeadRadius);
                    break;
                case GmForceInstallProgramCommand c:
                    GmForceInstallProgram(c.PinId, c.TypeId, c.CycleTime, c.LifetimeTicks, c.QtyPerCycle, c.HeadRadius);
                    break;
                default:
                    throw new PlanetRefusal("InvalidCommand");
            }
            if (abandoned) break;
        }
        if (abandoned)
        {
            Pins.Clear();
            Links.Clear();
            Routes.Clear();
            return new CommandOutcome(cost, true);
        }
        if (cost > ctx.Balance) throw new PlanetRefusal("CannotBuildNotEnoughCash");
        return new CommandOutcome(cost, false);
    }

    private static double ClampLatitude(double latitude) => Math.Min(Math.PI, Math.Max(-Math.PI, latitude));

    private static double ClampLongitude(double longitude) => Math.Abs(longitude % (2 * Math.PI));

    private Pin PinOf(long pinId) =>
        Pins.TryGetValue(pinId, out var pin) ? pin : throw new PlanetRefusal("PinDoesNotExist");

    private Pin EcuOf(long pinId)
    {
        var pin = PinOf(pinId);
        return pin.Kind == PinKind.Ecu ? pin : throw new PlanetRefusal("PinDoesNotHaveHeads");
    }

    private static (long A, long B) Ordered(long a, long b) => a > b ? (b, a) : (a, b);

    // ----- pins -----

    /// <summary>
    /// CreatePin (clientPlanet.CreatePin, baseColony.ValidateCreatePin, clientColony.PostValidateCreatePin). Returns the ISK it
    /// costs (the base price; the command center is an item and costs nothing).
    /// </summary>
    public long CreatePin(long pinId, int typeId, double latitude, double longitude, ColonyContext ctx)
    {
        var kind = Catalog.KindOf(typeId) ?? throw new PlanetRefusal("InvalidPinType");
        if (Pins.ContainsKey(pinId)) throw new PlanetRefusal("PinAlreadyExists");
        var t = Catalog.Type(typeId);
        if (kind == PinKind.CommandCenter)
        {
            if (CommandCenter is not null) throw new PlanetRefusal("CannotBuildMultipleCommandPins");
            if (ctx.PlanetsOwned > 0)
            {
                if (ctx.ConsolidationLevel < 0) throw new PlanetRefusal("CannotPlaceCommandCenterNotEnoughSkill", "maxPlanets 1");
                if (ctx.ConsolidationLevel < ctx.PlanetsOwned)
                    throw new PlanetRefusal("CannotPlaceCommandCenterNotEnoughSkill", $"maxPlanets {ctx.PlanetsOwned}");
            }
        }
        else if (CommandCenter is null)
        {
            throw new PlanetRefusal("CannotManagePlanetWithoutCommandCenter");
        }
        var restriction = t.Attr(PlanetAttr.PlanetRestriction, -1);
        if (t.HasAttr(PlanetAttr.PlanetRestriction) && (int)restriction != PlanetTypeId)
            throw new PlanetRefusal("CannotCreatePinWrongPlanetType");
        var cpuUsage = t.Attr(PlanetAttr.CpuLoad);
        var powerUsage = t.Attr(PlanetAttr.PowerLoad);
        if (t.Attr(PlanetAttr.CpuOutput) <= 0 && cpuUsage + CpuUsage > CpuSupply)
            throw new PlanetRefusal("CannotAddToColonyCPUUsageExceeded", t.TypeId.ToString());
        if (t.Attr(PlanetAttr.PowerOutput) <= 0 && powerUsage + PowerUsage > PowerSupply)
            throw new PlanetRefusal("CannotAddToColonyPowerUsageExceeded", t.TypeId.ToString());

        var pin = new Pin { Id = pinId, TypeId = typeId, Kind = kind, OwnerId = OwnerId, LastRunTime = null, State = 0 };
        pin.Latitude = ClampLatitude(latitude);
        pin.Longitude = ClampLongitude(longitude);
        if (kind == PinKind.CommandCenter)
        {
            Level = 0;
            pin.LastRunTime = null;
        }
        Pins[pinId] = pin;
        return kind == PinKind.CommandCenter ? NoCost : t.BasePrice;
    }

    /// <summary>
    /// RemovePin. A command center removal abandons the colony (every pin, link and route goes): returns true then.
    /// </summary>
    public bool RemovePin(long pinId)
    {
        var pin = PinOf(pinId);
        if (pin.Kind == PinKind.CommandCenter) return true;
        foreach (var other in LinksOf(pinId).ToList()) RemoveLink(other.End1, other.End2);
        Pins.Remove(pinId);
        return false;
    }

    private IEnumerable<Link> LinksOf(long pinId) => Links.Values.Where(l => l.End1 == pinId || l.End2 == pinId).OrderBy(l => l.End1).ThenBy(l => l.End2);

    // ----- links -----

    /// <summary>CreateLink (baseColony.ValidateCreateLink). The new link's CPU and power must fit the colony.</summary>
    public void CreateLink(long end1, long end2, int typeId, int level)
    {
        (end1, end2) = Ordered(end1, end2);
        if (!Catalog.IsLink(typeId)) throw new PlanetRefusal("InvalidLinkType");
        if (CommandCenter is null) throw new PlanetRefusal("CannotManagePlanetWithoutCommandCenter");
        if (Links.ContainsKey((end1, end2))) throw new PlanetRefusal("LinkAlreadyExists");
        if (!Pins.TryGetValue(end1, out var a) || !Pins.TryGetValue(end2, out var b)) throw new PlanetRefusal("PinDoesNotExist");
        if (end1 == end2) throw new PlanetRefusal("CannotLinkPinsToThemselves");
        var link = new Link { End1 = end1, End2 = end2, TypeId = typeId, Level = level };
        var addCpu = LinkCpuUsage(link, level);
        var addPower = LinkPowerUsage(link, level);
        if (addCpu + CpuUsage > CpuSupply) throw new PlanetRefusal("CannotAddToColonyCPUUsageExceeded", Catalog.Type(typeId).TypeId.ToString());
        if (addPower + PowerUsage > PowerSupply) throw new PlanetRefusal("CannotAddToColonyPowerUsageExceeded", Catalog.Type(typeId).TypeId.ToString());
        Links[(end1, end2)] = link;
    }

    /// <summary>RemoveLink: the routes that transit it go first.</summary>
    public void RemoveLink(long end1, long end2)
    {
        (end1, end2) = Ordered(end1, end2);
        if (!Links.TryGetValue((end1, end2), out var link)) return;
        foreach (var route in Routes.Values.Where(r => TransitsLink(r, link)).Select(r => r.Id).ToList()) RemoveRoute(route);
        Links.Remove((end1, end2));
    }

    /// <summary>SetLinkLevel (baseColony.ValidateSetLinkLevel): a raise must fit the colony's CPU and power.</summary>
    public void SetLinkLevel(long end1, long end2, int level)
    {
        (end1, end2) = Ordered(end1, end2);
        if (!Links.TryGetValue((end1, end2), out var link)) throw new PlanetRefusal("PinDoesNotExist");
        if (level > PlanetRules.LinkMaxLevel) throw new PlanetRefusal("CannotUpgradeLinkAlreadyMaxed");
        if (level > link.Level)
        {
            var cpuDelta = LinkCpuUsage(link, level) - LinkCpuUsage(link, link.Level);
            var powerDelta = LinkPowerUsage(link, level) - LinkPowerUsage(link, link.Level);
            if (cpuDelta + CpuUsage > CpuSupply) throw new PlanetRefusal("CannotAddToColonyCPUUsageExceeded");
            if (powerDelta + PowerUsage > PowerSupply) throw new PlanetRefusal("CannotAddToColonyPowerUsageExceeded");
        }
        link.Level = level;
    }

    // ----- routes -----

    /// <summary>CreateRoute (baseColony.ValidateCreateRoute, then PrimeRoute and OnRouteCreated).</summary>
    public void CreateRoute(long routeId, IReadOnlyList<long> path, int typeId, long quantity)
    {
        ValidateRoute(path, typeId, quantity);
        if (Routes.ContainsKey(routeId)) throw new PlanetRefusal("RouteAlreadyExists");
        var route = new Route { Id = routeId, OwnerId = OwnerId, TypeId = typeId, Quantity = quantity, Path = [.. path] };
        Routes[routeId] = route;
        OnRouteCreated(route);
    }

    private void ValidateRoute(IReadOnlyList<long> path, int typeId, long quantity)
    {
        if (typeId <= 0 || quantity < 1) throw new PlanetRefusal("CreateRouteWithoutCommodities");
        if (path.Count < 2) throw new PlanetRefusal("CreateRouteTooShort");
        if (path.Count - 2 > PlanetRules.MaxWaypoints) throw new PlanetRefusal("CannotRouteTooManyWaypoints");
        var source = PinOf(path[0]);
        var dest = PinOf(path[^1]);
        if (IsStorageLike(source) && !IsConsumer(dest)) throw new PlanetRefusal("CannotRouteStorageMustGoToConsumer");
        if (!IsConsumer(dest) && !IsStorageLike(dest)) throw new PlanetRefusal("CannotRouteDestinationWillNotAccept");
        if (IsProducer(source))
        {
            var used = Routes.Values.Where(r => r.Source == source.Id).Sum(r => r.TypeId == typeId ? r.Quantity : 0);
            var products = ProductsOf(source);
            if (!products.TryGetValue(typeId, out var made)) throw new PlanetRefusal("CreateRouteCommodityNotProduced");
            if (used + quantity > made) throw new PlanetRefusal("CreateRouteCommodityProductionTooSmall");
        }
        if (IsConsumer(dest) && !dest.Demands.ContainsKey(typeId))
            throw new PlanetRefusal("CreateRouteDestinationCannotAcceptCommodity");
        var volume = Catalog.Type(typeId).Volume * quantity;
        long cycle;
        if (IsProducer(source)) cycle = CycleOf(source) ?? 0;
        else if (IsStorageLike(source) && IsConsumer(dest)) cycle = CycleOf(dest) ?? 0;
        else cycle = Math.Max(CycleOf(source) ?? 0, CycleOf(dest) ?? 0);
        var additional = cycle != 0 ? PlanetRules.Bandwidth(volume, cycle) : volume;
        for (var i = 0; i < path.Count; i++)
        {
            var pin = PinOf(path[i]);
            if (pin.OwnerId != OwnerId) throw new PlanetRefusal("RouteFailedValidationPinNotYours");
            if (i == 0) continue;
            if (!Links.TryGetValue(Ordered(path[i - 1], path[i]), out var link))
                throw new PlanetRefusal("RouteFailedValidationLinkDoesNotExist");
            if (LinkBandwidthUsage(link) + additional > LinkBandwidth(link))
                throw new PlanetRefusal("RouteFailedValidationCannotRouteCommodities");
        }
    }

    private Dictionary<int, long> ProductsOf(Pin p)
    {
        if (p.Kind == PinKind.Ecu) return p.ProgramType is { } t ? new() { [t] = p.QtyPerCycle } : [];
        return new Dictionary<int, long>(p.Products);
    }

    /// <summary>RemoveRoute</summary>
    public void RemoveRoute(long routeId)
    {
        if (!Routes.Remove(routeId)) throw new PlanetRefusal("RouteDoesNotExist");
    }

    // ----- processors -----

    /// <summary>SetSchematic (ProcessPin.InstallSchematic, SetSchematic, StimulateIdlePin, then the obsolete routes go).</summary>
    public void SetSchematic(long pinId, int schematicId)
    {
        var pin = PinOf(pinId);
        if (pin.Kind != PinKind.Processor) throw new PlanetRefusal("CannotAssignSchematicToPinType");
        var s = Catalog.Schematic(schematicId) ?? throw new PlanetRefusal("InvalidSchematic");
        if (!s.PinTypes.Contains(pin.TypeId)) throw new PlanetRefusal("CannotAssignSchematicToPinType");
        pin.Demands.Clear();
        pin.Products.Clear();
        foreach (var (type, qty) in s.Inputs) pin.Demands[type] = qty;
        foreach (var (type, qty) in s.Outputs) pin.Products[type] = qty;
        pin.SchematicId = schematicId;
        pin.ProcessCycle = (long)(s.CycleSeconds * PlanetRules.Sec);
        var kept = pin.Contents.Where(kv => pin.Demands.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => Math.Min(kv.Value, pin.Demands[kv.Key]));
        pin.Contents.Clear();
        foreach (var kv in kept) pin.Contents[kv.Key] = kv.Value;
        pin.State = 0;
        StimulateIdle(pin);
        foreach (var route in Routes.Values.ToList())
        {
            if (route.Source == pin.Id && !pin.Products.ContainsKey(route.TypeId)) RemoveRoute(route.Id);
            else if (route.Destination == pin.Id && !pin.Demands.ContainsKey(route.TypeId)) RemoveRoute(route.Id);
        }
    }

    /// <summary>UpgradeCommandCenter: the level rises, one step at a time, within the skill. Returns the ISK it costs.</summary>
    public long UpgradeCommandCenter(long pinId, int level, ColonyContext ctx)
    {
        var pin = PinOf(pinId);
        if (pin.Kind != PinKind.CommandCenter) throw new PlanetRefusal("CanOnlyLaunchFromCommandCenters");
        if (level <= Level) throw new PlanetRefusal("CannotDowngradeCommandCenter");
        if (level > PlanetRules.CommandCenterMaxLevel) throw new PlanetRefusal("CannotUpgradeCommandCenterAboveMax");
        if (level > ctx.CommandCenterSkill) throw new PlanetRefusal("CantUpgradeCommandCenterSkillRequired");
        var cost = PlanetRules.UpgradeCost(Level, level);
        Level = level;
        return cost;
    }

    // ----- extractor heads and programs -----

    private double AreaOfInfluence(Pin ecu) => Catalog.Type(ecu.TypeId).Attr(PlanetAttr.EcuAreaOfInfluence);

    private void CheckHeadPosition(Pin ecu, double latitude, double longitude)
    {
        if (latitude < 0 || latitude > Math.PI) throw new PlanetRefusal("InvalidHeadPosition");
        if (longitude < 0 || longitude > 2 * Math.PI) throw new PlanetRefusal("InvalidHeadPosition");
        var angle = new SurfacePoint(Radius, ecu.Longitude, ecu.Latitude).AngleTo(new SurfacePoint(Radius, longitude, latitude));
        if (angle > AreaOfInfluence(ecu)) throw new PlanetRefusal("CannotPlaceHeadTooFarAway");
    }

    /// <summary>AddExtractorHead (baseColony.ValidateAddExtractorHead). Heads are kept sorted by id.</summary>
    public void AddExtractorHead(long pinId, int headId, double latitude, double longitude)
    {
        var ecu = EcuOf(pinId);
        if (ecu.Heads.Any(h => h.Id == headId)) throw new PlanetRefusal("CannotAddHeadAlreadyExists");
        if (ecu.Heads.Count >= PlanetRules.EcuMaxHeads) throw new PlanetRefusal("CannotPlaceHeadLimitReached");
        if (IsActive(ecu)) throw new PlanetRefusal("PinProgramActive");
        var cpuDelta = PinCpuUsage(ecu, ecu.Heads.Count + 1) - PinCpuUsage(ecu);
        var powerDelta = PinPowerUsage(ecu, ecu.Heads.Count + 1) - PinPowerUsage(ecu);
        if (cpuDelta + CpuUsage > CpuSupply) throw new PlanetRefusal("CannotAddToColonyCPUUsageExceeded", "ExtractorHead");
        if (powerDelta + PowerUsage > PowerSupply) throw new PlanetRefusal("CannotAddToColonyPowerUsageExceeded", "ExtractorHead");
        CheckHeadPosition(ecu, latitude, longitude);
        ecu.Heads.Add(new Head(headId, latitude, longitude));
        ecu.Heads.Sort((x, y) => x.Id.CompareTo(y.Id));
    }

    /// <summary>The lowest head id not taken (ECU_MAX_HEADS ids).</summary>
    public int NextHeadId(long pinId)
    {
        var ecu = EcuOf(pinId);
        for (var i = 0; i < PlanetRules.EcuMaxHeads; i++)
            if (ecu.Heads.All(h => h.Id != i)) return i;
        throw new PlanetRefusal("CannotInstallMoreExtractors");
    }

    public void KillExtractorHead(long pinId, int headId)
    {
        var ecu = EcuOf(pinId);
        if (ecu.Heads.All(h => h.Id != headId)) throw new PlanetRefusal("CannotRemoveHeadNotPresent");
        if (IsActive(ecu)) throw new PlanetRefusal("PinProgramActive");
        ecu.Heads.RemoveAll(h => h.Id == headId);
    }

    public void MoveExtractorHead(long pinId, int headId, double latitude, double longitude)
    {
        var ecu = EcuOf(pinId);
        var index = ecu.Heads.FindIndex(h => h.Id == headId);
        if (index < 0) throw new PlanetRefusal("CannotMoveHeadNotPresent");
        CheckHeadPosition(ecu, latitude, longitude);
        ecu.Heads[index] = new Head(headId, latitude, longitude);
    }

    /// <summary>
    /// GetProgramResultInfo: the amount a program distributes per cycle, its cycle time and its number of cycles, for the heads
    /// given, against the other programs of the same resource on this colony.
    /// </summary>
    public ProgramMath.Result ProgramResult(long pinId, int resourceTypeId, IReadOnlyList<Head> heads, double headRadius)
    {
        var ecu = EcuOf(pinId);
        var t = Catalog.Type(ecu.TypeId);
        var others = Pins.Values
            .Where(p => p.Kind == PinKind.Ecu && p.Id != pinId && p.ProgramType == resourceTypeId)
            .Select(p => (Radius: p.HeadRadius > 0 ? p.HeadRadius : 0.01, Heads: (IReadOnlyList<Head>)p.Heads))
            .ToList();
        return ProgramMath.Create(PlanetId, resourceTypeId, heads, headRadius, t.Attr(PlanetAttr.EcuOverlapFactor),
            t.Attr(PlanetAttr.EcuMaxVolume), Field, others);
    }

    /// <summary>
    /// InstallProgram (clientPlanet.InstallProgram). A null type clears the program. A program that replaces another
    /// re-creates the routes of the commodity from the output it now makes (the client's own rerouting).
    /// </summary>
    public void InstallProgram(long pinId, int? typeId, double headRadius)
    {
        var ecu = EcuOf(pinId);
        // a program is refused until its install cooldown ends, for a change and for a stop alike (ecuPin.CanInstallProgram)
        if (ecu.ProgramType is not null && ecu.ExpiryTime > CurrentSimTime && ecu.InstallTime + PlanetRules.ProgramInstallCooldown > CurrentSimTime)
            throw new PlanetRefusal("CantInstallProgramNeedsCooldown");
        if (ecu.Heads.Count < 1) typeId = null;
        if (headRadius < PlanetRules.RadiusMin || headRadius > PlanetRules.RadiusMax) throw new PlanetRefusal("InvalidProgramRadius");

        long qtyToDistribute = 0;
        long cycle = 0;
        int numCycles = 0;
        if (typeId is { } type)
        {
            var result = ProgramResult(pinId, type, ecu.Heads, headRadius);
            (qtyToDistribute, cycle, numCycles) = (result.MaxValue, result.CycleTime, result.NumCycles);
        }
        var qtyToRoute = typeId is null ? 0 : (long)PlanetRules.EcuMaxOutput(qtyToDistribute, cycle, Catalog.Type(ecu.TypeId).Attr(PlanetAttr.EcuNoiseFactor));
        var routes = Routes.Values.Where(r => r.Source == pinId).OrderBy(r => r.Id).ToList();

        if (typeId is null)
        {
            ClearProgram(ecu);
            return;
        }
        var storageRouteQty = new List<(long Id, long Qty)>();
        var processRouteQty = new List<(long Id, long Qty)>();
        var pathsByID = new Dictionary<long, IReadOnlyList<long>>();
        long formerQtyToStorage = 0;
        foreach (var route in routes)
        {
            if (typeId != route.TypeId)
            {
                RemoveRoute(route.Id);
                continue;
            }
            if (!Pins.TryGetValue(route.Destination, out var dest)) continue;
            if (!IsStorageLike(dest) && !IsConsumer(dest)) continue;
            pathsByID[route.Id] = route.Path;
            if (IsStorageLike(dest))
            {
                storageRouteQty.Add((route.Id, route.Quantity));
                formerQtyToStorage += route.Quantity;
            }
            else processRouteQty.Add((route.Id, route.Quantity));
            RemoveRoute(route.Id);
        }

        InstallProgramOn(ecu, type: typeId.Value, headRadius, qtyToDistribute, cycle, numCycles);
        if (processRouteQty.Count + storageRouteQty.Count == 0) return;

        foreach (var (routeId, formerQty) in processRouteQty)
        {
            var amount = formerQty;
            if (qtyToRoute <= 0) break;
            if (qtyToRoute < formerQty) amount = qtyToRoute;
            var path = pathsByID[routeId];
            if (TryCreateRoute(path, typeId.Value, amount)) qtyToRoute -= amount;
        }

        var storageTotalLeft = qtyToRoute;
        if (formerQtyToStorage > 0)
        {
            double qtyRemainder = 0.0;
            foreach (var (routeId, formerQty) in storageRouteQty)
            {
                if (qtyToRoute <= 0) break;
                var floatAmount = (double)formerQty / formerQtyToStorage * storageTotalLeft;
                var amount = (long)PlanetRules.PyRound(floatAmount);
                qtyRemainder += floatAmount - amount;
                amount += (long)PlanetRules.PyRound(qtyRemainder);
                var path = pathsByID[routeId];
                amount = RoutableQuantity(path, typeId.Value, amount, cycle);
                qtyRemainder -= PlanetRules.PyRound(qtyRemainder);
                if (TryCreateRoute(path, typeId.Value, amount)) qtyToRoute -= amount;
            }
        }
    }

    private bool TryCreateRoute(IReadOnlyList<long> path, int typeId, long amount)
    {
        try
        {
            CreateRoute(NewRouteId(), path, typeId, amount);
            return true;
        }
        catch (PlanetRefusal)
        {
            return false;
        }
    }

    private void ClearProgram(Pin ecu)
    {
        ecu.ProgramType = null;
        ecu.QtyPerCycle = 0;
        ecu.ExpiryTime = null;
        ecu.InstallTime = null;
        ecu.CycleTime = 0;
        ecu.State = 0;
    }

    /// <summary>
    /// GMForceInstallProgram (the client's GM deposit designer, clientPlanet.GMInstallProgram): the program starts now with the given cycle,
    /// output and lifetime. No head, install cooldown or resource check applies; routes of another commodity from this unit are removed.
    /// </summary>
    public void GmForceInstallProgram(long pinId, int typeId, long cycleTime, long lifetimeTicks, long qtyPerCycle, double headRadius)
    {
        var ecu = EcuOf(pinId);
        if (cycleTime <= 0 || lifetimeTicks <= 0 || qtyPerCycle < 0 || headRadius <= 0) throw new PlanetRefusal("InvalidCommand");
        foreach (var route in Routes.Values.Where(r => r.Source == pinId && r.TypeId != typeId).OrderBy(r => r.Id).ToList()) RemoveRoute(route.Id);
        InstallProgramOn(ecu, typeId, headRadius, qtyPerCycle, cycleTime, 0);
        ecu.ExpiryTime = CurrentSimTime + lifetimeTicks;
    }

    private void InstallProgramOn(Pin ecu, int type, double headRadius, long qtyPerCycle, long cycle, int numCycles)
    {
        var start = CurrentSimTime;
        ecu.ProgramType = type;
        ecu.QtyPerCycle = qtyPerCycle;
        ecu.CycleTime = cycle;
        ecu.ExpiryTime = start + cycle * numCycles;
        ecu.HeadRadius = headRadius;
        ecu.State = 1;
        ecu.LastRunTime = start;
        ecu.InstallTime = start;
        Schedule(ecu);
    }

    /// <summary>FindRoutableQuantityOfResource (clientPlanet.py:611): what the path's narrowest link can carry at this cycle.</summary>
    private long RoutableQuantity(IReadOnlyList<long> path, int typeId, long quantity, long cycle)
    {
        double? minimum = null;
        for (var i = 1; i < path.Count; i++)
        {
            var link = Links[Ordered(path[i - 1], path[i])];
            var available = LinkBandwidth(link) - LinkBandwidthUsage(link);
            if (minimum is null || available < minimum) minimum = available;
        }
        var volume = Catalog.Type(typeId).Volume;
        var required = PlanetRules.Bandwidth(volume * quantity, cycle);
        if (minimum is not null && required > minimum)
            quantity = (long)(minimum.Value * cycle / (PlanetRules.Hour * volume));
        return quantity;
    }

    // ----- launches and expedited transfers -----

    /// <summary>
    /// UserLaunchCommodities: takes the commodities out of the command center and records the launch. Returns the launch time.
    /// </summary>
    public long Launch(long commandPinId, IReadOnlyDictionary<int, long> commodities)
    {
        var pin = PinOf(commandPinId);
        if (pin.Kind != PinKind.CommandCenter) throw new PlanetRefusal("CanOnlyLaunchFromCommandCenters");
        if (commodities.Count < 1) throw new PlanetRefusal("PleaseSelectCommoditiesToLaunch");
        if (pin.LastLaunchTime is { } last && last + PlanetRules.LaunchCooldown > CurrentSimTime)
            throw new PlanetRefusal("CannotLaunchCommandPinNotReady");
        foreach (var (type, _) in commodities)
            if (pin.Contents.GetValueOrDefault(type) < 1) throw new PlanetRefusal("CannotLaunchCommoditiesNotFound");
        foreach (var (type, qty) in commodities) RemoveCommodity(pin, type, qty);
        pin.LastLaunchTime = CurrentSimTime;
        return CurrentSimTime;
    }

    /// <summary>
    /// UserTransferCommodities (expedited): moves from a storage pin at the end of the path, at once, and locks the source for the
    /// transfer time. Returns (time, the source's next time).
    /// </summary>
    public (long SimTime, long SourceRunTime) ExpeditedTransfer(IReadOnlyList<long> path, IReadOnlyDictionary<int, long> commodities)
    {
        if (path.Count < 2) throw new PlanetRefusal("CreateRouteTooShort");
        if (commodities.Count < 1) throw new PlanetRefusal("CreateRouteWithoutCommodities");
        var source = PinOf(path[0]);
        var dest = PinOf(path[^1]);
        if (!IsStorageLike(source)) throw new PlanetRefusal("RouteFailedValidationExpeditedSourceNotStorage");
        foreach (var (type, qty) in commodities)
        {
            if (!source.Contents.TryGetValue(type, out var have)) throw new PlanetRefusal("RouteFailedValidationExpeditedSourceLacksCommodity");
            if (qty > have) throw new PlanetRefusal("RouteFailedValidationExpeditedSourceLacksCommodityQty");
            if (CanAccept(dest, type, qty) < 1) throw new PlanetRefusal("RouteFailedValidationExpeditedDestinationCannotAccept");
        }
        double? minimum = null;
        for (var i = 1; i < path.Count; i++)
        {
            if (!Links.TryGetValue(Ordered(path[i - 1], path[i]), out var link)) throw new PlanetRefusal("RouteFailedValidationLinkDoesNotExist");
            var total = LinkBandwidth(link);
            if (minimum is null || total < minimum) minimum = total;
        }
        if (minimum is null || minimum < 0) throw new PlanetRefusal("RouteFailedValidationNoBandwidthAvailable");
        if (source.LastRunTime is { } next && next > CurrentSimTime) throw new PlanetRefusal("RouteFailedValidationExpeditedSourceNotReady");

        var runTime = CurrentSimTime;
        var moved = new Dictionary<int, long>();
        foreach (var (type, qty) in commodities)
        {
            var amount = Math.Min(source.Contents[type], qty);
            if (amount < 1) continue;
            moved[type] = AddCommodity(dest, type, amount);
        }
        foreach (var (type, amount) in moved) RemoveCommodity(source, type, amount);
        var volume = moved.Sum(kv => Catalog.Type(kv.Key).Volume * kv.Value);
        source.LastRunTime = runTime + PlanetRules.ExpeditedTransferTime(minimum.Value, volume);
        StimulateIdle(dest);
        return (runTime, source.LastRunTime.Value);
    }

    // ----- customs: the launchpad's side of an import or export -----

    /// <summary>A launchpad takes a commodity from a customs office; the volume must fit (spaceportPin.CanImportCommodities).</summary>
    public void ImportToLaunchpad(long pinId, int typeId, long quantity)
    {
        var pad = PinOf(pinId);
        if (pad.Kind != PinKind.Launchpad) throw new PlanetRefusal("CannotImportEndpointNotFound");
        if (quantity < 1) throw new PlanetRefusal("PleaseSelectCommoditiesToImport");
        var volume = Catalog.Type(typeId).Volume * quantity;
        if (UsedVolume(pad) + volume > (Capacity(pad) ?? 0)) throw new PlanetRefusal("CannotImportCapacityExceeded");
        AddCommodity(pad, typeId, quantity);
    }

    /// <summary>A launchpad gives a commodity to a customs office; the launchpad must hold it.</summary>
    public void ExportFromLaunchpad(long pinId, int typeId, long quantity)
    {
        var pad = PinOf(pinId);
        if (pad.Kind != PinKind.Launchpad) throw new PlanetRefusal("CannotImportEndpointNotFound");
        if (quantity < 1 || pad.Contents.GetValueOrDefault(typeId) < quantity) throw new PlanetRefusal("RouteFailedValidationExpeditedSourceLacksCommodityQty");
        RemoveCommodity(pad, typeId, quantity);
    }

    // ----- helpers for the host -----

    /// <summary>Route removal of a pin's routes, for an abandoned colony's or a removed pin's links (exposed for tests).</summary>
    public IReadOnlyList<Route> RoutesFrom(long pinId) => Routes.Values.Where(r => r.Source == pinId).OrderBy(r => r.Id).ToList();

    /// <summary>The resource type of the active program of a pin, or null.</summary>
    public int? ProgramOf(long pinId) => Pins.TryGetValue(pinId, out var p) ? p.ProgramType : null;

    /// <summary>The volume a commodity list takes (planetCommon.GetCommodityTotalVolume).</summary>
    public double VolumeOf(IReadOnlyDictionary<int, long> commodities) => commodities.Sum(kv => Catalog.Type(kv.Key).Volume * kv.Value);

    private bool TryGetCommandCenterCount(out int count)
    {
        count = Pins.Values.Count(p => p.Kind == PinKind.CommandCenter);
        return count > 0;
    }
}
