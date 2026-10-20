using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Skills;

namespace Eve.Ships;

/// <summary>
/// What a character's implants and active boosters add to its learning attributes, worked out by dogma (the same modifiers the pilot's dogma world
/// bonuses of the active implants (<c>applyActiveImplantAttributeBonuses</c>, <c>services/dogma/implants/activeImplantModifiers.js</c>).
/// </summary>
public sealed class PilotLearningBonus(DogmaStatic statics, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, Func<long> nowUnixMs)
{
    private const long UnixEpochInFileTime = 116_444_736_000_000_000;

    private readonly ConcurrentDictionary<long, double[]> _bonuses = new();

    /// <summary>The bonus of a learning attribute as last refreshed; zero for a character never refreshed or an attribute that is not a learning one.</summary>
    public double Of(long characterId, int attributeId)
    {
        var index = Array.IndexOf(ShipSnapshot.LearningAttributes, attributeId);
        return index < 0 || !_bonuses.TryGetValue(characterId, out var bonus) ? 0 : bonus[index];
    }

    /// <summary>Reads the character's implants and active boosters again and keeps what they add.</summary>
    public async Task RefreshAsync(long characterId, CancellationToken cancellationToken = default) =>
        _bonuses[characterId] = await ComputeAsync(characterId, cancellationToken);

    private async Task<double[]> ComputeAsync(long characterId, CancellationToken cancellationToken)
    {
        var bonus = new double[ShipSnapshot.LearningAttributes.Length];
        if (await characters.GetAsync(characterId, cancellationToken) is not { } character)
        {
            return bonus;
        }
        var augmentations = await items.InLocationAsync(characterId, characterId, cancellationToken);
        var nowFileTime = UnixEpochInFileTime + nowUnixMs() * 10_000;
        var activeBoosters = (await skills.ActiveSkillBoostersAsync(characterId, nowFileTime, cancellationToken)).ToDictionary(booster => booster.ItemId);
        await statics.EnsureTypesAsync([character.TypeId, .. augmentations.Select(item => item.TypeId)], cancellationToken);

        // the base is zero: the learning modifiers are added to it, so what the world reads is the bonus alone
        var world = new DogmaWorld(statics, characterId);
        world.Add(new DogmaItem(characterId, character.TypeId, characterId, characterId, ShipSnapshot.PilotFlag, ItemState.Online));
        foreach (var attribute in ShipSnapshot.LearningAttributes)
        {
            world.SetValue(characterId, attribute, 0);
        }
        foreach (var augmentation in augmentations.OrderBy(item => item.Id))
        {
            if (PilotAugmentations.ModelOf(statics, augmentation, characterId, activeBoosters) is { } model)
            {
                world.Add(model);
            }
        }
        for (var index = 0; index < bonus.Length; index++)
        {
            bonus[index] = world.Get(characterId, ShipSnapshot.LearningAttributes[index]);
        }
        return bonus;
    }
}
