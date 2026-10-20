using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Skills;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// Plugging an implant into the pilot and destroying one (the dogma service's <c>InjectImplant</c> and <c>DestroyImplant</c>). An implant is an item at
/// implant, its slot is free and the pilot has the skills the implant needs. Plugging in takes the implant from the station hangar or the cargo of the
/// </summary>
public sealed class PilotImplants(NpgsqlDataSource source, DogmaStatic statics, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, CharacterTraining training)
{
    private const int HangarFlag = 4;
    private const int CargoFlag = 5;

    /// <summary>The outcome of a change: its error (null on success), the item before it, the item after it (null when destroyed) and the training it changed.</summary>
    public sealed record Outcome(string? Error, Item? Before = null, Item? After = null, TrainingUpdate? Training = null);

    /// <summary>Plugs the implant into the pilot's implant slot.</summary>
    public async Task<Outcome> InjectAsync(long characterId, long itemId, long? stationId, long? shipId, CancellationToken cancellationToken = default)
    {
        if (await characters.GetAsync(characterId, cancellationToken) is not { } character)
        {
            return new Outcome("CHARACTER_NOT_FOUND");
        }
        if (await items.GetAsync(itemId, cancellationToken) is not { } item || item.OwnerId != characterId)
        {
            return new Outcome("ITEM_NOT_OWNED");
        }
        if (!InPlugInPlace(item, stationId, shipId))
        {
            return new Outcome("ITEM_LOCATION_MISMATCH");
        }
        if (!await IsImplantAsync(item, cancellationToken))
        {
            return new Outcome("NOT_AN_IMPLANT");
        }
        if (await PlugInRefusalAsync(characterId, item, cancellationToken) is { } refusal)
        {
            return new Outcome(refusal);
        }

        var rebased = await training.RebaseRunningAsync(characterId, character.AccountId, cancellationToken);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPilotAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } locked || locked.OwnerId != characterId || locked.TypeId != item.TypeId)
        {
            return new Outcome("ITEM_NOT_OWNED", Training: rebased);
        }
        if (!InPlugInPlace(locked, stationId, shipId))
        {
            return new Outcome("ITEM_LOCATION_MISMATCH", Training: rebased);
        }
        if (await PlugInRefusalAsync(characterId, locked, cancellationToken) is { } raced)
        {
            return new Outcome(raced, Training: rebased);
        }
        await items.MoveAsync(connection, transaction, itemId, characterId, PilotAugmentations.ImplantFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Outcome(null, locked, await items.GetAsync(itemId, cancellationToken), rebased);
    }

    /// <summary>Destroys an implant the pilot wears.</summary>
    public async Task<Outcome> DestroyAsync(long characterId, long itemId, CancellationToken cancellationToken = default)
    {
        if (await characters.GetAsync(characterId, cancellationToken) is not { } character)
        {
            return new Outcome("CHARACTER_NOT_FOUND");
        }
        if (await items.GetAsync(itemId, cancellationToken) is not { } worn || !IsWornBy(worn, characterId) || !await IsImplantAsync(worn, cancellationToken))
        {
            return new Outcome("IMPLANT_NOT_FOUND");
        }

        var rebased = await training.RebaseRunningAsync(characterId, character.AccountId, cancellationToken);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPilotAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } locked || !IsWornBy(locked, characterId))
        {
            return new Outcome("IMPLANT_NOT_FOUND", Training: rebased);
        }
        await items.DeleteAsync(connection, transaction, itemId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Outcome(null, worn, null, rebased);
    }

    /// <summary>The reason an implant cannot be plugged in now (its slot is taken, or the pilot lacks a skill it needs), or null.</summary>
    private async Task<string?> PlugInRefusalAsync(long characterId, Item item, CancellationToken cancellationToken)
    {
        var worn = (await items.InLocationAsync(characterId, characterId, cancellationToken)).Where(i => i.Flag == PilotAugmentations.ImplantFlag && i.Id != item.Id).ToList();
        await statics.EnsureTypesAsync([item.TypeId, .. worn.Select(i => i.TypeId)], cancellationToken);
        var slot = PilotAugmentations.SlotOf(statics.Type(item.TypeId));
        if (worn.Any(other => PilotAugmentations.SlotOf(statics.Type(other.TypeId)) == slot))
        {
            return "IMPLANT_SLOT_OCCUPIED";
        }
        var trained = (await skills.ForCharacterAsync(characterId, cancellationToken)).ToDictionary(skill => skill.TypeId);
        foreach (var (skill, level) in FittingRules.RequiredSkills(statics.Type(item.TypeId)!))
        {
            if ((trained.TryGetValue(skill, out var owned) ? owned.Level : 0) < level)
            {
                return "SKILL_REQUIRED";
            }
        }
        return null;
    }

    private async Task<bool> IsImplantAsync(Item item, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        return PilotAugmentations.IsImplantType(statics.Type(item.TypeId));
    }

    private static bool IsWornBy(Item item, long characterId) =>
        item.OwnerId == characterId && item.LocationId == characterId && item.Flag == PilotAugmentations.ImplantFlag;

    private static bool InPlugInPlace(Item item, long? stationId, long? shipId) =>
        (stationId is { } station && item.LocationId == station && item.Flag == HangarFlag) ||
        (shipId is { } ship && item.LocationId == ship && item.Flag == CargoFlag);

    /// <summary>The implant changes of a pilot happen one after the other (the same lock as the ship's changes).</summary>
    private static async Task LockPilotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction) { Parameters = { new() { Value = characterId } } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
