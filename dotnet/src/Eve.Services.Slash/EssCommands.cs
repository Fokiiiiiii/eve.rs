using Eve.Dogma;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Slash;

/// <summary>GM QA helpers for the SDE-defined ESS hull list and the ordinary ESS main-bank link RPC.</summary>
public sealed class EssCommands(ShipTypeNames names, DogmaStatic statics, CharacterShips ships, Func<ClientSession, CancellationToken, ValueTask<PyValue>> attemptLink)
{
    private const int EssLinkableShipsTypeList = 231;

    public async Task<string> RunAsync(ServiceCall call, string argument)
    {
        var session = call.Session;
        var action = argument.Trim();
        if (action.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            await attemptLink(session, call.Cancellation);
            return "ESS main-bank link request sent. Check the ESS status notification for the result.";
        }

        if (!action.Equals("grant", StringComparison.OrdinalIgnoreCase))
            return "Usage: /ess grant|link";
        if (session.CharacterId is not { } characterId)
            return "Select a character before using /ess grant.";
        if (session.GetInt("stationid") is null)
            return "Dock before using /ess grant.";

        var hull = await names.FirstPublishedInTypeListAsync(statics, EssLinkableShipsTypeList, call.Cancellation);
        if (hull is null)
            return "The SDE ESSLinkableShips type list contains no published hull.";
        if (await ships.GrantAsync(characterId, hull.Value.TypeId, 1, singleton: true, call.Cancellation) is not { } changes)
            return "The ESS hull could not be granted to the station hangar.";
        foreach (var change in changes)
        {
            var type = statics.Type(change.After.TypeId);
            ShipNotifications.SendItemChange(session, change,
                item => new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity,
                    type?.GroupId ?? 0, type?.CategoryId ?? 0));
        }
        return $"{hull.Value.Name} ({hull.Value.TypeId}), the first published hull in SDE type list 231, was added to your station hangar. Board it and use the normal ESS link interaction in range.";
    }
}
