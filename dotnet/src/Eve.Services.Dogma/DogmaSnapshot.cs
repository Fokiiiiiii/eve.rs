using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Skills;

namespace Eve.Services.Dogma;

/// <summary>What a ship has taken and what it has left: the fractions dogma reports next to its attributes.</summary>
public sealed record ShipCondition(double Damage, double Charge, double ShieldCharge, double ArmorDamage, bool Incapacitated)
{
    /// <summary>A ship as it leaves the hangar: nothing damaged, capacitor and shield full.</summary>
    public static readonly ShipCondition Full = new(0, 1, 1, 0, false);
}

/// <summary>A character's ship and what is fitted to it, with the skills the character has trained, ready to be worked out.</summary>
public sealed class DogmaSnapshot
{
    /// <summary>The item id of a trained skill: the character's id times a hundred thousand, plus the type.</summary>
    public static long SkillItemId(long characterId, int typeId) => (characterId * 100_000L) + typeId;

    /// <summary>The slot the character sits in when it flies a ship (the pilot's).</summary>
    public const int PilotFlag = 57;

    /// <summary>The flag skills are kept under.</summary>
    public const int SkillFlag = 7;

    public required Character Character { get; init; }

    public required Item Ship { get; init; }

    /// <summary>What is fitted to the ship, in the order of its slots.</summary>
    public required IReadOnlyList<Item> Fitted { get; init; }

    public required IReadOnlyList<CharacterSkill> Skills { get; init; }

    public required DogmaWorld World { get; init; }

    public ShipCondition Condition { get; init; } = ShipCondition.Full;

    public long TotalSkillPoints => Skills.Sum(s => s.SkillPoints);

    /// <summary>
    /// The character's active ship in its hangar with its fitting and skills, or null when the character has none.
    /// Every fitted module is online: what a module has been switched to is not kept yet.
    /// </summary>
    public static async Task<DogmaSnapshot?> LoadAsync(
        DogmaStatic statics, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, long characterId, CancellationToken cancellationToken)
    {
        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return null;
        }
        var fitted = (await items.InLocationAsync(shipId, characterId, cancellationToken))
            .Where(i => i.Flag is >= ItemFlags.LowSlot0 and < ItemFlags.HiSlot0 + 8)
            .OrderBy(i => i.Flag).ThenBy(i => i.Id)
            .ToList();
        var trained = await skills.ForCharacterAsync(characterId, cancellationToken);

        await statics.EnsureTypesAsync([character.TypeId, ship.TypeId, .. fitted.Select(i => i.TypeId), .. trained.Select(s => s.TypeId)], cancellationToken);

        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(characterId, character.TypeId, characterId, shipId, PilotFlag, ItemState.Online));
        foreach (var learning in LearningAttributes)
        {
            world.SetValue(characterId, learning, StartingLearningAttribute);
        }
        world.Add(new DogmaItem(shipId, ship.TypeId, characterId, ship.LocationId, ship.Flag));
        foreach (var module in fitted)
        {
            world.Add(new DogmaItem(module.Id, module.TypeId, characterId, shipId, module.Flag, ItemState.Online));
        }
        foreach (var skill in trained)
        {
            var id = SkillItemId(characterId, skill.TypeId);
            world.Add(new DogmaItem(id, skill.TypeId, characterId, characterId, SkillFlag));
            world.SetValue(id, DogmaWorld.SkillLevel, skill.Level);
            world.SetValue(id, SkillPointsAttribute, skill.SkillPoints);
        }
        return new DogmaSnapshot { Character = character, Ship = ship, Fitted = fitted, Skills = trained, World = world };
    }

    /// <summary>charisma, intelligence, memory, perception, willpower: how fast the character learns.</summary>
    public static readonly int[] LearningAttributes = [164, 165, 166, 167, 168];

    /// <summary>What a new character has in each (the reference's value: attributes are not remapped or raised by implants yet).</summary>
    public const double StartingLearningAttribute = 20;

    private const int SkillPointsAttribute = 276;
}
