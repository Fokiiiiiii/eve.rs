using System.Globalization;

namespace Eve.Services.Slash;

/// <summary>A pin of a planet as the game master lists it: its ID and its type.</summary>
public sealed record GmPlanetPin(long PinId, int TypeId);

/// <summary>A planet of a character as the game master lists it: its ID, its type and its pins.</summary>
public sealed record GmPlanet(long PlanetId, int TypeId, IReadOnlyList<GmPlanetPin> Pins);

/// <summary>What a planet change answers: the words of a refusal (null when it was made) and the planet it changed.</summary>
public sealed record GmPiAnswer(string? Refusal, long? PlanetId = null);

/// <summary>What the planet commands need from the host: a character's planets, and the two changes the game master may make to a pin of one.</summary>
public interface IPiControl
{
    /// <summary>The planets of the character, with their pins.</summary>
    Task<IReadOnlyList<GmPlanet>> PlanetsAsync(long characterId, CancellationToken cancellationToken);

    /// <summary>Adds a commodity to a pin of the character's planet, as the planet's game master call does.</summary>
    Task<GmPiAnswer> CommodityAsync(long characterId, long pinId, int typeId, long quantity, CancellationToken cancellationToken);

    /// <summary>Converts the command center of the character's planet (the pin named) to the type of its planet.</summary>
    Task<GmPiAnswer> CommandCenterAsync(long characterId, long pinId, CancellationToken cancellationToken);
}

/// <summary>
/// The game master's planet helpers (<c>/pi</c>): <c>list</c> shows the caller's planets and their pins; <c>commodity &lt;pinId&gt; &lt;typeId&gt; &lt;quantity&gt;</c> and
/// <c>command &lt;pinId&gt;</c> change a pin of one of them. The pin is named by its ID (from <c>list</c>), so a helper never picks a pin of its own. The planet
/// refusals of the planet service (a pin of another kind, a type it does not take) are the client's error keys, and they reach the caller as the planet service raises them.
/// </summary>
public sealed class PiCommands(IPiControl planets)
{
    public const string Usage = "Usage: /pi list | commodity <pinId> <typeId> <quantity> | command <pinId>";

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before using /pi.";
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Usage;
        switch (words[0].ToLowerInvariant())
        {
            case "list" when words.Length == 1:
                var listed = await planets.PlanetsAsync(character, cancellationToken);
                if (listed.Count == 0) return $"No planet for character {character}.";
                return string.Join("\n", listed.Select(planet =>
                    $"Planet {planet.PlanetId} (type {planet.TypeId}): pins {(planet.Pins.Count == 0 ? "none" : string.Join(", ", planet.Pins.Select(pin => $"{pin.PinId}:{pin.TypeId}")))}."));
            case "commodity" when words.Length == 4:
                if (!long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pin) || pin <= 0
                    || !int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out var type) || type <= 0
                    || !long.TryParse(words[3], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                    return Usage;
                var added = await planets.CommodityAsync(character, pin, type, quantity, cancellationToken);
                return added.Refusal ?? $"Added {quantity} of type {type} to pin {pin} on planet {added.PlanetId}.";
            case "command" when words.Length == 2:
                if (!long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var center) || center <= 0) return Usage;
                var converted = await planets.CommandCenterAsync(character, center, cancellationToken);
                return converted.Refusal ?? $"Converted the command center pin {center} on planet {converted.PlanetId}.";
            default:
                return Usage;
        }
    }
}
