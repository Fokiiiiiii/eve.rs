using Eve.Services;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Universe;

namespace Eve.Host;

/// <summary>
/// The game master's relocation on this host (<see cref="IGmRelocation"/>): the system names are the solar system catalogue's, the characters are the character
/// store's (<paramref name="characters"/>, by ID or exact name), the pilot's session is the one that is connected, and the move is the space sessions' relocation.
/// </summary>
internal sealed class HostGmRelocation(SpaceSessions space, SolarSystemCatalog systems, IStandingDirectory characters, CharacterSessions sessions) : IGmRelocation
{
    public Task<GmSolarSystem?> FindSolarSystemAsync(string token, CancellationToken cancellationToken)
    {
        var found = int.TryParse(token, out var id)
            ? systems.Get(id)
            : systems.Systems.FirstOrDefault(system => system.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(found is null ? null : new GmSolarSystem(found.Id, found.Name));
    }

    public async Task<GmPilot?> FindPilotAsync(string token, CancellationToken cancellationToken)
    {
        if (await characters.FindCharacterAsync(token, cancellationToken) is not { } characterId)
            return null;
        return new GmPilot(characterId, sessions.Of(characterId));
    }

    public async Task<GmDestination?> DestinationNearAsync(ClientSession pilot, CancellationToken cancellationToken)
    {
        if (await space.PilotDestinationAsync(pilot, cancellationToken) is not { } place)
            return null;
        return new GmDestination(place.SolarSystemId, systems.Get(place.SolarSystemId)?.Name ?? place.SolarSystemId.ToString(), place.Position, place.Direction);
    }

    public async Task<GmDestination?> ItemPlaceAsync(ClientSession caller, long itemId, CancellationToken cancellationToken)
    {
        if (await space.ItemPlaceAsync(caller, itemId, cancellationToken) is not { } place)
            return null;
        return new GmDestination(place.SolarSystemId, systems.Get(place.SolarSystemId)?.Name ?? place.SolarSystemId.ToString(), place.Position);
    }

    public async Task<GmDestination?> PlaceOfAsync(ClientSession pilot, CancellationToken cancellationToken)
    {
        if (await space.PilotPlaceAsync(pilot, cancellationToken) is not { } place)
            return null;
        return new GmDestination(place.SolarSystemId, systems.Get(place.SolarSystemId)?.Name ?? place.SolarSystemId.ToString(), place.Position);
    }

    public Task<string?> MoveAsync(ClientSession session, GmDestination destination, CancellationToken cancellationToken) =>
        space.RelocateAsync(session, destination.SolarSystemId, destination.Position, destination.Direction, cancellationToken);
}
