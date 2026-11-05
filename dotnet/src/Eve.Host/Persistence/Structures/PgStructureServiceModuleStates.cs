using Eve.Fitting;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

internal sealed class PgStructureServiceModuleStates(PgModuleStateStore modules) : IStructureServiceModuleStates
{
    public Task SetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long moduleId,
        bool online,
        CancellationToken cancellationToken) =>
        modules.SetAsync(connection, transaction, moduleId, online ? Eve.Dogma.ItemState.Online : Eve.Dogma.ItemState.Offline, cancellationToken);
}
