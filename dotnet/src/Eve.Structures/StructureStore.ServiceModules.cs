using Eve.Data;
using Npgsql;

namespace Eve.Structures;

public sealed partial class StructureStore
{
    public async Task<StructureServiceModuleFuelCycle?> ServiceModuleFuelCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long structureId,
        long moduleId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q("SELECT module_id, module_type_id, service_ids, online_since_ms, next_fuel_cycle_ms, fuel_remainder FROM {schema}.structure_service_module_fuel WHERE structure_id = $1 AND module_id = $2"),
            structureId, moduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StructureServiceModuleFuelCycle(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<int[]>(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetDouble(5))
            : null;
    }

    public async Task<IReadOnlyList<StructureServiceModuleFuelCycle>> ServiceModuleFuelCyclesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long structureId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q("SELECT module_id, module_type_id, service_ids, online_since_ms, next_fuel_cycle_ms, fuel_remainder FROM {schema}.structure_service_module_fuel WHERE structure_id = $1 ORDER BY module_id"),
            structureId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var cycles = new List<StructureServiceModuleFuelCycle>();
        while (await reader.ReadAsync(cancellationToken))
            cycles.Add(new StructureServiceModuleFuelCycle(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<int[]>(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetDouble(5)));
        return cycles;
    }

    public async Task SetServiceModuleFuelCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        long moduleId,
        int moduleTypeId,
        int[] serviceIds,
        long onlineSinceMs,
        long nextFuelCycleMs,
        double fuelRemainder,
        CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            INSERT INTO {schema}.structure_service_module_fuel
                (structure_id, module_id, module_type_id, service_ids, online_since_ms, next_fuel_cycle_ms, fuel_remainder)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (structure_id, module_id) DO UPDATE SET
                module_type_id = EXCLUDED.module_type_id, service_ids = EXCLUDED.service_ids,
                online_since_ms = EXCLUDED.online_since_ms, next_fuel_cycle_ms = EXCLUDED.next_fuel_cycle_ms,
                fuel_remainder = EXCLUDED.fuel_remainder
            """), cancellationToken, structureId, moduleId, moduleTypeId, serviceIds, onlineSinceMs, nextFuelCycleMs, fuelRemainder);

    public async Task<bool> HasOnlineServiceModuleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long structureId,
        int serviceId,
        CancellationToken cancellationToken = default) =>
        await Sql.ScalarAsync(connection, transaction,
            Q("SELECT EXISTS (SELECT 1 FROM {schema}.structure_service_module_fuel WHERE structure_id = $1 AND $2 = ANY(service_ids))"),
            cancellationToken, structureId, serviceId) is true;

    public async Task DeleteServiceModuleFuelCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        long moduleId,
        CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction,
            Q("DELETE FROM {schema}.structure_service_module_fuel WHERE structure_id = $1 AND module_id = $2"),
            cancellationToken, structureId, moduleId);

    public async Task DeleteAllServiceModuleFuelCyclesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction,
            Q("DELETE FROM {schema}.structure_service_module_fuel WHERE structure_id = $1"),
            cancellationToken, structureId);

    public async Task AdvanceServiceModuleFuelCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        long moduleId,
        long elapsedCycles,
        double fuelRemainder,
        CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction,
            Q("UPDATE {schema}.structure_service_module_fuel SET next_fuel_cycle_ms = next_fuel_cycle_ms + $3, fuel_remainder = $4 WHERE structure_id = $1 AND module_id = $2"),
            cancellationToken, structureId, moduleId, checked(elapsedCycles * StructureFuel.HourMs), fuelRemainder);
}
