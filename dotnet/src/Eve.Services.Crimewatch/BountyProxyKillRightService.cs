using Eve.Crimewatch;
using Eve.Marshal;
using System.Text.Json.Nodes;

namespace Eve.Services.Crimewatch;

public static class KillRightNotificationTypes
{
    public const int MiscGroup = 4;
    public const int Available = 115;
    public const int AvailableOpen = 116;
    public const int Used = 118;
    public const int Unavailable = 119;
    public const int UnavailableOpen = 120;
}

public sealed class BountyProxyKillRightService(
    Func<long, CancellationToken, Task<IReadOnlyList<KillRight>>>? readKillRights = null,
    PgCrimewatchStore? store = null,
    Func<long, long, int, JsonObject, string?, CancellationToken, Task>? notificationWriter = null,
    Func<long>? nowUnixMs = null,
    Func<ServiceCall, ValueTask<PyValue?>>? bountyRpcHandler = null,
    Func<long, CancellationToken, Task<(long CorporationId, long AllianceId)>>? resolveOrganization = null) : IService
{
    public string Name => "bountyProxy";

    public bool HasMethod(string method) => method is
        "AddToBounty" or "GetBounties" or "GetBountiesAndKillRights" or "GetMyBounties" or
        "GetMyKillRights" or "GetKillRightsOnCharacters" or "SellKillRight" or "CancelSellKillRight" or
        "GetTopPilotBounties" or "GetTopCorpBounties" or "GetTopAllianceBounties" or
        "GetTopPilotBountyHunters" or "GetTopCorporationBountyHunters" or "GetTopAllianceBountyHunters" or
        "SearchCharBounties" or "SearchCorpBounties" or "SearchAllianceBounties" or
        "GMReimburseBounties" or "GMClearBountyCache";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "AddToBounty" => AddToBountyAsync(call),
        "GetBounties" => BountyResponseAsync(call, BountyRows(call.Arg(0), call.Session)),
        "GetBountiesAndKillRights" => GetBountiesAndKillRightsAsync(call),
        "GetMyBounties" => BountyResponseAsync(call, new PyList([])),
        "GetMyKillRights" => GetMyKillRightsAsync(call),
        "GetKillRightsOnCharacters" => ReadAvailableKillRightsAsync(call, call.Arg(0)),
        "SellKillRight" => SellKillRightAsync(call),
        "CancelSellKillRight" => CancelSellKillRightAsync(call),
        "GetTopPilotBounties" or "GetTopCorpBounties" or "GetTopAllianceBounties" or
        "GetTopPilotBountyHunters" or "GetTopCorporationBountyHunters" or "GetTopAllianceBountyHunters" =>
            BountyResponseAsync(call, EmptyRankedResult()),
        "SearchCharBounties" or "SearchCorpBounties" or "SearchAllianceBounties" =>
            BountyResponseAsync(call, new PyList([])),
        "GMReimburseBounties" or "GMClearBountyCache" =>
            BountyResponseAsync(call, PyNone.Instance),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };


    private async ValueTask<PyValue> BountyResponseAsync(ServiceCall call, PyValue fallback)
    {
        if (bountyRpcHandler is null)
            return fallback;
        return await bountyRpcHandler(call).ConfigureAwait(false) ?? fallback;
    }

    private async ValueTask<PyValue> AddToBountyAsync(ServiceCall call)
    {
        if (bountyRpcHandler is null ||
            await bountyRpcHandler(call).ConfigureAwait(false) is not { } result)
        {
            throw Eve.Services.MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("Bounty service is not wired on this server.")));
        }
        return result;
    }

    private async ValueTask<PyValue> GetBountiesAndKillRightsAsync(ServiceCall call)
    {
        var bountyCall = new ServiceCall
        {
            Method = "GetBounties",
            Args = [call.Arg(0)],
            Session = call.Session,
            CallId = call.CallId,
            Cancellation = call.Cancellation,
        };
        var bountyRows = await BountyResponseAsync(bountyCall, BountyRows(call.Arg(0), call.Session)).ConfigureAwait(false);
        var killRights = await ReadAvailableKillRightsAsync(call, call.Arg(1)).ConfigureAwait(false);
        return Py.Tuple(bountyRows, killRights);
    }

    private static PyValue BountyRows(PyValue requested, ClientSession session)
    {
        var ids = Ids(requested);
        if (ids.Length == 0)
        {
            ids =
            [
                0,
                session.CharacterId ?? 0,
                session.GetInt("corpid") ?? 0,
                session.GetInt("allianceid") ?? 0,
            ];
            ids = ids.Distinct().OrderBy(id => id).ToArray();
        }

        return new PyList([.. ids.Select(targetId => (PyValue)new PyTuple(
        [
            InventoryWire.Id(targetId),
            Py.KeyVal(
                ("targetID", InventoryWire.Id(targetId)),
                ("bounty", new PyInt(0))),
        ]))]);
    }

    private PyValue EmptyRankedResult() =>
        Py.Tuple(new PyList([]), Py.FileTimeLong(Py.FileTimeFromUnixMs(NowUnixMs)));

    private async ValueTask<PyValue> GetKillRightsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var ownerCharacterId = call.Session.CharacterId ?? 0;
        if (ownerCharacterId <= 0 || (store is null && readKillRights is null))
            return new PyList([]);
        var requestedTargets = Ids(call.Arg(0)).ToHashSet();
        if (requestedTargets.Count == 0)
            return new PyList([]);

        var rights = store is not null
            ? await store.KillRightsOfAsync(ownerCharacterId, call.Cancellation)
            : await readKillRights!(ownerCharacterId, call.Cancellation);
        return new PyList([.. rights
            .Where(right => requestedTargets.Contains(right.TargetCharacterId))
            .OrderBy(right => right.TargetCharacterId)
            .ThenBy(right => right.KillRightId)
            .Select(LegacyGetKillRightsPayload)]);
    }

    private async ValueTask<PyValue> GetMyKillRightsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = call.Session.CharacterId ?? 0;
        if (characterId <= 0)
            return new PyList([]);

        var now = NowUnixMs;
        var rights = store is not null
            ? await store.KillRightsForCharacterAsync(characterId, now, call.Cancellation)
            : readKillRights is not null
                ? await readKillRights(characterId, call.Cancellation)
                : [];
        return new PyList([.. rights
            .Where(right => right.ExpiresAtUnixMs > now)
            .Select(Payload)]);
    }


    private async ValueTask<PyValue> ReadAvailableKillRightsAsync(ServiceCall call, PyValue targetsValue)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = call.Session.CharacterId ?? 0;
        var targetIds = Ids(targetsValue);
        if (characterId <= 0 || targetIds.Length == 0 || store is null)
            return new PyList([]);

        var organization = resolveOrganization is null
            ? (CorporationId: 0L, AllianceId: 0L)
            : await resolveOrganization(characterId, call.Cancellation);
        var ownerIds = new[] { characterId, organization.CorporationId, organization.AllianceId };
        var rights = await store.AvailableKillRightsOnCharactersAsync(
            targetIds, ownerIds, NowUnixMs, call.Cancellation);
        return new PyList([.. rights.Select(Payload)]);
    }

    private async ValueTask<PyValue> SellKillRightAsync(ServiceCall call)
    {
        var killRightId = Positive(call.Arg(0));
        if (store is null || call.Session.CharacterId is not { } ownerCharacterId
            || ownerCharacterId <= 0 || killRightId <= 0)
            throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");

        var price = call.Arg(1) is PyNone ? 0m : Money(call.Arg(1));
        var restrictedTo = call.Arg(2) is PyNone ? null : Number(call.Arg(2));
        if (price is null || call.Arg(2) is not PyNone && restrictedTo is null)
            throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");
        if (restrictedTo is <= 0)
            restrictedTo = null;

        var right = (await store.KillRightsOfAsync(ownerCharacterId, call.Cancellation))
            .FirstOrDefault(candidate => candidate.KillRightId == killRightId);
        var now = NowUnixMs;
        if (right is null)
            throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");
        if (right.ExpiresAtUnixMs <= now)
            throw Eve.Services.MachoErrors.ThrowUserError("KillRightExpired");
        if (!await store.SetKillRightSaleAsync(
                ownerCharacterId, killRightId, price, restrictedTo, now, call.Cancellation))
            throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");

        var listed = right with { SalePrice = price, RestrictedToOwnerId = restrictedTo };
        if (notificationWriter is { } notify)
        {
            var data = new JsonObject
            {
                ["charID"] = ownerCharacterId,
                ["price"] = price.Value,
            };
            if (restrictedTo is { } entityId)
                data["toEntityID"] = entityId;
            await notify(
                right.TargetCharacterId, ownerCharacterId,
                restrictedTo is null ? KillRightNotificationTypes.AvailableOpen : KillRightNotificationTypes.Available,
                data, $"kill-right-sale:{killRightId}:{now}", call.Cancellation);
        }
        return new PyList([Payload(listed)]);
    }

    private async ValueTask<PyValue> CancelSellKillRightAsync(ServiceCall call)
    {
        var killRightId = Positive(call.Arg(0));
        if (store is null || call.Session.CharacterId is not { } ownerCharacterId
            || ownerCharacterId <= 0 || killRightId <= 0)
            return PyNone.Instance;

        var toId = Number(call.Arg(1)) ?? 0;
        var right = (await store.KillRightsOfAsync(ownerCharacterId, call.Cancellation))
            .FirstOrDefault(candidate => candidate.KillRightId == killRightId);
        var now = NowUnixMs;
        if (right is null || right.ExpiresAtUnixMs <= now
            || toId > 0 && right.TargetCharacterId != toId)
            return PyNone.Instance;
        var wasForSale = right.SalePrice is not null;
        if (!await store.SetKillRightSaleAsync(ownerCharacterId, killRightId, null, null, now, call.Cancellation))
            return PyNone.Instance;
        if (wasForSale && notificationWriter is { } notify)
        {
            var data = new JsonObject { ["charID"] = ownerCharacterId };
            if (right.RestrictedToOwnerId is { } entityId)
                data["toEntityID"] = entityId;
            await notify(
                right.TargetCharacterId, ownerCharacterId,
                right.RestrictedToOwnerId is null
                    ? KillRightNotificationTypes.UnavailableOpen
                    : KillRightNotificationTypes.Unavailable,
                data, $"kill-right-cancel:{killRightId}:{now}", call.Cancellation);
        }
        return PyNone.Instance;
    }

    private long NowUnixMs => nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static PyValue Payload(KillRight right) => Py.KeyVal(
        ("killRightID", new PyLong(right.KillRightId)),
        ("fromID", InventoryWire.Id(right.OwnerCharacterId)),
        ("toID", InventoryWire.Id(right.TargetCharacterId)),
        ("expiryTime", new PyLong(Py.FileTimeFromUnixMs(right.ExpiresAtUnixMs))),
        ("price", right.SalePrice is { } price ? new PyReal((double)price) : PyNone.Instance),
        ("restrictedTo", right.RestrictedToOwnerId is { } restrictedTo
            ? InventoryWire.Id(restrictedTo)
            : PyNone.Instance));

    private static PyValue LegacyGetKillRightsPayload(KillRight right) => Py.KeyVal(
        ("killRightID", new PyLong(right.KillRightId)),
        ("fromID", InventoryWire.Id(right.OwnerCharacterId)),
        ("toID", InventoryWire.Id(right.TargetCharacterId)),
        ("restrictedTo", right.SalePrice is null
            ? InventoryWire.Id(right.OwnerCharacterId)
            : right.RestrictedToOwnerId is { } restrictedTo
                ? InventoryWire.Id(restrictedTo)
                : PyNone.Instance),
        ("price", right.SalePrice is { } price ? new PyReal((double)price) : PyNone.Instance),
        ("solarSystemID", InventoryWire.Id(right.SolarSystemId)));

    private static long[] Ids(PyValue value) => (value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => [],
    }).Select(Positive).Where(id => id > 0).Distinct().ToArray();

    private static long Positive(PyValue value) => Number(value) is { } number ? Math.Max(0, number) : 0;

    private static long? Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value == Math.Truncate(real.Value) => (long)real.Value,
        _ => null,
    };

    private static decimal? Money(PyValue value)
    {
        if (value is PyInt integer && integer.Value >= 0)
            return integer.Value;
        if (value is PyLong largeInteger && largeInteger.Value >= 0)
            return largeInteger.Value;
        if (value is PyReal real && double.IsFinite(real.Value)
            && decimal.TryParse(real.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            && amount >= 0 && decimal.Round(amount, 2) == amount)
            return amount;
        return null;
    }
}
