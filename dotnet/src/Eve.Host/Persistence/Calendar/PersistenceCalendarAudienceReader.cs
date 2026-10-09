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

public sealed class PersistenceCalendarAudienceReader(
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> characterIdsForCorporation,
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> corporationIdsForAlliance)
    : Eve.Calendar.ICalendarAudienceReader
{
    public async Task<IReadOnlyList<long>> GetMemberCharacterIdsAsync(
        short eventTag,
        long ownerId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId <= 0)
            return Array.Empty<long>();
        if (eventTag == Eve.Calendar.CalendarTags.Corporation)
        {
            var members = await characterIdsForCorporation(ownerId, cancellationToken);
            return members.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        }
        if (eventTag != Eve.Calendar.CalendarTags.Alliance)
            return Array.Empty<long>();

        var corporationIds = await corporationIdsForAlliance(ownerId, cancellationToken);
        var characters = new HashSet<long>();
        foreach (var corporationId in corporationIds.Where(id => id > 0).Distinct())
            foreach (var characterId in await characterIdsForCorporation(corporationId, cancellationToken))
                if (characterId > 0)
                    characters.Add(characterId);
        return characters.OrderBy(id => id).ToArray();
    }
}
