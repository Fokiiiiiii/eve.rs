using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// The holds of a ship: how much each takes and holds, and the stacks in them. What the reference answers for the cargo hold of a ship in a hangar, as recorded
/// (<c>oracle/fixtures/scenarios/cargo.json</c>): the capacity is the ship's own (dogma's capacity, skills and modules included), what is in the hold takes the volume of
/// each unit, and what does not fit is refused with the room that is left and the room that was asked for.
/// </summary>
/// <summary>The answer to one mining cycle. <c>Mined</c> is the yield of a cycle that was granted now (null for a refusal and for a cycle answered from its record).</summary>
public sealed record DroneMiningCommit(
    bool Accepted, string Reason, long UnitsRemaining, long DeliveredUnits, bool Depleted, ShipChanged? Change, MiningResult? Mined = null, int OreTypeId = 0);

/// <summary>The numbers of one mining cycle as the miner has them: the volume it mines, the waste and the critical yield (a chance is 0 to 1, a waste chance a percent).</summary>
public sealed record MiningNumbers(double YieldM3, double WasteVolumeMultiplier, double WasteProbability, double CritProbability, double CritQuantityMultiplier);

/// <summary>
/// The fact of a granted mining cycle (topic <see cref="Topic"/>), written in the grant's transaction: the mining ledger and the other readers of mining
/// take it from the event bus. <c>Quantity</c> is what went to the hold, <c>QuantityWasted</c> what the rock lost and nobody kept, and <c>QuantityCritical</c>
/// the critical bonus (as EVE.js records them). <c>MinerTypeId</c> is the module's type (0 for a drone).
/// </summary>
public sealed record OreMinedEvent(
    long CharacterId, int CorporationId, int SolarSystemId, long ShipId, int ShipTypeId, long MinerId, int MinerTypeId, MiningSource Source,
    int OreTypeId, string YieldKind, long Quantity, long QuantityWasted, long QuantityCritical)
{
    public const string Topic = "mining.ore_mined";
}

/// <summary>The fact of a granted salvage cycle (<c>salvage.wreck_salvaged</c>), written in the grant's transaction; <c>ContainerId</c> is 0 when no can was left.</summary>
public sealed record SalvageWreckedEvent(
    long CharacterId, int SolarSystemId, long ShipId, long MinerId, MiningSource Source, long WreckId, int WreckTypeId, int ProductTypeId, long ContainerId)
{
    public const string Topic = "salvage.wreck_salvaged";
}

/// <summary>The fact of a salvage cycle whose roll failed (<c>salvage.salvage_failed</c>), written with the answer that records the cycle.</summary>
public sealed record SalvageFailedEvent(long CharacterId, int SolarSystemId, long ShipId, long MinerId, MiningSource Source, long WreckId, int WreckTypeId)
{
    public const string Topic = "salvage.salvage_failed";
}
public sealed record DroneSalvageCommit(bool Accepted, string Reason, ShipChanged? Change, Item? Container, SpaceItem? ContainerSpaceItem);

/// <summary>
/// The numbers of a mining cycle, read from the dogma of the item that mines (a drone or a module) by attribute name, so every source reads them the
/// same way. An attribute name the static data does not define reads as 0; any other value is the world's (the item's own, else the default).
/// </summary>
public sealed record MiningProfile(
    double MiningAmountM3, double CycleTimeMs, double WasteVolumeMultiplier, double WasteProbability, double AccessDifficultyBonus,
    double CritProbability = 0, double CritQuantityMultiplier = 0)
{
    /// <summary>The profile of the item <paramref name="itemId"/> in <paramref name="world"/> (its dogma after its own modifiers).</summary>
    public static MiningProfile From(DogmaWorld world, DogmaStatic statics, long itemId)
    {
        double Read(string attributeName) => statics.AttributeId(attributeName) is { } attribute ? world.Get(itemId, attribute) : 0d;
        return new(
            Read("miningAmount"),
            Read("duration"),
            Read("miningWastedVolumeMultiplier"),
            Read("miningWasteProbability"),
            Read("accessDifficultyBonus"),
            Read("miningCritChance"),
            Read("miningCritBonusYield"));
    }
}

public sealed partial class CharacterShips
{
    /// <summary>The drone bay of a ship: it holds drones (category 18) and nothing else, up to its drone capacity (<c>NotEnoughDroneBaySpace</c> when they do not fit).</summary>
    public const int DroneBayFlag = 87;
    public const int FighterBayFlag = 158;
    public const int FrigateEscapeBayFlag = 179;
    public const int ShipMaintenanceBayFlag = 90;
    public const int FleetHangarFlag = 155;

    private const int DroneCategory = 18;


    /// <summary>The room that is left in a hold is not worked out finer than this (the volumes are sums of floats).</summary>
    private const double RoomTolerance = 1e-7;

    /// <summary>How big a hold is, and how much is in it.</summary>
    public readonly record struct Room(double Capacity, double Used)
    {
        public double Free => Math.Max(0, Capacity - Used);
    }

    /// <summary>The attribute that says how big a bay is (a bay the ship has no word for holds nothing).</summary>
private sealed record ShipHoldRule(
        string AttributeName,
        string[]? Categories = null,
        string[]? Groups = null,
        string[]? BlockedGroups = null,
        string[]? Types = null,
        string[]? BlockedTypes = null);

    // Flags, attributes, categories, groups, and types match the pinned client inventoryFlagsCommon.ShouldAllowAdd table.
    private static readonly IReadOnlyDictionary<int, ShipHoldRule> ShipHoldRules = new Dictionary<int, ShipHoldRule>
    {
        [5] = new("capacity"),
        [87] = new("droneCapacity", Categories: ["Drone"]),
        [158] = new("fighterCapacity", Categories: ["Fighter"]),
        [179] = new("frigateEscapeBayCapacity", Groups: ["Frigate", "Assault Frigate", "Logistics Frigate", "Electronic Attack Ship"], BlockedGroups: ["Capsule"]),
        [90] = new("shipMaintenanceBayCapacity", Categories: ["Ship"], BlockedGroups: ["Capsule"]),
        [155] = new("fleetHangarCapacity"),
        [133] = new("specialFuelBayCapacity", Groups: ["Ice Product", "Fuel Block"]),
        [134] = new("generalMiningHoldCapacity", Categories: ["Asteroid"], Groups: ["Harvestable Cloud", "Compressed Gas", "Prismaticite"], BlockedGroups: ["Scalable Decorative Asteroid", "Terran Artifacts", "Non-Interactable Asteroids"]),
        [182] = new("specialAsteroidHoldCapacity", Categories: ["Asteroid"], Groups: ["Prismaticite"], BlockedGroups: ["Scalable Decorative Asteroid", "Terran Artifacts", "Ancient Compressed Ice", "Temporal Resources", "Ice", "Non-Interactable Asteroids"]),
        [181] = new("specialIceHoldCapacity", Groups: ["Ice", "Temporal Resources", "Ancient Compressed Ice"]),
        [135] = new("specialGasHoldCapacity", Groups: ["Harvestable Cloud", "Compressed Gas"]),
        [136] = new("specialMineralHoldCapacity", Groups: ["Mineral", "Unrefined Mineral"]),
        [137] = new("specialSalvageHoldCapacity", Groups: ["Ancient Salvage", "Salvaged Materials", "Refinables"]),
        [138] = new("specialShipHoldCapacity", Categories: ["Ship"]),
        [139] = new("specialSmallShipHoldCapacity", Groups: ["Frigate", "Assault Frigate", "Destroyer", "Interdictor", "Interceptor", "Covert Ops", "Electronic Attack Ship", "Stealth Bomber", "Expedition Frigate", "Tactical Destroyer", "Logistics Frigate", "Command Destroyer"]),
        [140] = new("specialMediumShipHoldCapacity", Groups: ["Cruiser", "Combat Recon Ship", "Command Ship", "Heavy Assault Cruiser", "Heavy Interdiction Cruiser", "Logistics", "Strategic Cruiser", "Combat Battlecruiser", "Attack Battlecruiser", "Force Recon Ship", "Flag Cruiser", "Expedition Command Ship", "Special Edition Yachts"]),
        [141] = new("specialLargeShipHoldCapacity", Groups: ["Battleship", "Black Ops", "Marauder"]),
        [142] = new("specialIndustrialShipHoldCapacity", Groups: ["Blockade Runner", "Exhumer", "Hauler", "Mining Barge", "Deep Space Transport", "Expedition Frigate"]),
        [143] = new("specialAmmoHoldCapacity", Categories: ["Charge"], Types: ["Nanite Repair Paste"]),
        [148] = new("specialCommandCenterHoldCapacity", Groups: ["Command Centers"]),
        [149] = new("specialPlanetaryCommoditiesHoldCapacity", Categories: ["Planetary Commodities", "Planetary Resources"], Types: ["Water", "Oxygen"]),
        [151] = new("specialMaterialBayCapacity", Categories: ["Planetary Commodities", "Commodity", "Material"]),
        [154] = new("specialQuafeHoldCapacity", Types: ["Large Crates of Quafe", "Quafe", "Quafe Ultra", "Quafe Ultra Special Edition", "Quafe Zero Classic", "Spiked Quafe", "Quafe Hyper"]),
        [174] = new("specialCorpseHoldCapacity", Types: ["Corpse Male", "Corpse Female"]),
        [176] = new("specialMobileDepotHoldCapacity", Groups: ["Mobile Depot"]),
        [177] = new("specialBoosterHoldCapacity", Groups: ["Booster", "Drugs", "Biochemical Material"]),
        [183] = new("specialSubsystemHoldCapacity", Categories: ["Subsystem"]),
        [185] = new("specialColonyResourcesHoldCapacity", Categories: ["Planetary Commodities", "Planetary Resources", "Infrastructure Upgrades", "Orbitals", "Sovereignty Structures", "Structure", "Deployable", "Structure Module"], Groups: ["Colony Reagents", "Moon Materials", "Fuel Block", "Ice Product", "Structure Light Fighter", "Structure Support Fighter", "Structure Heavy Fighter", "Guided Bomb", "Structure Anti-Subcapital Missile", "Structure Anti-Capital Missile", "Structure ECM script", "Structure Area Denial Ammunition", "Command Centers", "Structure Warp Disruptor Script"], Types: ["Water", "Oxygen"]),
        [186] = new("outputMoonMaterialBayCapacity", Groups: ["Moon Materials", "Mineral"]),
        [188] = new("specialExpeditionHoldCapacity", Categories: ["Deployable", "Blueprint", "Personalization", "SKINs", "Commodity", "Apparel", "Skill", "Special Edition Assets", "Ancient Relics"], Groups: ["Harvestable Cloud", "Compressed Gas", "Generic Decryptor", "Salvaged Materials", "Ancient Salvage", "Booster", "Festival Charges", "Scanner Probe", "Survey Probe", "Structure Festival Charges", "Armor Command Burst Charges", "Expedition Command Burst Charges", "Information Command Burst Charges", "Mining Foreman Burst Charges", "Shield Command Burst Charges", "Skirmish Command Burst Charges", "Capacitor Booster Charge", "Rogue Drone Components", "Refinables", "Named Components"], Types: ["Nanite Repair Paste"]),
    };

    public static bool IsShipHoldFlag(int flag) => ShipHoldRules.ContainsKey(flag);

    private bool CanAddToShipHold(int flag, TypeDef? type, bool singleton, long quantity)
    {
        if (!ShipHoldRules.TryGetValue(flag, out var rule)) return false;
        if ((flag is ShipMaintenanceBayFlag or FrigateEscapeBayFlag) && (!singleton || quantity != 1)) return false;
        if (rule.Categories is null && rule.Groups is null && rule.Types is null) return true;
        if (type is null) return false;

        var category = statics.CategoryName(type.CategoryId);
        var group = statics.GroupName(type.GroupId);
        var categoryAllowed = rule.Categories?.Contains(category, StringComparer.OrdinalIgnoreCase) == true;
        var groupAllowed = rule.Groups?.Contains(group, StringComparer.OrdinalIgnoreCase) == true;
        var typeAllowed = rule.Types?.Contains(type.Name, StringComparer.OrdinalIgnoreCase) == true;
        if (!categoryAllowed && !groupAllowed && !typeAllowed)
            return false;

        if (categoryAllowed
            && (rule.BlockedGroups?.Contains(group, StringComparer.OrdinalIgnoreCase) == true
                || rule.BlockedTypes?.Contains(type.Name, StringComparer.OrdinalIgnoreCase) == true))
            return false;
        return true;
    }


    private async Task<bool> ShipContentsAllowedInBayAsync(Item candidate, int flag, DogmaWorld world, long ownerId, CancellationToken cancellationToken)
    {
        if (flag is not ShipMaintenanceBayFlag and not FrigateEscapeBayFlag) return true;
        if (!candidate.Singleton || candidate.Quantity != 1) return false;
        await statics.EnsureTypesAsync([candidate.TypeId], cancellationToken);
        if (statics.Type(candidate.TypeId) is not { CategoryId: 6 } shipType) return false;
        if (flag == FrigateEscapeBayFlag && FittingRules.MissingSkill(world, ownerId, shipType) is not null)
            return false;

        var cargo = (await items.InContainerAsync(candidate.Id, cancellationToken))
            .Where(item => item.Flag == CargoFlag)
            .ToArray();
        await statics.EnsureTypesAsync(cargo.Select(item => item.TypeId), cancellationToken);
        return cargo.All(item => statics.Type(item.TypeId) is { } type && IsAllowedMaintenanceBayCargo(type));
    }


    private async Task<Item?> EscapeBayShipAsync(Item battleship, DogmaWorld pilotWorld, long ownerId, CancellationToken cancellationToken)
    {
        if (CapacityAttributeOf(FrigateEscapeBayFlag) is not { } capacityAttribute)
            return null;
        await statics.EnsureTypesAsync([battleship.TypeId], cancellationToken);
        if ((statics.Type(battleship.TypeId)?.Attributes.GetValueOrDefault(capacityAttribute, 0) ?? 0) < 1)
            return null;

        var contents = (await items.InContainerAsync(battleship.Id, cancellationToken))
            .Where(item => item.Flag == FrigateEscapeBayFlag)
            .Take(2)
            .ToArray();
        if (contents.Length != 1)
            return null;
        var frigate = contents[0];
        await statics.EnsureTypesAsync([frigate.TypeId], cancellationToken);
        if (statics.Type(frigate.TypeId) is not { } type
            || !CanAddToShipHold(FrigateEscapeBayFlag, type, frigate.Singleton, frigate.Quantity)
            || !await ShipContentsAllowedInBayAsync(frigate, FrigateEscapeBayFlag, pilotWorld, ownerId, cancellationToken))
            return null;
        return frigate;
    }

    private static bool IsAllowedMaintenanceBayCargo(TypeDef type) =>
        type.CategoryId is 7 or 8 or 22
        || type.Name.Contains("isotope", StringComparison.OrdinalIgnoreCase)
        || type.Name.Contains("strontium", StringComparison.OrdinalIgnoreCase)
        || type.Name.Contains("mobile warp disruptor", StringComparison.OrdinalIgnoreCase);

    private int? CapacityAttributeOf(int flag) => ShipHoldRules.TryGetValue(flag, out var rule) ? statics.AttributeId(rule.AttributeName) : null;


    private double ItemVolumeOf(TypeDef type) =>
        statics.AttributeId("volume") is { } attribute
            ? type.Attributes.GetValueOrDefault(attribute, 0)
            : 0;

    /// <summary>The volume of what these items take.</summary>
    public async Task<double> VolumeOfAsync(IEnumerable<Item> contents, CancellationToken cancellationToken = default)
    {
        var list = contents.ToList();
        await statics.EnsureTypesAsync(list.Select(i => i.TypeId), cancellationToken);
        var used = 0d;
        foreach (var item in list)
        {
            if (statics.Type(item.TypeId) is { } type)
            {
                used += ItemVolumes.Of(item, type.GroupId, ItemVolumeOf(type));
            }
        }
        return used;
    }

    /// <summary>
    /// How big a bay of a ship the character owns is and what is in it. The capacity of the ship the character flies is dogma's (its skills and modules act on it);
    /// the capacity of another ship in the hangar is its type's.
    /// </summary>
    public async Task<Room?> RoomAsync(long characterId, long shipId, int flag, CancellationToken cancellationToken = default)
    {
        if (await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return null;
        }
        await statics.EnsureTypesAsync([ship.TypeId], cancellationToken);
        var capacity = 0d;
        if (CapacityAttributeOf(flag) is { } attribute)
        {
            capacity = await characters.GetAsync(characterId, cancellationToken) is { ShipId: { } active } && active == shipId && await LoadAsync(characterId, cancellationToken) is { } snapshot
                ? snapshot.World.Get(shipId, attribute)
                : statics.Type(ship.TypeId)?.Attributes.GetValueOrDefault(attribute, 0) ?? 0;
        }
        var contents = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => i.Flag == flag).ToArray();
        var used = flag == FrigateEscapeBayFlag
            ? contents.Sum(item => Math.Max(1, item.Quantity))
            : await VolumeOfAsync(contents, cancellationToken);
        return new Room(capacity, used);
    }

    public async Task<(Item Item, SpaceItem SpaceItem)> CreateAsteroidAsync(
        int typeId, int solarSystemId, long beltId, long units, IReadOnlyList<double> position, IReadOnlyList<double> direction,
        double radius, PgSpaceItemStore spaceItems, CancellationToken cancellationToken = default)
    {
        await statics.EnsureTypesAsync([typeId], cancellationToken);
        if (statics.Type(typeId) is not { } type || statics.CategoryName(type.CategoryId) != "Asteroid" || units <= 0)
        {
            throw new ArgumentException("The SDE type is not a published asteroid ore type.", nameof(typeId));
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, 0, solarSystemId, InSpaceFlag, units, true), cancellationToken);
        var item = new Item(id, typeId, 0, solarSystemId, InSpaceFlag, units, true, null);
        var spaceItem = new SpaceItem(id, solarSystemId, position, direction, radius, type.GroupId, beltId, 0, 0, 0, 0, long.MaxValue);
        await spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (item, spaceItem);
    }

    /// <summary>Where a miner must be: a drone out in the system, or a module fitted to the ship.</summary>
    private static bool IsPlacedFor(MiningSource kind, Item miner, int solarSystemId, long shipId) => kind switch
    {
        MiningSource.Drone => miner.LocationId == solarSystemId && miner.Flag == InSpaceFlag,
        MiningSource.Module => miner.LocationId == shipId && Slots.IsSlotFlag(miner.Flag),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The category a miner of the kind belongs to.</summary>
    private static string CategoryOf(MiningSource kind) => kind switch
    {
        MiningSource.Drone => "Drone",
        MiningSource.Module => "Module",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// One mining cycle of an asteroid by a miner of the ship: a drone or a module (<paramref name="kind"/>). The yield is taken from the asteroid
    /// in one short transaction (cargo grant and stock change); the refusal reasons are the same for every kind of miner.
    /// </summary>
    public async Task<DroneMiningCommit> MineAsteroidAsync(
        long characterId, long shipId, int solarSystemId, long minerId, long asteroidId, MiningSource kind, MiningNumbers numbers, MiningRolls rolls,
        long cycleId, PgWorkCycleLedger ledger, PgSpaceItemStore spaceItems, EventBus events, int corporationId, CancellationToken cancellationToken = default)
    {
        static bool IsRoll(double value) => value >= 0d && value <= 1d;
        if (!double.IsFinite(numbers.YieldM3) || numbers.YieldM3 <= 0 || !double.IsFinite(numbers.WasteVolumeMultiplier)
            || !double.IsFinite(numbers.WasteProbability) || !double.IsFinite(numbers.CritProbability) || !double.IsFinite(numbers.CritQuantityMultiplier)
            || !IsRoll(rolls.Waste) || !IsRoll(rolls.NormalRounding) || !IsRoll(rolls.WasteRounding) || !IsRoll(rolls.Crit) || !IsRoll(rolls.CritRounding))
        {
            return new(false, "INVALID_PROFILE", 0, 0, false, null);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        // a cycle this miner has had granted is answered from its record: nothing is granted twice when the simulation sends the cycle again
        if (await ledger.LastAsync(connection, transaction, minerId, cancellationToken) is { } settled && settled.CycleId == cycleId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(settled.Accepted, settled.Reason, settled.UnitsRemaining, settled.DeliveredUnits, settled.Complete, null);
        }
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return new(false, "NO_SHIP", 0, 0, false, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, minerId, cancellationToken) is not { } miner
            || miner.OwnerId != characterId || !IsPlacedFor(kind, miner, solarSystemId, shipId)
            || statics.Type(miner.TypeId) is not { } minerType || statics.CategoryName(minerType.CategoryId) != CategoryOf(kind))
        {
            return new(false, "NOT_CONTROLLED", 0, 0, false, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, asteroidId, cancellationToken) is not { } asteroid
            || asteroid.LocationId != solarSystemId || asteroid.Flag != InSpaceFlag || asteroid.Quantity <= 0)
        {
            return new(false, "NO_TARGET", 0, 0, false, null);
        }
        await statics.EnsureTypesAsync([asteroid.TypeId], cancellationToken);
        var type = statics.Type(asteroid.TypeId);
        var volumeAttribute = statics.AttributeId("volume");
        var unitVolume = type is null || volumeAttribute is not { } volumeId
            ? 0d
            : type.Attributes.GetValueOrDefault(volumeId, 0d);
        if (type is null || statics.CategoryName(type.CategoryId) != "Asteroid" || !double.IsFinite(unitVolume) || unitVolume <= 0)
        {
            return new(false, "NO_TARGET", asteroid.Quantity, 0, false, null);
        }
        // the yield is the one function both sources use (MiningYield): a drone rounds down and gets no critical yield, a module does both
        var mined = MiningYield.ComputeMiningResult(
            new MiningCycle(kind, numbers.YieldM3, unitVolume, asteroid.Quantity, numbers.WasteVolumeMultiplier, numbers.WasteProbability,
                numbers.CritQuantityMultiplier, numbers.CritProbability),
            rolls);
        if (kind == MiningSource.Drone && mined.NormalQuantity <= 0)
        {
            return new(false, "INVALID_PROFILE", asteroid.Quantity, 0, false, null);
        }
        var delivered = mined.TransferredQuantity;
        var remaining = asteroid.Quantity - mined.DepletedQuantity;
        if (delivered > 0)
        {
            var candidate = new Item(0, asteroid.TypeId, characterId, shipId, CargoFlag, delivered, false, null);
            if (await RefuseWhatDoesNotFitTogetherAsync(snapshot, [candidate], CargoFlag, cancellationToken) is not null)
            {
                return new(false, "CARGO_FULL", asteroid.Quantity, 0, false, null);
            }
        }
        ItemChanged? change = null;
        if (delivered > 0)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(asteroid.TypeId, characterId, shipId, CargoFlag, delivered, false), cancellationToken);
            change = new ItemChanged(new Item(id, asteroid.TypeId, characterId, shipId, CargoFlag, delivered, false, null), solarSystemId, InSpaceFlag, 0);
        }
        await items.SetQuantityAsync(connection, transaction, asteroidId, remaining, cancellationToken);
        await ledger.RecordAsync(connection, transaction, new WorkCycleRecord(minerId, cycleId, true, "", remaining, delivered, remaining == 0), cancellationToken);
        // the fact of the grant is written with it (the mining ledger and every other reader take it from the bus), so a rolled-back grant announces nothing
        if (mined.TransferredQuantity > 0 || mined.WastedQuantity > 0)
        {
            var yieldKind = statics.GroupName(type.GroupId).Equals("Ice", StringComparison.OrdinalIgnoreCase) ? "ice" : "ore";
            await events.PublishInTransactionAsync(connection, transaction, OreMinedEvent.Topic, new OreMinedEvent(
                characterId, corporationId, solarSystemId, shipId, snapshot.Ship.TypeId, minerId, kind == MiningSource.Module ? miner.TypeId : 0, kind,
                asteroid.TypeId, yieldKind, mined.TransferredQuantity, mined.WastedQuantity, mined.CriticalQuantity), $"char:{characterId}", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        events.Wake();
        return new(true, "", remaining, delivered, remaining == 0,
            change is null ? null : new ShipChanged(change.After.Id, shipId, [], [], [change]), mined, asteroid.TypeId);
    }

    /// <summary>
    /// One salvage cycle of a wreck by a salvager of the ship: a drone (in space) or a module (fitted). The wreck's product goes to the ship's hold and what
    /// is left in the wreck goes to a can; the miner checks are the ones of <see cref="MineAsteroidAsync"/>.
    /// </summary>
    public async Task<DroneSalvageCommit> SalvageWreckAsync(
        long characterId, long shipId, int solarSystemId, MiningSource kind, long minerId, long wreckId, int? productTypeId,
        SpaceItem wreckSpace, long containerExpiresAtMs, PgSpaceItemStore spaceItems, long cycleId, PgWorkCycleLedger ledger, EventBus events,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return new(false, "NO_SHIP", null, null, null);
        }
        // a cycle this salvager has had granted is answered from its record, in the transaction: the wreck is not salvaged twice when the cycle is sent again
        if (await ledger.LastAsync(connection, transaction, minerId, cancellationToken) is { } settled && settled.CycleId == cycleId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(settled.Accepted, settled.Reason, null, null, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, minerId, cancellationToken) is not { } drone
            || drone.OwnerId != characterId || !IsPlacedFor(kind, drone, solarSystemId, shipId)
            || statics.Type(drone.TypeId) is not { } droneType || statics.CategoryName(droneType.CategoryId) != CategoryOf(kind))
        {
            return new(false, "NOT_CONTROLLED", null, null, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, wreckId, cancellationToken) is not { } wreck
            || wreck.LocationId != solarSystemId || wreck.Flag != InSpaceFlag || wreckSpace.ItemId != wreckId
            || wreckSpace.SolarSystemId != solarSystemId)
        {
            return new(false, "NO_WRECK", null, null, null);
        }
        int[] typeIds = productTypeId is { } product ? [wreck.TypeId, product] : [wreck.TypeId];
        await statics.EnsureTypesAsync(typeIds, cancellationToken);
        var wreckType = statics.Type(wreck.TypeId);
        if (wreckType is null || !statics.GroupName(wreckType.GroupId).Contains("Wreck", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "NO_WRECK", null, null, null);
        }
        if (productTypeId is { } productTypeIdValue)
        {
            if (statics.Type(productTypeIdValue) is not { } productType
                || !statics.GroupName(productType.GroupId).Equals("Salvaged Materials", StringComparison.OrdinalIgnoreCase))
            {
                return new(false, "INVALID_SALVAGE_PRODUCT", null, null, null);
            }
            // ponytail: one salvage unit per successful wreck until SDE exposes an authoritative per-wreck amount table.
            var productCandidate = new Item(0, productTypeIdValue, characterId, shipId, CargoFlag, 1, false, null);
            if (await RefuseWhatDoesNotFitTogetherAsync(snapshot, [productCandidate], CargoFlag, cancellationToken) is not null)
            {
                return new(false, "CARGO_FULL", null, null, null);
            }
        }
        var contents = await items.LockContainerAsync(connection, transaction, wreckId, cancellationToken);
        var moved = new List<ItemChanged>(contents.Count + (productTypeId is null ? 0 : 1));
        Item? container = null;
        SpaceItem? containerSpace = null;
        if (contents.Count > 0)
        {
            await statics.EnsureTypesAsync([CargoContainerTypeId], cancellationToken);
            if (statics.Type(CargoContainerTypeId) is not { } canType)
            {
                return new(false, "NO_CONTAINER_TYPE", null, null, null);
            }
            var canId = await items.CreateAsync(connection, transaction, new NewItem(
                CargoContainerTypeId, wreck.OwnerId, solarSystemId, InSpaceFlag, 1, true), cancellationToken);
            container = new Item(canId, CargoContainerTypeId, wreck.OwnerId, solarSystemId, InSpaceFlag, 1, true, null);
            foreach (var content in contents)
            {
                await items.MoveAsync(connection, transaction, content.Id, canId, ItemFlags.Hangar, cancellationToken);
                moved.Add(new ItemChanged(content with { LocationId = canId }, content.LocationId, content.Flag, content.Quantity));
            }
            var radiusId = statics.AttributeId("radius");
            var radius = radiusId is { } radiusAttribute
                ? canType.Attributes.GetValueOrDefault(radiusAttribute, wreckSpace.Radius)
                : wreckSpace.Radius;
            containerSpace = new SpaceItem(
                canId, solarSystemId, wreckSpace.Position, wreckSpace.Direction, radius, canType.GroupId, wreckSpace.LauncherId,
                wreckSpace.CharacterId, wreckSpace.CorporationId, wreckSpace.AllianceId, wreckSpace.WarFactionId, containerExpiresAtMs);
            await spaceItems.SaveAsync(connection, transaction, containerSpace, cancellationToken);
        }
        if (productTypeId is { } salvageType)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(salvageType, characterId, shipId, CargoFlag, 1, false), cancellationToken);
            moved.Add(new ItemChanged(new Item(id, salvageType, characterId, shipId, CargoFlag, 1, false, null), solarSystemId, InSpaceFlag, 0));
        }
        await items.DeleteAsync(connection, transaction, wreckId, cancellationToken);
        await spaceItems.RemoveAsync(connection, transaction, wreckId, cancellationToken);
        // the grant, the wreck's removal, the answer that records the cycle and its fact are one transaction (rule 1)
        await ledger.RecordAsync(connection, transaction, new WorkCycleRecord(minerId, cycleId, true, "", 0, 1, true), cancellationToken);
        await events.PublishInTransactionAsync(connection, transaction, SalvageWreckedEvent.Topic, new SalvageWreckedEvent(
            characterId, solarSystemId, shipId, minerId, kind, wreckId, wreck.TypeId, productTypeId ?? 0, container?.Id ?? 0), $"char:{characterId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events.Wake();
        var change = moved.Count == 0 ? null : new ShipChanged(moved[0].After.Id, shipId, [], [], moved);
        return new(true, "", change, container, containerSpace);
    }

    /// <summary>What is moved into a hold of the ship the character flies when <paramref name="quantity"/> of the item goes there: refused when it does not fit.</summary>
    private async Task<ShipRefused?> RefuseWhatDoesNotFitAsync(ShipSnapshot snapshot, Item item, long quantity, int flag, CancellationToken cancellationToken)
    {
        if (statics.Type(item.TypeId) is not { } type || CapacityAttributeOf(flag) is not { } attribute)
        {
            return null;
        }
        var contents = (await items.InContainerAsync(snapshot.Ship.Id, cancellationToken)).Where(i => i.Flag == flag).ToArray();
        var capacity = snapshot.Character.ShipId == snapshot.Ship.Id
            ? snapshot.World.Get(snapshot.Ship.Id, attribute)
            : statics.Type(snapshot.Ship.TypeId)?.Attributes.GetValueOrDefault(attribute, 0) ?? 0;
        if (flag == FrigateEscapeBayFlag)
        {
            var room = new Room(capacity, contents.Length);
            var requiredShips = Math.Max(1, quantity);
            if (requiredShips <= room.Free)
            {
                return null;
            }
            return new ShipRefused(ShipRefusal.NotEnoughCargoSpace, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId,
                Available: Math.Max(0, room.Free), Volume: requiredShips);
        }
        var required = ItemVolumes.Unit(item.TypeId, type.GroupId, item.Singleton, ItemVolumeOf(type)) * Math.Max(1, quantity);
        var cargoRoom = new Room(capacity, await VolumeOfAsync(contents, cancellationToken));
        if (required <= 0 || required <= cargoRoom.Free + RoomTolerance)
        {
            return null;
        }
        return new ShipRefused(flag == DroneBayFlag ? ShipRefusal.NotEnoughDroneBaySpace : ShipRefusal.NotEnoughCargoSpace, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId, Available: Math.Round(cargoRoom.Free, 6), Volume: Math.Round(required, 6));
    }

    /// <summary>
    /// <c>StackAll</c>: the stacks of one type in a place (a hangar, a ship's hold) become one, the oldest, and the others are gone. What is not stackable (an assembled
    /// item) and what sits in a slot is left alone. Answers what changed, in the order the client is told: the stack that grew, then the stack that went.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>> StackAllAsync(long characterId, long containerId, int? flag, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var stacks = (await items.InContainerAsync(containerId, cancellationToken))
            .Where(i => i.OwnerId == characterId && !i.Singleton && !Slots.IsSlotFlag(i.Flag) && (flag is null || i.Flag == flag))
            .GroupBy(i => (i.TypeId, i.Flag));
        var changes = new List<ItemChanged>();
        foreach (var group in stacks)
        {
            var survivor = group.First();
            foreach (var other in group.Skip(1))
            {
                // read again under the row locks: a stack that moved or went since the list was read is left as it is
                if (await items.LockAndGetAsync(connection, transaction, survivor.Id, cancellationToken) is not { } kept
                    || await items.LockAndGetAsync(connection, transaction, other.Id, cancellationToken) is not { } gone
                    || !Stackable(kept, containerId, group.Key.Flag, characterId) || !Stackable(gone, containerId, group.Key.Flag, characterId) || kept.TypeId != gone.TypeId)
                {
                    continue;
                }
                var merged = kept with { Quantity = kept.Quantity + gone.Quantity };
                await items.SetQuantityAsync(connection, transaction, kept.Id, merged.Quantity, cancellationToken);
                await items.DeleteAsync(connection, transaction, gone.Id, cancellationToken);
                changes.Add(new ItemChanged(merged, kept.LocationId, kept.Flag, kept.Quantity));
                changes.Add(new ItemChanged(gone with { LocationId = JunkLocation }, gone.LocationId, gone.Flag, gone.Quantity));
                survivor = merged;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return changes;
    }

    private static bool Stackable(Item item, long containerId, int flag, long characterId) =>
        !item.Singleton && item.LocationId == containerId && item.Flag == flag && item.OwnerId == characterId;
}
