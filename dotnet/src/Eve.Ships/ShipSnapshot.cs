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

    public required Character Character { get; init; }

    public required Item Ship { get; init; }

    /// <summary>What is fitted to the ship, in the order of its slots.</summary>
    public required IReadOnlyList<Item> Fitted { get; init; }

    public required IReadOnlyList<CharacterSkill> Skills { get; init; }

    public required DogmaWorld World { get; init; }

    public ShipCondition Condition { get; init; } = ShipCondition.Full;

    /// <summary>What each fitted module has taken (a module with nothing in the map has taken nothing and never been mended).</summary>
    public IReadOnlyDictionary<long, ModuleCondition> ModuleConditions { get; init; } = new Dictionary<long, ModuleCondition>();

    /// <summary>The same ship with what it has taken as told (a ship in space is asked of the simulation, which has it live).</summary>
    public ShipSnapshot WithCondition(ShipCondition condition) => new()
    {
        Character = Character, Ship = Ship, Fitted = Fitted, Skills = Skills, World = World, Condition = condition, ModuleConditions = ModuleConditions,
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
        Func<int, double>? learningAttribute, IReadOnlyList<CharacterSkill>? skillsOverride, CancellationToken cancellationToken)
    {
        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return null;
        }
        var contents = await items.InLocationAsync(shipId, characterId, cancellationToken);
        var fitted = contents.Where(i => Slots.IsSlotFlag(i.Flag)).OrderBy(i => i.Flag).ThenBy(i => i.Id).ToList();
        var states = await modules.StatesAsync(fitted.Select(i => i.Id), cancellationToken);
        var moduleConditions = await modules.ModuleConditionsAsync(fitted.Select(i => i.Id), cancellationToken);
        var condition = await modules.ShipConditionAsync(shipId, cancellationToken);
        var trained = skillsOverride ?? await skills.ForCharacterAsync(characterId, cancellationToken);

        await statics.EnsureTypesAsync([character.TypeId, ship.TypeId, .. contents.Select(i => i.TypeId), .. trained.Select(s => s.TypeId)], cancellationToken);

        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(characterId, character.TypeId, characterId, shipId, PilotFlag, ItemState.Online));
        foreach (var learning in LearningAttributes)
        {
            world.SetValue(characterId, learning, learningAttribute?.Invoke(learning) ?? StartingLearningAttribute);
        }
        world.Add(new DogmaItem(shipId, ship.TypeId, characterId, ship.LocationId, ship.Flag));
        foreach (var module in fitted)
        {
            world.Add(new DogmaItem(module.Id, module.TypeId, characterId, shipId, module.Flag, states[module.Id]));
        }
        foreach (var skill in trained)
        {
            var id = SkillItemId(characterId, skill.TypeId);
            world.Add(new DogmaItem(id, skill.TypeId, characterId, characterId, SkillFlag));
            world.SetValue(id, DogmaWorld.SkillLevel, skill.Level);
            world.SetValue(id, SkillPointsAttribute, skill.SkillPoints);
        }
        return new ShipSnapshot { Character = character, Ship = ship, Fitted = fitted, Skills = trained, World = world, Condition = condition, ModuleConditions = moduleConditions };
    }
}
