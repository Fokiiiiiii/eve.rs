using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>Skills, the character manager, and the online status of characters.</summary>
    private IReadOnlyList<IService> CharacterRpc()
    {
        var homeStations = new Eve.Services.CharacterInfo.HomeStationResolver(
            (characterId, token) => characters.GetAsync(characterId, token),
            stations,
            catalog,
            ReadHomeStationStructureAsync,
            ReadHomeStationOfficeLocationsAsync);
        return [
            new SkillMgr2Service(),
            skillHandler,
            new AlphaInjectorMgrService(skills, skillCatalog),
            new CharMgrService(
                characters,
                homeStations,
                charMgrReadModel.ReadRecentShipKillmailPageAsync,
                charMgrReadModel.ReadCharacterDescriptionAsync,
                charMgrReadModel.WriteCharacterDescriptionAsync,
                ReadCharacterSecurityStatusAsync,
                charMgrReadModel.ReadMedal1GraphicIdAsync,
                charMgrReadModel.ReadAsync,
                charMgrReadModel.UpdateAsync,
                charMgrReadModel.WriteActivityStatusAsync,
                charMgrReadModel.WriteLogSettingsAsync,
                async (characterId, token) => (await corporations.MemberAsync(characterId, token))?.StartedMs,
                async (memberCharacters, token) =>
                {
                    var corporationIds = memberCharacters
                        .Select(character => character.CorporationId)
                        .Where(id => id > 0)
                        .Distinct()
                        .ToArray();
                    var corporationsById = (await corporations.GetManyAsync(corporationIds, token))
                        .ToDictionary(corporation => corporation.Id);
                    var characterState = await ReadGameStoreTableAsync("characters", token);
                    return memberCharacters.ToDictionary(character => character.Id, character =>
                    {
                        var corporation = corporationsById.GetValueOrDefault(character.CorporationId);
                        var key = character.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var row = characterState[key] as JsonObject;
                        var warFactionId = CharMgrJsonLong(row?["warFactionID"] ?? row?["militiaFactionID"]);
                        return new Eve.Services.CharacterInfo.CharMgrOrganizationInfo(
                            character.CorporationId,
                            character.AllianceId ?? corporation?.AllianceId ?? 0,
                            Math.Max(0, warFactionId));
                    });
                },
                async (characterId, token) => await characters.PaperdollStateAsync(characterId, token)),
            new Eve.Services.CharacterInfo.HomeStationService(
                homeStations,
                characters.TryChangeHomeStationAsync,
                characters.ResetRemoteHomeStationChangeTimeAsync,
                () => DateTimeOffset.FromUnixTimeMilliseconds(now()).ToFileTime()),
            new Eve.Services.CharacterInfo.OnlineStatusService(characterSessions, ReadWatchlistAsync),
        ];
    }

    private async Task<Eve.Services.CharacterInfo.HomeStationStructure?> ReadHomeStationStructureAsync(
        long structureId,
        CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var structures = await ReadGameStoreTableAsync("structures", cancellationToken);
        if (structures[key] is not JsonObject row ||
            !PersistenceStructureDirectoryReadModel.IsPersistentlyDeployedStructure(row, structureId))
            return null;

        var rawTypeId = CharMgrJsonLong(row["typeID"]);
        var rawSystemId = CharMgrJsonLong(row["solarSystemID"]);
        var ownerId = CharMgrJsonLong(row["ownerID"] ?? row["ownerCorpID"] ?? row["corporationID"]);
        if (rawTypeId is <= 0 or > int.MaxValue || rawSystemId is <= 0 or > int.MaxValue || ownerId <= 0 ||
            solarSystems.Get((int)rawSystemId) is not { } system)
            return null;

        var name = Text(row["itemName"] ?? row["name"] ?? row["structureName"]);
        var location = new Eve.Services.CharacterInfo.HomeStationLocation(
            structureId, (int)rawTypeId, name, (int)rawSystemId, system.ConstellationId, system.RegionId, ownerId, false);
        return new Eve.Services.CharacterInfo.HomeStationStructure(location, true, false);

        static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private async Task<IReadOnlyList<Eve.Services.CharacterInfo.HomeStationLocation>> ReadHomeStationOfficeLocationsAsync(
        Character character,
        CancellationToken cancellationToken)
    {
        var offices = await corporationOfficeStore.ForCorporationAsync(character.CorporationId, cancellationToken);
        var locations = new List<Eve.Services.CharacterInfo.HomeStationLocation>();
        foreach (var stationId in offices.Select(office => office.StationId).Distinct())
        {
            if (stations.Get(stationId) is { } station)
                locations.Add(new(station.Id, station.TypeId, station.Name, station.SolarSystemId,
                    station.ConstellationId, station.RegionId, station.OwnerId, false));
            else if (await ReadHomeStationStructureAsync(stationId, cancellationToken) is
                     { PersistentlyDeployed: true, Location: { } location })
                locations.Add(location);
        }
        return locations;
    }
}
