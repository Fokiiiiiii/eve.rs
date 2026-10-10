using Eve.Corporations;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>
    /// <c>ship.GetShipFittingInfo</c> for a saved fitting: its name and hull (EveJS <c>Handle_GetShipFittingInfo</c>: the name, the icon, the ship type and
    /// no insurance). A fitting that is not saved gets the reference's default, which the ship service gives.
    /// </summary>
    private async ValueTask<ShipFittingInfo?> ShipFittingInfoOf(long fittingId, CancellationToken cancellationToken) =>
        await savedFittings.FindAsync(fittingId, cancellationToken) is { } fitting
            ? new ShipFittingInfo(fitting.Draft.Name, "res:/UI/Texture/classes/Fitting/tabFittings.png", fitting.Draft.ShipTypeId, PyNone.Instance)
            : null;

    /// <summary>The saved fittings of characters, corporations and alliances (fitting persistence, track S1).</summary>
    private IReadOnlyList<IService> FittingRpc()
    {
        var calls = new FittingMgrCalls(savedFittings, new FittingHost(dogma, corporations, characterSessions), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return [new CharFittingMgrService(calls), new CorpFittingMgrService(calls), new AllianceFittingMgrService(calls)];
    }
}

/// <summary>The host's answers to the fitting calls: the static data, the corporation roles and the sessions that play an owner.</summary>
internal sealed class FittingHost(DogmaStatic dogma, CorporationStore corporations, CharacterSessions sessions) : IFittingHost
{
    /// <summary>The corporation role that lets a member change the shared fittings (EveJS <c>CORP_ROLE_FITTING_MANAGER</c>, fittingStore.js:50).</summary>
    private const long FittingManagerRole = 1L << 59;

    public async Task<IReadOnlyDictionary<int, int>> CategoriesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken)
    {
        await dogma.EnsureTypesAsync(typeIds, cancellationToken);
        var categories = new Dictionary<int, int>();
        foreach (var typeId in typeIds)
        {
            if (dogma.Type(typeId) is { } type)
            {
                categories[typeId] = type.CategoryId;
            }
        }
        return categories;
    }

    public async Task<bool> HasFittingManagerRoleAsync(long characterId, CancellationToken cancellationToken) =>
        await corporations.MemberAsync(characterId, cancellationToken) is { } member
        && (member.Masks.Roles & FittingManagerRole) == FittingManagerRole;

    public async Task<long?> AllianceExecutorAsync(long allianceId, CancellationToken cancellationToken) =>
        (await corporations.AllianceAsync(allianceId, cancellationToken))?.ExecutorCorporationId;

    public IReadOnlyList<ClientSession> SessionsOf(FittingOwnerScope scope, long ownerId) => scope switch
    {
        FittingOwnerScope.Character => sessions.Of(ownerId) is { } session ? [session] : [],
        FittingOwnerScope.Corporation => sessions.OfCorporation(ownerId),
        _ => [.. sessions.All().Where(session => session.GetInt("allianceid") == ownerId)],
    };
}
