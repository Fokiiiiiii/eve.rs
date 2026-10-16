using Eve.Characters;
using Eve.Services.Core;
using Eve.Skills;
using Npgsql;

namespace Eve.Host;

/// <summary>The jump clone service's store: the clone rows of the characters schema, and the character's skill levels from PostgreSQL.</summary>
internal sealed class HostJumpCloneStore(PgJumpCloneStore clones, NpgsqlDataSource source, PgSkillStore skills) : IJumpCloneStore
{
    public async ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken)
    {
        var snapshot = await clones.ReadAsync(characterId, cancellationToken);
        return new JumpCloneSnapshot([.. snapshot.Clones.Select(Map)], snapshot.TimeLastJumpFileTime);
    }

    public async ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken) =>
        await clones.RenameAsync(characterId, jumpCloneId, name, cancellationToken);

    public async ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken) =>
        await clones.DestroyAsync(characterId, jumpCloneId, cancellationToken) is { } removed ? Map(removed) : null;

    public async ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken) =>
        await clones.ResetLastJumpAsync(characterId, cancellationToken);

    public async ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return await skills.LevelAsync(connection, transaction, characterId, typeId, cancellationToken);
    }

    private static JumpCloneRecord Map(InstalledJumpClone clone) =>
        new(clone.JumpCloneId, clone.LocationId, clone.LocationKind, clone.Name, clone.ImplantTypeIds);
}
