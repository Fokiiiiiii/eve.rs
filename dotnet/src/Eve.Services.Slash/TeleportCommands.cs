using Eve.Services;
using Eve.Macho;

namespace Eve.Services.Slash;

/// <summary>A named solar system and, when SDE stargate data is available, the radial point used for a GM arrival.</summary>
public sealed record GmSolarSystem(int Id, string Name, IReadOnlyList<double>? Position = null, IReadOnlyList<double>? Direction = null, string? AnchorName = null);

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

    /// <summary>
    /// Where a connected pilot is, exactly: its ship's place in space (<see cref="GmDestination.Position"/>), or the anchor of its system (no position) when it is
    /// docked; null when it is in no solar system.
    /// </summary>
    Task<GmDestination?> PlaceOfAsync(ClientSession pilot, CancellationToken cancellationToken);

    /// <summary>Where an item of the caller's solar system is (a celestial or a pilot's ship), or null when the simulation does not know it there.</summary>
    Task<GmDestination?> ItemPlaceAsync(ClientSession caller, long itemId, CancellationToken cancellationToken);

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
    public const string TrUsage = "Usage: /tr <me|characterID> <destination|pos=x,y,z|offset=x,y,z>";
    public const string WarpToUsage = "Usage: /warpto <itemID>";

    /// <summary>How far off an item <c>/warpto</c> puts the pilot, metres: the same place as <c>/tele</c> (the host's rule).</summary>
    public const double WarpToOffsetMeters = 2_500d;

    /// <summary><c>/solar &lt;system&gt;</c>: the pilot goes to the anchor of the system.</summary>
    public async Task<string> SolarAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /solar.";
        var token = argument.Trim();
        if (token.Length == 0) return SolarUsage;
        if (await relocation.FindSolarSystemAsync(token, cancellationToken) is not { } system)
            return $"Solar system not found: {token}.";
        var destination = new GmDestination(system.Id, system.Name, system.Position, system.Direction);
        var anchor = system.AnchorName is { Length: > 0 } name ? $" near stargate {name}" : "";
        return await relocation.MoveAsync(session, destination, cancellationToken) ?? $"Teleported to {system.Name} ({system.Id}){anchor}.";
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

    /// <summary>
    /// <c>/tr &lt;me|character&gt; &lt;destination&gt;</c> (EVE.js <c>handleTransportCommand</c>): the pilot is moved to a solar system's anchor, to a place given as
    /// <c>pos=x,y,z</c> (or three numbers) in the pilot's system, to a place offset by <c>offset=x,y,z</c> from the pilot or from a destination, or to another pilot's
    /// place (<c>me</c> is the caller's). <c>noblock</c> is ignored. The pilot's ship is put in space at the place (no gate, range or timer rule applies).
    /// </summary>
    public async Task<string> TrAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } self) return "Select a character before using /tr.";
        var tokens = argument.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !token.Equals("noblock", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tokens.Length < 2) return TrUsage;

        var pilot = tokens[0].Equals("me", StringComparison.OrdinalIgnoreCase)
            ? new GmPilot(self, session)
            : await relocation.FindPilotAsync(tokens[0], cancellationToken);
        if (pilot is null) return $"Transport target not found: {tokens[0]}.";
        if (pilot.Session is not { } target) return $"Character {pilot.CharacterId} is not currently connected.";
        var targetLabel = target == session ? "me" : $"character {pilot.CharacterId}";

        var rest = tokens[1..];
        var offset = Vector(rest[^1], "offset");
        var from = offset is null ? rest : rest[..^1];
        GmDestination destination;
        if (rest.Length == 1 && Vector(rest[0], "pos") is { } point)
        {
            if (await relocation.PlaceOfAsync(target, cancellationToken) is not { } context)
                return "Could not resolve a solar-system context for /tr pos=...";
            destination = Point(context, point);
        }
        else if (rest.Length == 3 && Triple(rest) is { } raw)
        {
            if (await relocation.PlaceOfAsync(target, cancellationToken) is not { } context)
                return "Could not resolve a solar-system context for raw /tr coordinates.";
            destination = Point(context, raw);
        }
        else if (from.Length == 0)
        {
            // only an offset: from the pilot's own place, which must be in space
            if (await relocation.PlaceOfAsync(target, cancellationToken) is not { Position: { } at } place || offset is null)
                return "Could not apply /tr offset=... because the target has no in-space anchor.";
            destination = Point(place, Add(at, offset));
        }
        else if (from.Length == 1)
        {
            if (await LocationAsync(session, from[0], cancellationToken) is not { } location)
                return $"Transport destination not found: {from[0]}.";
            if (offset is null)
            {
                destination = location;
            }
            else if (location.Position is not { } at)
            {
                return "Could not apply /tr offset=... to that destination.";
            }
            else
            {
                destination = Point(location, Add(at, offset));
            }
        }
        else
        {
            return TrUsage;
        }

        var answer = await relocation.MoveAsync(target, destination, cancellationToken);
        return answer ?? $"Transported {targetLabel} to {Describe(destination)}.";
    }

    /// <summary>
    /// <c>/warpto &lt;itemID&gt;</c>: the caller's ship is put <see cref="WarpToOffsetMeters"/> off the item, facing it. Only a pilot in space warps (the client's
    /// warp-to rule); the item is a celestial or a pilot's ship of the caller's system.
    /// </summary>
    public async Task<string> WarpToAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /warpto.";
        var token = argument.Trim();
        if (!long.TryParse(token, out var itemId)) return WarpToUsage;
        if (await relocation.PlaceOfAsync(session, cancellationToken) is not { Position: not null })
            return "Undock before using /warpto.";
        if (await relocation.ItemPlaceAsync(session, itemId, cancellationToken) is not { Position: { } at } item)
            return $"Item not found in your solar system: {itemId}.";
        var destination = new GmDestination(item.SolarSystemId, item.SolarSystemName, [at[0] + WarpToOffsetMeters, at[1], at[2]], [-1d, 0d, 0d]);
        return await relocation.MoveAsync(session, destination, cancellationToken) ?? $"Warped to item {itemId} in {item.SolarSystemName} ({item.SolarSystemId}).";
    }

    /// <summary>The place a destination token names: <c>me</c> (the caller's), a solar system (ID or name), or a connected character's place.</summary>
    private async Task<GmDestination?> LocationAsync(ClientSession session, string token, CancellationToken cancellationToken)
    {
        if (token.Equals("me", StringComparison.OrdinalIgnoreCase))
            return await relocation.PlaceOfAsync(session, cancellationToken);
        if (await relocation.FindSolarSystemAsync(token, cancellationToken) is { } system)
            return new GmDestination(system.Id, system.Name);
        if (await relocation.FindPilotAsync(token, cancellationToken) is { Session: { } other })
            return await relocation.PlaceOfAsync(other, cancellationToken);
        return null;
    }

    private static GmDestination Point(GmDestination context, IReadOnlyList<double> at) =>
        new(context.SolarSystemId, context.SolarSystemName, at, [1d, 0d, 0d]);

    private static IReadOnlyList<double> Add(IReadOnlyList<double> at, IReadOnlyList<double> offset) =>
        [at[0] + offset[0], at[1] + offset[1], at[2] + offset[2]];

    private static string Describe(GmDestination destination) =>
        destination.Position is { } at
            ? FormattableString.Invariant($"({at[0]}, {at[1]}, {at[2]})")
            : $"{destination.SolarSystemName} ({destination.SolarSystemId})";

    /// <summary><c>key=x,y,z</c> as three numbers, or null when the token is not that form.</summary>
    private static IReadOnlyList<double>? Vector(string token, string key)
    {
        var prefix = key + "=";
        return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? Triple(token[prefix.Length..].Split(',')) : null;
    }

    /// <summary>Three numbers (as tokens or as the parts of a comma list), or null.</summary>
    private static IReadOnlyList<double>? Triple(IReadOnlyList<string> parts)
    {
        if (parts.Count != 3) return null;
        var values = new double[3];
        for (var i = 0; i < 3; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i]))
                return null;
        return values;
    }
}
