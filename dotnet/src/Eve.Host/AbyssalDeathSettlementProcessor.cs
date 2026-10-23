using Eve.Characters;
using Eve.Content;
using Eve.Inventory;
using Eve.Ships;
using Eve.Universe;
using Npgsql;

namespace Eve.Host;

public sealed record AbyssalDeathSettlementOutcome(
    long CharacterId,
    long DestroyedShipId,
    long ReplacementShipId,
    long StationId,
    int SolarSystemId,
    int ConstellationId,
    int RegionId,
    ShipDeath? ShipDeath,
    PodDeath? PodDeath)
{
    public bool AuthorityAlreadyCompleted { get; init; }
}

/// <summary>Runs the normal ship-loss and capsule-clone authorities for one durable Domain death request.</summary>
public sealed class AbyssalDeathSettlementProcessor(
    CharacterShips ships,
    PgCharacterStore characters,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    CreationCatalog creation,
    StationCatalog stations)
{
    // These are the target build's client corpse types used by SpaceSessions.Death.cs.
    private const int CorpseTypeMale = 25;
    private const int CorpseTypeFemale = 29148;

    public async Task<AbyssalDeathSettlementOutcome> SettleAsync(
    AbyssalParticipantDeathSettlementRequestedEvent request,
    int? wreckTypeId,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(request);
    if (request.InstanceId <= 0 || !Guid.TryParse(request.ContentId, out _) ||
        request.RecipientCharacterId <= 0 || request.ShipId <= 0 ||
        request.SpaceLocationId <= 0 || request.RoomOrdinal is < 1 or > 3 ||
        !string.Equals(request.SettlementKey,
            $"abyssal:{request.ContentId}:death:{request.RecipientCharacterId}",
            StringComparison.Ordinal) ||
        wreckTypeId is <= 0)
    {
        throw new InvalidDataException("The Abyssal death request has an invalid persisted identity.");
    }

    var scope = new RunSpaceItemScope(Guid.Parse(request.ContentId), request.InstanceId, request.RoomOrdinal,
        request.SpaceLocationId, request.RecipientCharacterId, request.SettlementKey);
    var character = await RequireCharacterAsync(request.RecipientCharacterId, cancellationToken).ConfigureAwait(false);
    var rookieShipTypeId = creation.Race(character.RaceId)?.ShipTypeId
        ?? throw new InvalidDataException($"The rookie ship for race {character.RaceId} is absent from the character catalog.");
    var homeStation = HomeStationOf(character)
        ?? throw new InvalidDataException($"Character {character.Id} has no valid clone station.");
    var home = new HomePlace(
        homeStation.Id,
        homeStation.SolarSystemId,
        homeStation.ConstellationId,
        homeStation.RegionId);

    var currentShipId = character.ShipId
        ?? throw new InvalidDataException($"Character {character.Id} has no ship during Abyssal death settlement.");
    long capsuleId = 0;
    ShipDeath? shipDeath = null;
    if (currentShipId == request.ShipId)
    {
        var lostShip = await items.GetAsync(request.ShipId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The active ship {request.ShipId} is missing before death settlement.");
        if (lostShip.OwnerId != request.RecipientCharacterId ||
            lostShip.LocationId != request.SpaceLocationId ||
            lostShip.Flag != CharacterShips.InSpaceFlag ||
            lostShip.TypeId == CharacterShips.CapsuleTypeId ||
            character.SolarSystemId != request.SpaceLocationId)
        {
            throw new InvalidDataException($"The active ship {request.ShipId} is not persisted in its Abyssal system.");
        }

        if (wreckTypeId is { } typeId && await itemTypes.GetAsync(typeId, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidDataException($"Abyssal wreck type {typeId} is absent from the pinned SDE.");

        shipDeath = await ships.DestroyShipAsync(
            request.RecipientCharacterId,
            request.ShipId,
            wreckTypeId,
            request.SettlementKey,
            cancellationToken, runSpaceScope: wreckTypeId is null ? null : scope).ConfigureAwait(false);
        if (shipDeath is not null)
        {
            capsuleId = shipDeath.Capsule?.Id
                ?? throw new InvalidDataException("The Abyssal participant's hull loss did not create a capsule.");
        }
    }

    if (capsuleId == 0)
    {
        character = await RequireCharacterAsync(request.RecipientCharacterId, cancellationToken).ConfigureAwait(false);
        currentShipId = character.ShipId
            ?? throw new InvalidDataException($"Character {character.Id} has no ship while resuming Abyssal death settlement.");
        var currentShip = await items.GetAsync(currentShipId, cancellationToken).ConfigureAwait(false);
        if (currentShip?.TypeId == CharacterShips.CapsuleTypeId &&
            currentShip.OwnerId == character.Id &&
            currentShip.LocationId == request.SpaceLocationId &&
            currentShip.Flag == CharacterShips.InSpaceFlag &&
            character.SolarSystemId == request.SpaceLocationId)
        {
            capsuleId = currentShipId;
        }
        else if (await IsAlreadyAtCloneAsync(character, request.ShipId, homeStation.Id, rookieShipTypeId, cancellationToken).ConfigureAwait(false))
        {
            return Outcome(character, request.ShipId, currentShipId, homeStation, null, null, authorityAlreadyCompleted: true);
        }
        else
        {
            throw new InvalidDataException($"The active ship state for character {character.Id} cannot resume Abyssal death settlement.");
        }
    }

    var corpseTypeId = character.Gender == 0 ? CorpseTypeFemale : CorpseTypeMale;
    if (await itemTypes.GetAsync(corpseTypeId, cancellationToken).ConfigureAwait(false) is null)
        throw new InvalidDataException($"The corpse type {corpseTypeId} is absent from the pinned SDE.");

    var podDeath = await ships.DestroyCapsuleAsync(
        character.Id,
        capsuleId,
        corpseTypeId,
        character.Name,
        rookieShipTypeId,
        home,
        cancellationToken, runSpaceScope: scope).ConfigureAwait(false);
    if (podDeath is not null)
        return Outcome(podDeath.Character, request.ShipId, podDeath.Ship.Id, homeStation, shipDeath, podDeath);

    character = await RequireCharacterAsync(request.RecipientCharacterId, cancellationToken).ConfigureAwait(false);
    currentShipId = character.ShipId
        ?? throw new InvalidDataException($"Character {character.Id} has no ship after capsule settlement.");
    if (await IsAlreadyAtCloneAsync(character, request.ShipId, homeStation.Id, rookieShipTypeId, cancellationToken).ConfigureAwait(false))
        return Outcome(character, request.ShipId, currentShipId, homeStation, shipDeath, null, authorityAlreadyCompleted: true);
    throw new InvalidDataException($"Capsule {capsuleId} was not settled by the normal clone authority.");
}

    private async Task<Character> RequireCharacterAsync(long characterId, CancellationToken cancellationToken) =>
        await characters.GetAsync(characterId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException($"Abyssal death recipient character {characterId} does not exist.");

    private Eve.Universe.Station? HomeStationOf(Character character)
    {
        if (character.HomeStationId > 0 && stations.Get(character.HomeStationId) is { } home)
            return home;
        return stations.Get(character.StationId);
    }

    private async Task<bool> IsAlreadyAtCloneAsync(
        Character character,
        long destroyedShipId,
        long homeStationId,
        int rookieShipTypeId,
        CancellationToken cancellationToken)
    {
        if (character.StationId != homeStationId ||
            character.ShipId is not { } shipId ||
            shipId == destroyedShipId)
        {
            return false;
        }

        var ship = await items.GetAsync(shipId, cancellationToken).ConfigureAwait(false);
        if (ship is null ||
            ship.OwnerId != character.Id ||
            ship.TypeId != rookieShipTypeId ||
            ship.LocationId != homeStationId ||
            ship.Flag != ItemFlags.Hangar)
        {
            return false;
        }
        return await items.GetAsync(destroyedShipId, cancellationToken).ConfigureAwait(false) is null;
    }

    private static AbyssalDeathSettlementOutcome Outcome(
        Character character,
        long destroyedShipId,
        long replacementShipId,
        Station station,
        ShipDeath? shipDeath,
        PodDeath? podDeath,
        bool authorityAlreadyCompleted = false)
    {
        return new AbyssalDeathSettlementOutcome(
            character.Id,
            destroyedShipId,
            replacementShipId,
            station.Id,
            station.SolarSystemId,
            station.ConstellationId,
            station.RegionId,
            shipDeath,
            podDeath)
        {
            AuthorityAlreadyCompleted = authorityAlreadyCompleted,
        };
    }
}
