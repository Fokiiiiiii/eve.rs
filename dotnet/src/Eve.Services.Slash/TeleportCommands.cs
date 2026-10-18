using Eve.Services;
using Eve.Macho;

namespace Eve.Services.Slash;

/// <summary>A solar system a game master names: its ID and its name.</summary>
public sealed record GmSolarSystem(int Id, string Name);

/// <summary>A character a game master names, with its connected session (null when the character is not connected).</summary>
public sealed record GmPilot(long CharacterId, ClientSession? Session);

/// <summary>
/// Where a relocation takes a pilot: a solar system, with the place in it (<see cref="Position"/> and <see cref="Direction"/>; null for the system's anchor).
/// </summary>
public sealed record GmDestination(int SolarSystemId, string SolarSystemName, IReadOnlyList<double>? Position = null, IReadOnlyList<double>? Direction = null);

/// <summary>What the teleport commands need from the host: the names the game master gives, the pilots that are connected, and the relocation itself.</summary>
public interface IGmRelocation
{
    /// <summary>The solar system a name (exact, not case-sensitive) or an ID names, or null.</summary>
    Task<GmSolarSystem?> FindSolarSystemAsync(string token, CancellationToken cancellationToken);

    /// <summary>The character a name or an ID names, or null when none is.</summary>
    Task<GmPilot?> FindPilotAsync(string token, CancellationToken cancellationToken);

    /// <summary>Where a connected pilot is, as a destination for a game master to go to; null when it is in no solar system.</summary>
    Task<GmDestination?> DestinationNearAsync(ClientSession pilot, CancellationToken cancellationToken);

    /// <summary>Moves the session's pilot to the destination; null when it is there, else the words of the refusal.</summary>
    Task<string?> MoveAsync(ClientSession session, GmDestination destination, CancellationToken cancellationToken);
}

/// <summary>
/// The game master's teleport commands (EVE.js <c>chatCommands.js</c>, <c>/solar &lt;system name&gt;</c> and <c>/tele &lt;character name|characterID&gt;</c>). Each
/// moves the caller's pilot: <c>/solar</c> to the system's anchor, <c>/tele</c> to a place beside the target pilot (or the anchor of the system a docked target is in).
/// The move is the host's relocation (<see cref="IGmRelocation"/>): no gate, range or timer rule applies to it.
/// </summary>
public sealed class TeleportCommands(IGmRelocation relocation)
{
    public const string SolarUsage = "Usage: /solar <system name>";
    public const string TeleUsage = "Usage: /tele <character name|characterID>";

    /// <summary><c>/solar &lt;system&gt;</c>: the pilot goes to the anchor of the system.</summary>
    public async Task<string> SolarAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /solar.";
        var token = argument.Trim();
        if (token.Length == 0) return SolarUsage;
        if (await relocation.FindSolarSystemAsync(token, cancellationToken) is not { } system)
            return $"Solar system not found: {token}.";
        var destination = new GmDestination(system.Id, system.Name);
        return await relocation.MoveAsync(session, destination, cancellationToken) ?? $"Teleported to {system.Name} ({system.Id}).";
    }

    /// <summary><c>/tele &lt;character&gt;</c>: the pilot goes to the target pilot (<c>me</c> is the caller's own character).</summary>
    public async Task<string> TeleAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } self) return "Select a character before using /tele.";
        var token = argument.Trim();
        if (token.Length == 0) return TeleUsage;
        var pilot = token.Equals("me", StringComparison.OrdinalIgnoreCase)
            ? new GmPilot(self, session)
            : await relocation.FindPilotAsync(token, cancellationToken);
        if (pilot is null) return $"Character not found: {token}.";
        if (pilot.Session is not { } target) return $"Character {pilot.CharacterId} is not currently connected.";
        if (await relocation.DestinationNearAsync(target, cancellationToken) is not { } destination)
            return $"Could not resolve a live location for character {pilot.CharacterId}.";
        return await relocation.MoveAsync(session, destination, cancellationToken)
            ?? $"Teleported to character {pilot.CharacterId} in {destination.SolarSystemName} ({destination.SolarSystemId}).";
    }
}
