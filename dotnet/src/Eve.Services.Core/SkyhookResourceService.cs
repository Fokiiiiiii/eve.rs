namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

/// <summary>
/// Implements the resource operations consumed by sovereigntyResourceSvc.py.
/// The host supplies SDE-backed resource definitions and durable sovereignty state; the read
/// authorization callback must apply the existing theft-vulnerability visibility rules.
/// The activation writer must revalidate owner and corporation role in the same transaction
/// that persists the status, so a role change cannot race the preflight check.
/// </summary>
public sealed class SkyhookResourceService
{
    private const long SkyhookTypeId = 81080;

    private readonly Func<long, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> _readSkyhook;
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyList<SkyhookResourceSnapshot>>> _readCorporationSkyhooks;
    private readonly Func<ServiceCall, SkyhookResourceSnapshot, SkyhookResourcePermission, CancellationToken, ValueTask<bool>> _authorize;
    private readonly Func<ServiceCall, SkyhookResourceSnapshot, bool, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> _persistActivation;


    public SkyhookResourceService(
        Func<long, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> readSkyhook,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<SkyhookResourceSnapshot>>> readCorporationSkyhooks,
        Func<ServiceCall, SkyhookResourceSnapshot, SkyhookResourcePermission, CancellationToken, ValueTask<bool>> authorize,
        Func<ServiceCall, SkyhookResourceSnapshot, bool, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> persistActivation)
    {
        _readSkyhook = readSkyhook ?? throw new ArgumentNullException(nameof(readSkyhook));
        _readCorporationSkyhooks = readCorporationSkyhooks ?? throw new ArgumentNullException(nameof(readCorporationSkyhooks));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _persistActivation = persistActivation ?? throw new ArgumentNullException(nameof(persistActivation));
    }

    public async ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> GetSkyhookAsync(
        ServiceCall call, long skyhookId)
    {
        var site = await ReadDeployedSkyhookAsync(skyhookId, call.Cancellation);
        if (site is null)
            return new(SkyhookResourceResultCode.NotFound, null);
        if (!HasCharacter(call) || !await _authorize(call, site, SkyhookResourcePermission.Read, call.Cancellation))
            return new(SkyhookResourceResultCode.Forbidden, null);
        return new(SkyhookResourceResultCode.Ok, site);
    }

    public async ValueTask<SkyhookResourceResult<IReadOnlyList<SkyhookResourceSnapshot>>> GetAllByCorporationAsync(
        ServiceCall call, long corporationId)
    {
        var sessionCorporationId = call.Session.GetInt("corporationID") ?? 0;
        if (sessionCorporationId == 0)
            sessionCorporationId = call.Session.GetInt("corpid") ?? 0;
        if (!HasCharacter(call) || corporationId <= 0 || sessionCorporationId != corporationId)
            return new(SkyhookResourceResultCode.Forbidden, null);

        var candidates = await _readCorporationSkyhooks(corporationId, call.Cancellation);
        var visible = new List<SkyhookResourceSnapshot>();
        foreach (var site in candidates)
        {
            if (!IsDeployedSkyhook(site) || site.CorporationId != corporationId)
                continue;
            if (await _authorize(call, site, SkyhookResourcePermission.Read, call.Cancellation))
                visible.Add(site);
        }

        visible.Sort(static (left, right) =>
        {
            var systemOrder = left.SolarSystemId.CompareTo(right.SolarSystemId);
            return systemOrder != 0 ? systemOrder : left.SkyhookId.CompareTo(right.SkyhookId);
        });
        return new(SkyhookResourceResultCode.Ok, visible);
    }

    public ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> ActivateAsync(
        ServiceCall call, long skyhookId) => SetProductionActiveAsync(call, skyhookId, true);

    public ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> DeactivateAsync(
        ServiceCall call, long skyhookId) => SetProductionActiveAsync(call, skyhookId, false);

    public async ValueTask<SkyhookResourceResult<SkyhookProductionStatus>> IsProductionActiveAsync(
        ServiceCall call, long skyhookId)
    {
        var result = await GetSkyhookAsync(call, skyhookId);
        return result.Code == SkyhookResourceResultCode.Ok
            ? new(SkyhookResourceResultCode.Ok, new(result.Value!.ProductionActive))
            : new(result.Code, null);
    }

    public async ValueTask<SkyhookResourceResult<SkyhookReagentAmounts>> GetReagentsInSkyhookNowAsync(
        ServiceCall call, long skyhookId)
    {
        var result = await GetSkyhookAsync(call, skyhookId);
        if (result.Code != SkyhookResourceResultCode.Ok)
            return new(result.Code, null);

        var reagent = result.Value!.Reagents.FirstOrDefault();
        if (!IsValid(reagent))
            return new(SkyhookResourceResultCode.Ok, null);

        return new(SkyhookResourceResultCode.Ok, new(reagent!.SecureStock, reagent.InsecureStock));
    }

    public async ValueTask<SkyhookResourceResult<SkyhookWorkforce>> GetCurrentWorkforceForSkyhookAsync(
        ServiceCall call, long skyhookId)
    {
        var result = await GetSkyhookAsync(call, skyhookId);
        return result.Code == SkyhookResourceResultCode.Ok
            ? new(SkyhookResourceResultCode.Ok, new(result.Value!.Workforce))
            : new(result.Code, null);
    }

    private async ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> SetProductionActiveAsync(
        ServiceCall call, long skyhookId, bool active)
    {
        var site = await ReadDeployedSkyhookAsync(skyhookId, call.Cancellation);
        if (site is null)
            return new(SkyhookResourceResultCode.NotFound, null);
        if (!HasCharacter(call) || call.Session.GetInt("corpid") != site.CorporationId ||
            !await _authorize(call, site,
                active ? SkyhookResourcePermission.Activate : SkyhookResourcePermission.Deactivate,
                call.Cancellation))
            return new(SkyhookResourceResultCode.Forbidden, null);
        if (site.ProductionActive == active)
            return new(SkyhookResourceResultCode.Ok, site);

        var updated = await _persistActivation(call, site, active, call.Cancellation);
        if (updated is null || updated.SkyhookId != site.SkyhookId ||
            updated.CorporationId != site.CorporationId || updated.ProductionActive != active ||
            !IsDeployedSkyhook(updated))
            return new(SkyhookResourceResultCode.PersistenceFailure, null);
        return new(SkyhookResourceResultCode.Ok, updated);
    }

    private async ValueTask<SkyhookResourceSnapshot?> ReadDeployedSkyhookAsync(
        long skyhookId, CancellationToken cancellationToken)
    {
        if (skyhookId <= 0)
            return null;
        var site = await _readSkyhook(skyhookId, cancellationToken);
        return site is not null && site.SkyhookId == skyhookId && IsDeployedSkyhook(site) ? site : null;
    }

    private static bool HasCharacter(ServiceCall call) => call.Session.CharacterId is > 0;

    private static bool IsDeployedSkyhook(SkyhookResourceSnapshot site) =>
        site.TypeId == SkyhookTypeId && site.SkyhookId > 0 && site.SolarSystemId > 0 &&
        site.PlanetId > 0 && site.CorporationId > 0 && site.Reagents is not null;

    private static bool IsValid(SkyhookReagentResource? reagent) =>
        reagent is not null && reagent.TypeId > 0 &&
        reagent.SecureStock >= 0 && reagent.InsecureStock >= 0;

}
