using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class PersistenceStructureHangarViewRpcTests
{
    [Fact]
    public async Task Hangar_view_service_returns_the_persisted_state_through_its_callback()
    {
        const long structureId = 30_000_142;
        var reads = new List<string>();
        Task<JsonObject> ReadTable(string table, CancellationToken _)
        {
            reads.Add(table);
            return Task.FromResult(table == "structures"
                ? new JsonObject
                {
                    [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["state"] = 1,
                        ["upkeepState"] = 2,
                    },
                }
                : new JsonObject());
        }

        var backend = new PersistenceStructureProfilesRpcBackend(ReadTable, null!, null!);
        var callbacks = new StructureRpcCallbacks
        {
            GetMyHangarViewStateAsync = (call, id) =>
                backend.GetMyHangarViewStateAsync(id, call.Cancellation),
        };
        var service = new StructureRpcService("structureHangarViewMgr", callbacks);
        var session = new ClientSession();
        session.Set("structureid", new PyInt(structureId));

        var result = Assert.IsType<PyObjectEx>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetMyHangarViewState",
            Args = [],
            Session = session,
            Cancellation = CancellationToken.None,
        }));

        Assert.Equal(ObjectExKind.Ex2, result.Kind);
        Assert.Equal(["structures"], reads);
    }
}
