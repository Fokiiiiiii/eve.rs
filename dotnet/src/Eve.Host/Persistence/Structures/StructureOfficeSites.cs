using Eve.Scheduler;
using Eve.Services.Corporations;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The structure side of corporation offices (ST-12): a structure as an office site, and the access and rent of its Corporation Offices service, read from the
/// structure records and the profile directory that the structure services use.
/// </summary>
internal sealed class StructureOfficeSites(
    NpgsqlDataSource source,
    StructureStore structures,
    PersistenceStructureDirectoryReadModel directory,
    PersistenceStructureAssetSafetyReadModel assetSafety,
    DurableScheduler scheduler) : IStructureOffices
{
    public async Task<StructureOfficeSite?> ReadAsync(long structureId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        if (await structures.GetAsync(connection, null, structureId, false, cancellationToken) is not { } record ||
            record.Snapshot.DestroyedMs > 0 || record.OwnerCorporationId <= 0)
            return null;
        return new StructureOfficeSite(structureId, record.TypeId, record.SolarSystemId, record.OwnerCorporationId);
    }

    public async Task<bool> MayRentAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
        await directory.CharacterHasServiceAsync(characterId, structureId, StructureServiceIds.Offices, cancellationToken);

    public async Task<decimal> RentForAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
        await directory.CharacterOfficeRentAsync(characterId, structureId, cancellationToken);

    public Task<bool> MoveOfficeToSafetyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StructureOfficeSite site, long corporationId,
        CancellationToken cancellationToken) =>
        assetSafety.CreateCorporationOfficeWrapAsync(connection, transaction, site.StructureId, site.SolarSystemId, corporationId, cancellationToken);

    public async Task ScheduleRentDueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long officeId, long dueAtMs,
        CancellationToken cancellationToken)
    {
        await scheduler.ScheduleAsync(connection, transaction,
            new JobSpec(StructureOfficeRentDue.JobKind, dueAtMs) { Key = "office:" + officeId, Payload = new { OfficeId = officeId } }, cancellationToken);
    }
}
