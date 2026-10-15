using Eve.Sde;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The fixed bodies a Mobile Observatory keeps 1 000 km from (EVE.js <c>collectPlacementBlockers</c>, kind stationStargateUpwell): the system's stations (the static
/// data), its stargates (the static data's map, with their positions) and its Upwell structures that are not destroyed (the structure store).
/// </summary>
internal sealed class ObservatoryLandmarkSource(
    NpgsqlDataSource source,
    StructureStore structures,
    Eve.Universe.StationCatalog stations,
    IReadOnlyDictionary<int, IReadOnlyList<double[]>> stargates)
{
    public async Task<IReadOnlyList<double[]>> ReadAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var bodies = new List<double[]>();
        foreach (var station in stations.InSystem(solarSystemId))
            bodies.Add([station.X, station.Y, station.Z]);
        if (stargates.TryGetValue(solarSystemId, out var gates))
            bodies.AddRange(gates);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        foreach (var record in await structures.ListInSystemAsync(connection, null, solarSystemId, cancellationToken))
        {
            if (record.Snapshot.DestroyedMs == 0)
                bodies.Add([record.X, record.Y, record.Z]);
        }
        return bodies;
    }

    /// <summary>The stargates of the static data by solar system, with their positions in metres.</summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<double[]>>> LoadStargatesAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var lists = new Dictionary<int, List<double[]>>();
        foreach (var row in await sde.ReadAsync("mapStargates", cancellationToken: cancellationToken))
        {
            var system = row.Doc.GetProperty("solarSystemID").GetInt32();
            var position = row.Doc.GetProperty("position");
            if (!lists.TryGetValue(system, out var list))
                lists[system] = list = [];
            list.Add([position.GetProperty("x").GetDouble(), position.GetProperty("y").GetDouble(), position.GetProperty("z").GetDouble()]);
        }
        return lists.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<double[]>)pair.Value);
    }
}
