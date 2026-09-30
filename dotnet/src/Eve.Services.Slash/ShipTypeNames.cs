using System.Text.RegularExpressions;
using Eve.Sde;

namespace Eve.Services.Slash;

/// <summary>
/// The ship types by name, for the commands that take one (<c>/deathtest Ibis 3</c>): a port of <c>resolveShipByName</c> of EVE.js
/// (<c>services/chat/shipTypeRegistry.js</c>, ADR 0009): a name that is exactly one ship's, else the one ship whose name holds it; several are ambiguous, and
/// the reference lists the first five.
/// </summary>
public sealed partial class ShipTypeNames(SdeReader sde)
{
    private const int ShipCategory = 6;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<(int TypeId, string Name, bool Published)>? _ships;

    /// <summary><c>normalizeShipName</c>: lower case, every run of anything but letters and digits one space, trimmed.</summary>
    public static string Normalize(string value) => NonAlphanumeric().Replace(value.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    private async Task<List<(int TypeId, string Name, bool Published)>> ShipsAsync(CancellationToken cancellationToken)
    {
        if (_ships is { } known)
        {
            return known;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ships is { } ready)
            {
                return ready;
            }
            var ships = (await sde.ReadAsync("groups", cancellationToken: cancellationToken))
                .Where(row => row.Doc.TryGetProperty("categoryID", out var category) && category.GetInt32() == ShipCategory)
                .Select(row => (int)row.Key)
                .ToHashSet();
            return _ships = (await sde.ReadAsync("types", cancellationToken: cancellationToken))
                .Where(row => ships.Contains(row.Doc.GetProperty("groupID").GetInt32()))
                .Select(row => ((int)row.Key, SdeReader.English(row.Doc.GetProperty("name")).Trim(), !row.Doc.TryGetProperty("published", out var published) || published.ValueKind != System.Text.Json.JsonValueKind.False))
                .Where(ship => ship.Item2.Length > 0)
                .OrderBy(ship => ship.Item1)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Label((int TypeId, string Name, bool Published) ship) => $"{ship.Name} ({ship.TypeId}{(ship.Published ? "" : ", unpublished")})";

    /// <summary>The ship type a name (or a type id) is. <see cref="ShipMatch.Error"/> says why there is none: not found, or ambiguous with the first five it could be.</summary>
    public async Task<ShipMatch> FindAsync(string query, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0)
        {
            return new ShipMatch(null, "SHIP_NAME_REQUIRED", []);
        }
        var ships = await ShipsAsync(cancellationToken);
        if (int.TryParse(normalized, out var typeId) && ships.FirstOrDefault(ship => ship.TypeId == typeId) is { TypeId: > 0 } byId)
        {
            return new ShipMatch(byId.TypeId, null, []);
        }
        var exact = ships.Where(ship => Normalize(ship.Name) == normalized).ToList();
        if (exact.Count == 1)
        {
            return new ShipMatch(exact[0].TypeId, null, []);
        }
        if (exact.Count > 1)
        {
            return new ShipMatch(null, "AMBIGUOUS_SHIP_NAME", [.. exact.Take(5).Select(Label)]);
        }
        var partial = ships.Where(ship => Normalize(ship.Name).Contains(normalized, StringComparison.Ordinal)).ToList();
        if (partial.Count == 1)
        {
            return new ShipMatch(partial[0].TypeId, null, []);
        }
        return new ShipMatch(null, partial.Count > 1 ? "AMBIGUOUS_SHIP_NAME" : "SHIP_NOT_FOUND", [.. partial.Take(5).Select(Label)]);
    }
}

/// <summary>A ship type found by name, or why none was.</summary>
public sealed record ShipMatch(int? TypeId, string? Error, IReadOnlyList<string> Suggestions);
