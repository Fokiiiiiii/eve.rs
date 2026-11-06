using Eve.Content;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host;

/// <summary>Read-only Abyssal projection used by keeper.GetCurrentDungeonForCharacter and client resync.</summary>
public sealed class AbyssalDungeonReadModel(
    NpgsqlDataSource source,
    AbyssalContentRuntime runtime,
    AbyssalStaticCatalog catalog,
    SpaceSessions spaceSessions)
{
    public async ValueTask<PyValue?> ReadCurrentDungeonForCharacterAsync(
        ClientSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.CharacterId is not { } characterId || characterId <= 0)
            return null;

        var shipId = spaceSessions.ShipIdOf(session);
        if (shipId is not > 0)
            return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var binding = await runtime.FindActiveForParticipantAsync(
            connection,
            transaction,
            characterId,
            shipId.Value,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (binding is null)
            return null;

        var room = binding.Run.Data.Plan.Rooms.SingleOrDefault(
            plan => plan.LocationId == binding.Participant.CurrentRoomIndex)
            ?? throw new InvalidDataException(
                $"Abyssal run {binding.Run.InstanceId} has no plan for room ordinal {binding.Participant.CurrentRoomIndex}.");
        var environment = catalog.GetEnvironmentRoom(room.EnvironmentProfileId);
        return AbyssalDungeonProjection.CurrentInfo(binding.Run.InstanceId, room, environment);
    }
}