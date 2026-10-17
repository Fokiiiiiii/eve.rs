using System.Globalization;
using Eve.Dogma;
using Eve.Services.Core;
using Eve.Services.Slash;

namespace Eve.Host;

/// <summary>
/// The owners and pilots the standing commands name: the SDE's faction names (<see cref="DogmaStatic.FactionNames"/>), the corporation store, the agents of the
/// agent catalog and the character store. A corporation or an agent is named by its ID; only factions are also named by their name (the stores keep no
/// corporation or agent names index for a name lookup).
/// </summary>
internal sealed class HostStandingDirectory(DogmaStatic statics, Eve.Corporations.CorporationStore corporations,
    IReadOnlyDictionary<long, AgentMissionAgentData> agents, Eve.Characters.PgCharacterStore characters) : IStandingDirectory
{
    public async Task<StandingOwner?> FindOwnerAsync(string token, CancellationToken cancellationToken)
    {
        var text = token.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            if (id <= int.MaxValue && statics.FactionName((int)id) is { Length: > 0 } faction) return new StandingOwner(id, faction, true);
            if (await corporations.GetAsync(id, cancellationToken) is { } corporation) return new StandingOwner(id, corporation.Name, IsCorporation: true);
            return agents.ContainsKey(id) ? new StandingOwner(id, "") : null;
        }
        var key = Normalize(text);
        return key.Length == 0 ? null : statics.FactionNames.Where(entry => Normalize(entry.Value) == key)
            .Select(entry => new StandingOwner(entry.Key, entry.Value, true)).Cast<StandingOwner?>().FirstOrDefault();
    }

    public async Task<IReadOnlyList<StandingOwner>> AgentOwnersAsync(CancellationToken cancellationToken)
    {
        var owners = new List<StandingOwner>();
        foreach (var corporationId in agents.Values.Select(agent => agent.CorporationId).Where(id => id > 0).Distinct().Order())
        {
            if (await corporations.GetAsync(corporationId, cancellationToken) is { } corporation) owners.Add(new StandingOwner(corporationId, corporation.Name, IsCorporation: true));
        }
        return owners;
    }

    public async Task<long?> FindCharacterAsync(string token, CancellationToken cancellationToken)
    {
        if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            return await characters.GetAsync(id, cancellationToken) is null ? null : id;
        return await characters.FindIdByNameAsync(token, cancellationToken);
    }

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
}
