using Eve.Planets;
using Eve.Services.Planets;
using Eve.Services.Slash;

namespace Eve.Host;

/// <summary>
/// The game master's planet helpers on this host (<see cref="IPiControl"/>): the character's colonies are the planet service's, and a pin is found in the colony of
/// the character that holds it; the change is the planet service's change path (the one its game master calls take).
/// </summary>
internal sealed class HostPiControl(PlanetMgrService planets) : IPiControl
{
    public async Task<IReadOnlyList<GmPlanet>> PlanetsAsync(long characterId, CancellationToken cancellationToken) =>
        (await planets.GmColoniesAsync(characterId, cancellationToken))
            .Select(colony => new GmPlanet(colony.PlanetId, colony.PlanetTypeId, colony.Pins.Select(pin => new GmPlanetPin(pin.Id, pin.TypeId)).ToArray()))
            .ToArray();

    public async Task<GmPiAnswer> CommodityAsync(long characterId, long pinId, int typeId, long quantity, CancellationToken cancellationToken) =>
        await PlanetOfPinAsync(characterId, pinId, cancellationToken) is not { } planetId
            ? new GmPiAnswer($"No pin {pinId} is on a planet of character {characterId}.")
            : await ChangeAsync(characterId, planetId, new GmAddCommodityCommand(pinId, typeId, quantity), cancellationToken);

    public async Task<GmPiAnswer> CommandCenterAsync(long characterId, long pinId, CancellationToken cancellationToken) =>
        await PlanetOfPinAsync(characterId, pinId, cancellationToken) is not { } planetId
            ? new GmPiAnswer($"No pin {pinId} is on a planet of character {characterId}.")
            : await ChangeAsync(characterId, planetId, new GmConvertCommandCenterCommand(pinId), cancellationToken);

    private async Task<long?> PlanetOfPinAsync(long characterId, long pinId, CancellationToken cancellationToken) =>
        (await planets.GmColoniesAsync(characterId, cancellationToken)).FirstOrDefault(colony => colony.Pins.Any(pin => pin.Id == pinId))?.PlanetId;

    private async Task<GmPiAnswer> ChangeAsync(long characterId, long planetId, ColonyCommand command, CancellationToken cancellationToken)
    {
        await planets.GmChangeAsync(characterId, planetId, command, cancellationToken);
        return new GmPiAnswer(null, planetId);
    }
}
