namespace Eve.Host;

public sealed class PersistenceLookupDirectory(
    Eve.Characters.PgCharacterStore characters,
    Eve.Corporations.CorporationStore corporations,
    Eve.Sde.SdeReader sde,
    Eve.Universe.StationCatalog stations) : Eve.Services.Core.ILookupDirectory
{
    private readonly Lazy<Task<StaticData>> _staticData = new(() => LoadStaticDataAsync(sde, stations, CancellationToken.None));

    public async ValueTask<IReadOnlyList<Eve.Services.Core.LookupCharacter>> CharactersAsync(CancellationToken cancellationToken)
    {
        var rows = await characters.AllAsync(cancellationToken);
        return rows.Select(character => new Eve.Services.Core.LookupCharacter(
            character.Id,
            character.Name,
            Gender: character.Gender,
            CorporationId: character.CorporationId,
            AllianceId: character.AllianceId ?? 0)).ToArray();
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken)
    {
        var data = await _staticData.Value.WaitAsync(cancellationToken);
        var rows = await corporations.AllAsync(cancellationToken);
        return rows.Select(corporation => new Eve.Services.Core.LookupCorporation(
            corporation.Id,
            corporation.Name,
            corporation.Ticker,
            data.NpcFactionIds.GetValueOrDefault(corporation.Id),
            corporation.IsNpc,
            WarPermit: corporation.IsNpc ? 0 : null)).ToArray();
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken)
    {
        var rows = await corporations.AllAlliancesAsync(cancellationToken);
        return rows.Select(alliance => new Eve.Services.Core.LookupAlliance(
            alliance.Id,
            alliance.Name,
            alliance.ShortName,
            IsNpc: false,
            ExecutorCorporationId: alliance.ActiveExecutorId ?? 0)).ToArray();
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.LookupFaction>> FactionsAsync(CancellationToken cancellationToken)
    {
        var data = await _staticData.Value.WaitAsync(cancellationToken);
        return data.Factions;
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.LookupLocation>> LocationsByGroupAsync(
        int groupId,
        CancellationToken cancellationToken)
    {
        var data = await _staticData.Value.WaitAsync(cancellationToken);
        return data.Locations.GetValueOrDefault(groupId) ?? Array.Empty<Eve.Services.Core.LookupLocation>();
    }

    private static async Task<StaticData> LoadStaticDataAsync(
        Eve.Sde.SdeReader sde,
        Eve.Universe.StationCatalog stations,
        CancellationToken cancellationToken)
    {
        var factions = (await sde.ReadAsync("factions", cancellationToken: cancellationToken))
            .Select(row => new Eve.Services.Core.LookupFaction(
                row.Key,
                Eve.Sde.SdeReader.English(row.Doc.GetProperty("name"))))
            .Where(faction => faction.Id > 0 && faction.Name.Length > 0)
            .ToArray();
        var npcFactionIds = (await sde.ReadAsync("npcCorporations", cancellationToken: cancellationToken))
            .Where(row => row.Doc.TryGetProperty("factionID", out var faction) && faction.ValueKind == System.Text.Json.JsonValueKind.Number)
            .ToDictionary(row => row.Key, row => row.Doc.GetProperty("factionID").GetInt64());
        var typeGroups = (await sde.ReadTypeSummariesAsync(cancellationToken))
            .ToDictionary(type => type.TypeId, type => type.GroupId);
        var systemRows = await sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken);
        var systems = systemRows.ToDictionary(
            row => checked((int)row.Key),
            row => new
            {
                Id = row.Key,
                Name = Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")),
                ConstellationId = Integer(row.Doc, "constellationID"),
                RegionId = Integer(row.Doc, "regionID"),
            });

        var grouped = new Dictionary<int, Dictionary<long, Eve.Services.Core.LookupLocation>>();
        void Add(Eve.Services.Core.LookupLocation location)
        {
            if (location.Id <= 0 || string.IsNullOrWhiteSpace(location.Name))
                return;
            if (!grouped.TryGetValue(location.GroupId, out var rows))
            {
                rows = new Dictionary<long, Eve.Services.Core.LookupLocation>();
                grouped.Add(location.GroupId, rows);
            }
            rows.TryAdd(location.Id, location);
        }

        foreach (var system in systems.Values)
            Add(new(system.Id, system.Name, 5, 5, system.Id, system.ConstellationId, system.RegionId));
        foreach (var station in stations.All)
            Add(new(station.Id, station.Name, station.TypeId, 15, station.SolarSystemId, station.ConstellationId, station.RegionId));
        foreach (var row in await sde.ReadAsync("mapRegions", cancellationToken: cancellationToken))
            Add(new(row.Key, Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")), 3, 3, RegionId: row.Key));
        foreach (var row in await sde.ReadAsync("mapConstellations", cancellationToken: cancellationToken))
            Add(new(row.Key, Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")), 4, 4,
                ConstellationId: row.Key, RegionId: Long(row.Doc, "regionID")));

        foreach (var table in new[] { "mapAsteroidBelts", "mapMoons", "mapPlanets", "mapStars", "mapStargates", "mapSecondarySuns" })
        {
            foreach (var row in await sde.ReadAsync(table, cancellationToken: cancellationToken))
            {
                var typeId = Integer(row.Doc, "typeID");
                if (typeId <= 0 || !typeGroups.TryGetValue(typeId, out var groupId) || groupId <= 0 || groupId is 3 or 4 or 5 or 15)
                    continue;

                var solarSystemId = Long(row.Doc, "solarSystemID");
                systems.TryGetValue(checked((int)Math.Clamp(solarSystemId, 0, int.MaxValue)), out var system);
                var name = Text(row.Doc, "uniqueName");
                if (name.Length == 0)
                    name = table switch
                    {
                        "mapAsteroidBelts" => $"{system?.Name ?? "Unknown System"} - Asteroid Belt {Integer(row.Doc, "orbitIndex")}",
                        "mapStars" => $"{system?.Name ?? "Unknown System"} - Sun",
                        "mapStargates" => $"{system?.Name ?? "Unknown System"} Stargate {row.Key}",
                        "mapSecondarySuns" => $"{system?.Name ?? "Unknown System"} - Secondary Sun",
                        _ => "",
                    };
                if (name.Length == 0)
                    continue;

                Add(new(
                    row.Key,
                    name,
                    typeId,
                    groupId,
                    solarSystemId > 0 ? solarSystemId : null,
                    system?.ConstellationId > 0 ? system.ConstellationId : null,
                    system?.RegionId > 0 ? system.RegionId : null));
            }
        }

        return new StaticData(
            factions,
            npcFactionIds,
            grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Eve.Services.Core.LookupLocation>)pair.Value.Values.ToArray()));
    }

    private static int Integer(System.Text.Json.JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static long Long(System.Text.Json.JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static string Text(System.Text.Json.JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) ? Eve.Sde.SdeReader.English(value) : "";

    private sealed record StaticData(
        IReadOnlyList<Eve.Services.Core.LookupFaction> Factions,
        IReadOnlyDictionary<long, long> NpcFactionIds,
        IReadOnlyDictionary<int, IReadOnlyList<Eve.Services.Core.LookupLocation>> Locations);
}
