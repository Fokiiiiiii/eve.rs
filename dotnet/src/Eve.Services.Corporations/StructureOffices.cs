using Npgsql;

namespace Eve.Services.Corporations;

/// <summary>A structure as an office site: its type and system, and the corporation that owns it.</summary>
public sealed record StructureOfficeSite(long StructureId, int TypeId, long SolarSystemId, long OwnerCorporationId);

/// <summary>
/// The structure side of <c>officeManager</c> (EVE.js officeManagerService structure branch): the site, and the access and rent of its Corporation Offices service.
/// The Host implements it over the structure and profile stores.
/// </summary>
public interface IStructureOffices
{
    /// <summary>The structure as an office site, or null when there is no such structure (or it is destroyed).</summary>
    Task<StructureOfficeSite?> ReadAsync(long structureId, CancellationToken cancellationToken);

    /// <summary>Whether the character may use the structure's Corporation Offices service (the service is online and the profile's access admits the character).</summary>
    Task<bool> MayRentAsync(long characterId, long structureId, CancellationToken cancellationToken);

    /// <summary>The rent quoted to the character for an office here: the profile's Corporation Office Rent setting that applies to the character (0 when none does).</summary>
    Task<decimal> RentForAsync(long characterId, long structureId, CancellationToken cancellationToken);

    /// <summary>Moves the corporation's items at the structure into a corporate asset safety wrap, in the caller's transaction; false when the wrap is refused.</summary>
    Task<bool> MoveOfficeToSafetyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StructureOfficeSite site, long corporationId, CancellationToken cancellationToken);

    /// <summary>Schedules the office's rent due job in the caller's transaction (one per office: a later schedule replaces the waiting one).</summary>
    Task ScheduleRentDueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long officeId, long dueAtMs, CancellationToken cancellationToken);
}
