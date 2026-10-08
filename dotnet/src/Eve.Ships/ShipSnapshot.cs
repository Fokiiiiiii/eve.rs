using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Skills;

namespace Eve.Ships;

/// <summary>A character's ship and what is fitted to it, with the skills the character has trained, ready to be worked out.</summary>
public sealed class ShipSnapshot
{
    /// <summary>The item id of a trained skill: the character's id times a hundred thousand, plus the type.</summary>
    public static long SkillItemId(long characterId, int typeId) => (characterId * 100_000L) + typeId;

    /// <summary>The slot the character sits in when it flies a ship (the pilot's).</summary>
    public const int PilotFlag = 57;

    /// <summary>The flag skills are kept under.</summary>
    public const int SkillFlag = 7;

    /// <summary>charisma, intelligence, memory, perception, willpower: how fast the character learns.</summary>
    public static readonly int[] LearningAttributes = [164, 165, 166, 167, 168];

    /// <summary>What a new character has in each (the reference's value: attributes are not remapped or raised by implants yet).</summary>
    public const double StartingLearningAttribute = 20;

    private const int SkillPointsAttribute = 276;

    /// <summary>The rounds a loaded charge holds (quantityCharge): the client reads them as an attribute of the charge.</summary>
    public const int QuantityCharge = 805;

    public required Character Character { get; init; }

    public required Item Ship { get; init; }

    /// <summary>What is fitted to the ship, in the order of its slots (the charges loaded in the modules are not fitted: see <see cref="Charges"/>).</summary>
    public required IReadOnlyList<Item> Fitted { get; init; }

    /// <summary>The charges loaded in the modules, in the order of the slots: an item of the ship whose flag is the flag of its module.</summary>
    public IReadOnlyList<Item> Charges { get; init; } = [];

    /// <summary>The charge loaded in the module, or null when it has none.</summary>
    public Item? ChargeOf(Item module) => Charges.FirstOrDefault(charge => charge.Flag == module.Flag);

    private const int ChargeCategory = 8;

    /// <summary>
    /// What a ship holds in its slots, told apart: the modules and the charges loaded in them (both are items of the ship at a slot's flag; the
    /// category of the type says which is which).
    /// </summary>
    internal static async Task<(List<Item> Fitted, List<Item> Charges)> SlotsOfAsync(DogmaStatic statics, IReadOnlyList<Item> contents, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync(contents.Select(i => i.TypeId), cancellationToken);
        var inSlots = contents.Where(i => Slots.IsSlotFlag(i.Flag)).ToList();
        bool IsCharge(Item item) => statics.Type(item.TypeId)?.CategoryId == ChargeCategory;
        return (
            [.. inSlots.Where(i => !IsCharge(i)).OrderBy(i => i.Flag).ThenBy(i => i.Id)],
            [.. inSlots.Where(IsCharge).OrderBy(i => i.Flag).ThenBy(i => i.Id)]);
    }

    public required IReadOnlyList<CharacterSkill> Skills { get; init; }

    public required DogmaWorld World { get; init; }

    public ShipCondition Condition { get; init; } = ShipCondition.Full;

    /// <summary>What each fitted module has taken (a module with nothing in the map has taken nothing and never been mended).</summary>
    public IReadOnlyDictionary<long, ModuleCondition> ModuleConditions { get; init; } = new Dictionary<long, ModuleCondition>();

    /// <summary>
    /// The capsuleer ships of the same system, for a transport field (the Gravitational Transportation Field Oscillator): the space host lists them, since
    /// the ships of a system are its own. Empty unless the host has set it (<see cref="WithNeighbours"/>).
    /// </summary>
    public IReadOnlyList<TransportNeighbour> Neighbours { get; init; } = [];

    /// <summary>The capital ship groups (carrier, dreadnought, force auxiliary, capital industrial ship, lancer dreadnought, command carrier) and the supercapitals (titan, supercarrier): the groups the simulation sizes undock points by (<c>locators.rs</c>). A transport field does not carry them.</summary>
    public static readonly IReadOnlySet<int> CapitalGroupIds = new HashSet<int> { 547, 485, 1538, 883, 4594, 5120, 30, 659 };

    /// <summary>The same ship with the other capsuleer ships of its system (see <see cref="Neighbours"/>).</summary>
    public ShipSnapshot WithNeighbours(IReadOnlyList<TransportNeighbour> neighbours) => new()
    {
        Character = Character, Ship = Ship, Fitted = Fitted, Charges = Charges, Skills = Skills, World = World, Condition = Condition, ModuleConditions = ModuleConditions,
        Neighbours = neighbours,
    };

    /// <summary>The same ship with what it has taken as told (a ship in space is asked of the simulation, which has it live).</summary>
    public ShipSnapshot WithCondition(ShipCondition condition) => new()
    {
        Character = Character, Ship = Ship, Fitted = Fitted, Charges = Charges, Skills = Skills, World = World, Condition = condition, ModuleConditions = ModuleConditions, Neighbours = Neighbours,
    };


    /// <summary>The same ship after its dogma sources and module states have changed.</summary>
    public ShipSnapshot WithDogma(DogmaWorld world, ShipCondition condition, IReadOnlyList<CharacterSkill> skills) => new()
    {
        Character = Character, Ship = Ship, Fitted = Fitted, Charges = Charges, Skills = skills, World = world, Condition = condition, ModuleConditions = ModuleConditions, Neighbours = Neighbours,
    };

    /// <summary>What a fitted module has taken.</summary>
    public ModuleCondition ConditionOf(long moduleId) => ModuleConditions.TryGetValue(moduleId, out var condition) ? condition : ModuleCondition.Untouched;

    public long TotalSkillPoints => Skills.Sum(s => s.SkillPoints);

    /// <summary>How far a fitted module is switched on.</summary>
    public ItemState StateOf(long moduleId) => World.Item(moduleId)?.State ?? ItemState.Offline;

    /// <summary>
    /// A character's ship in its hangar with its fitting and skills, or null when the character has none. Every ship the character owns
    /// can be loaded, but only the active one (the character's <c>ShipId</c>) has the character's skills acting on it.
    /// </summary>
    internal static async Task<ShipSnapshot?> LoadAsync(
        DogmaStatic statics, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, PgModuleStateStore modules, long characterId,
        Func<int, double>? learningAttribute, IReadOnlyList<CharacterSkill>? skillsOverride, CancellationToken cancellationToken, long? shipIdOverride = null, long? shipOwnerIdOverride = null)
    {
        if (await characters.GetAsync(characterId, cancellationToken) is not { } character)
        {
            return null;
        }
        var selectedShipId = shipIdOverride ?? character.ShipId;
        if (selectedShipId is not { } shipId
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != (shipOwnerIdOverride ?? characterId))
        {
            return null;
        }
        var contents = await items.InLocationAsync(shipId, ship.OwnerId, cancellationToken);
        var (fitted, charges) = await SlotsOfAsync(statics, contents, cancellationToken);
        var states = await modules.StatesAsync(fitted.Select(i => i.Id), cancellationToken);
        var moduleConditions = await modules.ModuleConditionsAsync(fitted.Select(i => i.Id), cancellationToken);
        var condition = await modules.ShipConditionAsync(shipId, cancellationToken);
        var trained = skillsOverride ?? await skills.ForCharacterAsync(characterId, cancellationToken);
        var provenance = await items.Mutations.GetManyAsync([ship.Id, .. contents.Select(i => i.Id)], cancellationToken);

        await statics.EnsureTypesAsync(
            [character.TypeId, ship.TypeId, .. contents.Select(i => i.TypeId), .. provenance.Values.Select(m => m.SourceTypeId), .. trained.Select(s => s.TypeId)],
            cancellationToken);

        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(characterId, character.TypeId, characterId, shipId, PilotFlag, ItemState.Online));
        foreach (var learning in LearningAttributes)
        {
            world.SetValue(characterId, learning, learningAttribute?.Invoke(learning) ?? StartingLearningAttribute);
        }
        world.Add(ItemDogma.Of(ship, characterId, ship.LocationId, ship.Flag, mutation: provenance.GetValueOrDefault(ship.Id)));
        foreach (var module in fitted)
        {
            world.Add(ItemDogma.Of(module, characterId, shipId, module.Flag, states[module.Id], provenance.GetValueOrDefault(module.Id)));
        }
        foreach (var charge in charges)
        {
            world.Add(ItemDogma.Of(charge, characterId, shipId, charge.Flag, mutation: provenance.GetValueOrDefault(charge.Id)));
            world.SetValue(charge.Id, QuantityCharge, charge.Quantity);
        }
        foreach (var skill in trained)
        {
            var id = SkillItemId(characterId, skill.TypeId);
            world.Add(new DogmaItem(id, skill.TypeId, characterId, characterId, SkillFlag));
            world.SetValue(id, DogmaWorld.SkillLevel, skill.Level);
            world.SetValue(id, SkillPointsAttribute, skill.SkillPoints);
        }
        return new ShipSnapshot { Character = character, Ship = ship, Fitted = fitted, Charges = charges, Skills = trained, World = world, Condition = condition, ModuleConditions = moduleConditions };
    }
}

/// <summary>Another capsuleer ship of the system a transport field reaches: its id, and whether it is a capital (held in place, not carried).</summary>
public sealed record TransportNeighbour(long ShipId, bool IsCapital);
