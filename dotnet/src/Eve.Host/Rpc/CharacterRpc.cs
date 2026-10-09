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
    private IReadOnlyList<IService> CharacterRpc() =>
    [
            new SkillMgr2Service(),
            skillHandler,
            new CharMgrService(
                characters,
                stations,
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
                }),
            new Eve.Services.CharacterInfo.HomeStationService(characters, stations),
            new Eve.Services.CharacterInfo.OnlineStatusService(characterSessions, ReadWatchlistAsync),
    ];
}
