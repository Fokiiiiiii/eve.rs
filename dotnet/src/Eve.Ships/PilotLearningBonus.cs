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

    private sealed record BonusFrame(long AtFileTime, double[] Values);
    private sealed record BonusTimeline(IReadOnlyList<BonusFrame> Frames);

    private readonly ConcurrentDictionary<long, BonusTimeline> _timelines = new();

    /// <summary>The bonus of a learning attribute as last refreshed; zero for a character never refreshed or an attribute that is not a learning one.</summary>
    public double Of(long characterId, int attributeId)
    {
        var index = Array.IndexOf(ShipSnapshot.LearningAttributes, attributeId);
        return index < 0 ? 0 : At(characterId, attributeId, UnixEpochInFileTime + nowUnixMs() * 10_000);
    }

    /// <summary>The learning bonus at a FILETIME, including boosters that have not expired yet at that time.</summary>
    public double At(long characterId, int attributeId, long atFileTime)
    {
        var index = Array.IndexOf(ShipSnapshot.LearningAttributes, attributeId);
        if (index < 0 || !_timelines.TryGetValue(characterId, out var timeline) || timeline.Frames.Count == 0)
            return 0;

        var frame = timeline.Frames[0];
        foreach (var candidate in timeline.Frames)
        {
            if (candidate.AtFileTime > atFileTime)
                break;
            frame = candidate;
        }
        return frame.Values[index];
    }

    /// <summary>Booster expiries where the character's learning bonus can change.</summary>
    public IReadOnlyList<long> BoundariesBetween(long characterId, long afterFileTime, long throughFileTime) =>
        _timelines.TryGetValue(characterId, out var timeline)
            ? timeline.Frames.Select(frame => frame.AtFileTime)
                .Where(time => time > afterFileTime && time <= throughFileTime).ToArray()
            : [];

    /// <summary>Reads the character's implants and active boosters again and keeps what they add.</summary>
    public Task RefreshAsync(long characterId, CancellationToken cancellationToken = default) =>
        RefreshAtAsync(characterId, UnixEpochInFileTime + nowUnixMs() * 10_000, cancellationToken);

    /// <summary>Reads the character's implants and active boosters as they were at a FILETIME.</summary>
    public async Task RefreshAtAsync(long characterId, long atFileTime, CancellationToken cancellationToken = default)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is null)
        {
            _timelines[characterId] = new BonusTimeline([new BonusFrame(long.MinValue, new double[ShipSnapshot.LearningAttributes.Length])]);
            return;
        }

        var augmentations = await items.InLocationAsync(characterId, characterId, cancellationToken);
        var boosters = await skills.SkillBoostersAsync(characterId, cancellationToken);
        await statics.EnsureTypesAsync([character.TypeId, .. augmentations.Select(item => item.TypeId)], cancellationToken);

        // The queue is rebased when an implant or booster is fitted. From that start onward only booster expiry times
        // can change its attribute rate; retain expired rows until the lapse job rebases and prunes them.
        var expiries = boosters.Select(booster => booster.ExpiryFileTime).Distinct().Order().ToArray();
        var frames = new List<BonusFrame>(expiries.Length + 1)
        {
            new(long.MinValue, await ComputeAsync(characterId, augmentations, boosters, long.MinValue, cancellationToken)),
        };
        foreach (var expiry in expiries)
        {
            frames.Add(new BonusFrame(expiry,
                await ComputeAsync(characterId, augmentations, boosters, expiry, cancellationToken)));
        }
        _timelines[characterId] = new BonusTimeline(frames);
    }

    private async Task<double[]> ComputeAsync(long characterId, IReadOnlyList<Eve.Inventory.Item> augmentations,
        IReadOnlyList<StoredSkillBooster> boosters, long atFileTime, CancellationToken cancellationToken)
    {
        var bonus = new double[ShipSnapshot.LearningAttributes.Length];
        if (await characters.GetAsync(characterId, cancellationToken) is not { } character)
        {
            return bonus;
        }
        var activeBoosters = boosters.Where(booster => booster.ExpiryFileTime > atFileTime)
            .ToDictionary(booster => booster.ItemId);

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
