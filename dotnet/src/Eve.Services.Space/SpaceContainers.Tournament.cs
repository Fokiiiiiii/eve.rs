using Eve.Ships;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>Read match-owned candidates inside the caller's cleanup journal transaction.</summary>
    public async Task<IReadOnlyList<SpaceItem>> TournamentWreckCandidatesAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction,
        int systemId, IReadOnlySet<long> participants, CancellationToken ct)
    {
        if (systemId <= 0) throw new ArgumentException("Invalid tournament system.");
        var candidates = await _spaceItems.InSystemAsync(connection, transaction, systemId, ct);
        return candidates.Where(item => item.GroupId == WreckGroup && item.Kind == SpaceItemKind.ContainerOrWreck &&
            item.MoonExtractionId == 0 && participants.Contains(item.CharacterId)).ToArray();
    }

    /// <summary>Find persisted match-owned wrecks; the simulation must check their live position before removal.</summary>
    public async Task<IReadOnlyList<SpaceItem>> TournamentWreckCandidatesAsync(int systemId,
        IReadOnlySet<long> participants, CancellationToken cancellationToken)
    {
        if (systemId <= 0) throw new ArgumentException("Invalid tournament system.");
        var candidates = await _spaceItems.InSystemAsync(systemId, cancellationToken);
        return candidates.Where(item => item.GroupId == WreckGroup && item.Kind == SpaceItemKind.ContainerOrWreck &&
            item.MoonExtractionId == 0 && participants.Contains(item.CharacterId)).ToArray();
    }

    /// <summary>Remove a previously acknowledged wreck and its contents from persistent space.</summary>
    public async Task FinalizeTournamentWreckAsync(int systemId, long itemId, long ownerId, long launcherId, int typeId, CancellationToken ct)
    {
        var placed = await _spaceItems.GetAsync(itemId, ct);
        if (placed is null) return;
        if (placed.SolarSystemId != systemId || placed.GroupId != WreckGroup || placed.Kind != SpaceItemKind.ContainerOrWreck ||
            placed.MoonExtractionId != 0 || placed.CharacterId != ownerId || placed.LauncherId != launcherId)
            throw new InvalidOperationException("The tournament wreck identity changed.");
        var item = await _items.GetAsync(itemId, ct);
        if (item is not null && item.TypeId != typeId) throw new InvalidOperationException("The tournament wreck type changed.");
        await _ships.RemoveContainerAsync(itemId, withContents: true, ct);
        await ForgetAsync(itemId);
    }
}
