using Eve.Inventory;

namespace Eve.Ships;

public sealed record TournamentDroneSource(Item Item, Eve.Inventory.Mutation.ItemMutationRecord? Mutation);

public sealed partial class CharacterShips
{
    /// <summary>Relevant persisted source identities for bracketing a spectator observation.</summary>
    public async Task<string> TournamentSourceIdentityAsync(long characterId, long shipId, CancellationToken cancellationToken)
    {
        var contents = (await items.InLocationAsync(shipId, characterId, cancellationToken)).OrderBy(x => x.Id).ToArray();
        var augmentations = (await items.InLocationAsync(characterId, characterId, cancellationToken)).OrderBy(x => x.Id).ToArray();
        var provenance = await items.Mutations.GetManyAsync([shipId, .. contents.Select(x => x.Id)], cancellationToken);
        var boosters = await skills.ActiveSkillBoostersAsync(characterId, DateTime.UtcNow.ToFileTimeUtc(), cancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(new { contents, augmentations,
            provenance = provenance.OrderBy(x => x.Key).ToArray(), boosters = boosters.OrderBy(x => x.ItemId).ToArray() });
    }

    /// <summary>Owned bay and space drone items and their mutation provenance for spectator metrics.</summary>
    public async Task<IReadOnlyList<TournamentDroneSource>?> TournamentDroneSourcesAsync(long characterId, long shipId, int systemId, CancellationToken cancellationToken)
    {
        var bay = (await items.InLocationAsync(shipId, characterId, cancellationToken)).Where(x => x.Flag == DroneBayFlag);
        var space = (await items.InLocationAsync(systemId, characterId, cancellationToken)).Where(x => x.Flag == InSpaceFlag && x.Id != shipId);
        var candidates = bay.Concat(space).OrderBy(x => x.Id).ToArray();
        await statics.EnsureTypesAsync(candidates.Select(x => x.TypeId), cancellationToken);
        // An unrecognized bay item cannot safely be classified as a non-drone.
        if (candidates.Any(x => x.LocationId == shipId && statics.Type(x.TypeId) is null)) return null;
        var drones = candidates.Where(x => statics.Type(x.TypeId)?.CategoryId == 18).ToArray();
        var provenance = await items.Mutations.GetManyAsync(drones.Select(x => x.Id).ToArray(), cancellationToken);
        await statics.EnsureTypesAsync(provenance.Values.Select(x => x.SourceTypeId), cancellationToken);
        return drones.Select(x => new TournamentDroneSource(x, provenance.GetValueOrDefault(x.Id))).ToArray();
    }

    /// <summary>Fighter items and their mutation provenance in this ship or its owner's system.</summary>
    public async Task<IReadOnlyList<TournamentDroneSource>?> TournamentFighterSourcesAsync(long characterId, long shipId, int systemId, CancellationToken cancellationToken)
    {
        var held = (await items.InLocationAsync(shipId, characterId, cancellationToken)).Where(x => FighterTubes.IsTube(x.Flag));
        var space = (await items.InLocationAsync(systemId, characterId, cancellationToken)).Where(x => x.Flag == InSpaceFlag && x.Id != shipId);
        var candidates = held.Concat(space).OrderBy(x => x.Id).ToArray();
        await statics.EnsureTypesAsync(candidates.Select(x => x.TypeId), cancellationToken);
        if (candidates.Any(x => x.LocationId == shipId && statics.Type(x.TypeId) is null)) return null;
        var fighters = candidates.Where(x => statics.Type(x.TypeId)?.CategoryId == FighterCategory).ToArray();
        var provenance = await items.Mutations.GetManyAsync(fighters.Select(x => x.Id).ToArray(), cancellationToken);
        await statics.EnsureTypesAsync(provenance.Values.Select(x => x.SourceTypeId), cancellationToken);
        return fighters.Select(x => new TournamentDroneSource(x, provenance.GetValueOrDefault(x.Id))).ToArray();
    }

    /// <summary>Inventory inputs to tournament potential, including fighter tubes and bay.</summary>
    public async Task<IReadOnlyList<Item>> TournamentCompanionsAsync(long shipId, CancellationToken cancellationToken)
    {
        var contents = await items.InContainerAsync(shipId, cancellationToken).ConfigureAwait(false);
        return contents.Where(item => item.Flag == DroneBayFlag || item.Flag == FighterBayFlag || FighterTubes.IsTube(item.Flag))
            .OrderBy(item => item.Id).ToArray();
    }
}
