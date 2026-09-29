using Eve.Inventory;
using Eve.Skills;
using Npgsql;

namespace Eve.Characters;

/// <summary>What a player chose on the creation screen.</summary>
public sealed record CreationRequest(
    int AccountId, string Name, int RaceId, int BloodlineId, int Gender, int AncestryId, int SchoolId, byte[]? DollInfo, byte[]? PortraitInfo);

public enum CreationRefusal
{
    /// <summary>The name breaks the rules (<see cref="CharacterNames.Validate"/>).</summary>
    InvalidName,

    /// <summary>Another character has the name.</summary>
    NameTaken,

    /// <summary>The account has no free character slot.</summary>
    SlotsFull,

    /// <summary>The race, bloodline, gender or school is not one a player can start with.</summary>
    InvalidChoice,
}

public abstract record CreationOutcome;

/// <summary>The character exists, in its rookie ship, docked at its starting station.</summary>
public sealed record Created(long CharacterId, long ShipId) : CreationOutcome;

public sealed record CreationRefused(CreationRefusal Reason) : CreationOutcome;

/// <summary>
/// Makes a character: the character itself, the rookie ship of its race docked in the hangar of the station it starts
/// at, and the skills its race starts with, in one transaction (so a failure leaves none of them behind).
/// </summary>
public sealed class CharacterCreation(
    NpgsqlDataSource source, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, SkillCatalog skillCatalog, CreationCatalog catalog)
{
    /// <summary>How many characters an account may have.</summary>
    public const int SlotsPerAccount = 3;

    public async Task<CreationOutcome> CreateAsync(CreationRequest request, CancellationToken cancellationToken = default)
    {
        if (CharacterNames.Validate(request.Name) != NameCheck.Valid)
        {
            return new CreationRefused(CreationRefusal.InvalidName);
        }
        var race = catalog.Race(request.RaceId);
        var bloodline = catalog.Bloodline(request.BloodlineId);
        var school = CreationCatalog.ResolveSchool(request.SchoolId, request.RaceId);
        if (race is null || bloodline is null || bloodline.RaceId != race.Id || school is null || request.Gender is < 0 or > 2
            || catalog.StarterFor(school.CorporationId) is not { } start)
        {
            return new CreationRefused(CreationRefusal.InvalidChoice);
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PgCharacterStore.LockAccountAsync(connection, transaction, request.AccountId, cancellationToken);
        if (await characters.CountForAccountAsync(connection, transaction, request.AccountId, cancellationToken) >= SlotsPerAccount)
        {
            return new CreationRefused(CreationRefusal.SlotsFull);
        }

        long characterId;
        try
        {
            characterId = await characters.InsertAsync(
                connection,
                transaction,
                new NewCharacter(
                    request.AccountId, CharacterNames.Clean(request.Name), request.Gender, race.Id, bloodline.Id, request.AncestryId, school.Id,
                    CreationCatalog.CharacterTypeId(bloodline.Id), start.CorporationId, start.FactionId, start.StationId, start.SolarSystemId,
                    start.ConstellationId, start.RegionId, request.DollInfo, request.PortraitInfo),
                cancellationToken);
        }
        catch (NameTakenException)
        {
            return new CreationRefused(CreationRefusal.NameTaken);
        }

        var shipId = await items.CreateAsync(
            connection, transaction, new NewItem(race.ShipTypeId, characterId, start.StationId, ItemFlags.Hangar, Singleton: true), cancellationToken);
        await characters.SetShipAsync(connection, transaction, characterId, shipId, cancellationToken);
        var starting = skillCatalog.StartingSet(race.Id)
            ?? throw new InvalidOperationException($"race {race.Id} starts with a skill that is not in the skill catalog: the static data does not agree with itself");
        await skills.GrantAsync(connection, transaction, characterId, starting, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Created(characterId, shipId);
    }
}
