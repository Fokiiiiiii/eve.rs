using Eve.Crimewatch;
using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Crimewatch;

/// <summary>What the host knows of a bounty target: its kind, and its corporation and alliance (0 when none).</summary>
public sealed record PlayerBountyOwner(BountyOwnerKind Kind, long CorporationId = 0, long AllianceId = 0);

/// <summary>A bounty placement that is recorded: who placed it, on what, how much, and the operation key that makes its retries one placement.</summary>
public sealed record BountyPlacement(long PlacerId, long TargetId, BountyOwnerKind Kind, long CorporationId, long AllianceId, decimal Amount, string Key);

/// <summary>
/// placer's wallet once, in the same transaction that records it; the reads answer the stored pools, contributions and rankings. Methods this
/// class does not answer are left to <see cref="BountyProxyKillRightService"/>.
/// </summary>
public sealed class PlayerBountyRpc(
    PlayerBountyStore store,
    NpgsqlDataSource source,
    WalletTransactions wallets,
    PgWalletStore walletStore,
    Func<long, CancellationToken, ValueTask<PlayerBountyOwner>> resolveOwner,
    Func<long> nowUnixMs,
    Func<BountyPlacement, CancellationToken, ValueTask>? notifyPlaced = null)
{

    private const int BountyJournalEntryType = 16;
    private const int RankedRows = 10;

    public static decimal MinimumFor(BountyOwnerKind kind) => kind switch
    {
        BountyOwnerKind.Character => 1_000_000m,
        BountyOwnerKind.Corporation => 20_000_000m,
        BountyOwnerKind.Alliance => 100_000_000m,
        _ => 0m,
    };

    /// <summary>Answers the player bounty method of the call, or null when the method is not one of them.</summary>
    public async ValueTask<PyValue?> HandleAsync(ServiceCall call) => call.Method switch
    {
        "AddToBounty" => await AddToBountyAsync(call),
        "GetBounties" => await GetBountiesAsync(call),
        "GetMyBounties" => await GetMyBountiesAsync(call),
        "GetTopPilotBounties" => await TopBountiesAsync(BountyOwnerKind.Character, call),
        "GetTopCorpBounties" => await TopBountiesAsync(BountyOwnerKind.Corporation, call),
        "GetTopAllianceBounties" => await TopBountiesAsync(BountyOwnerKind.Alliance, call),
        "GetTopPilotBountyHunters" => await TopHuntersAsync(BountyOwnerKind.Character, call),
        "GetTopCorporationBountyHunters" => await TopHuntersAsync(BountyOwnerKind.Corporation, call),
        "GetTopAllianceBountyHunters" => await TopHuntersAsync(BountyOwnerKind.Alliance, call),
        "SearchCharBounties" => await SearchAsync(BountyOwnerKind.Character, call),
        "SearchCorpBounties" => await SearchAsync(BountyOwnerKind.Corporation, call),
        "SearchAllianceBounties" => await SearchAsync(BountyOwnerKind.Alliance, call),
        _ => null,
    };

    private async ValueTask<PyValue> AddToBountyAsync(ServiceCall call)
    {
        var targetId = Id(call.Arg(0));
        var amount = Money(call.Arg(1));
        var placer = call.Session.CharacterId ?? 0;
        if (placer <= 0)
            throw Eve.Services.MachoErrors.ThrowUserError("CharacterNotFound");
        var owner = await resolveOwner(targetId, call.Cancellation);
        if (owner.Kind == BountyOwnerKind.Unknown)
            throw Custom("Bounty target is not a known character, corporation, or alliance.");
        var minimum = MinimumFor(owner.Kind);
        if (!(amount > 0) || amount < minimum)
            throw Custom($"Bounty must be at least {minimum:0} ISK for this target.");

        var key = OperationKey(placer, call.CallId);
        await using var connection = await source.OpenConnectionAsync(call.Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        var placement = new BountyPlacement(placer, targetId, owner.Kind, owner.CorporationId, owner.AllianceId, amount, key);
        // a retried placement answers the pool it already changed, and debits nothing again (its notifications are keyed, so they are sent once)
        if (await store.PlacementPoolAsync(connection, transaction, key, call.Cancellation) is { } placed)
        {
            await transaction.CommitAsync(call.Cancellation);
            await NotifyPlacedAsync(placement, call.Cancellation);
            return Payload(placed);
        }
        var now = nowUnixMs();
        try
        {
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(placer), -amount, 0, BountyJournalEntryType,
                $"Bounty placed on {targetId}", now, key, $"bounty:{targetId}", call.Cancellation, referenceId: targetId);
        }
        catch (InsufficientFundsException)
        {
            var balance = (await walletStore.GetAsync(placer, call.Cancellation))?.Isk ?? 0m;
            throw Eve.Services.MachoErrors.ThrowUserError("NotEnoughMoney",
                ("amount", new PyReal((double)amount)), ("balance", new PyReal((double)balance)));
        }
        var pool = await store.RecordPlacementAsync(connection, transaction,
            new BountyPool(targetId, owner.Kind, owner.CorporationId, owner.AllianceId, 0), placer, amount, key, now, call.Cancellation);
        await transaction.CommitAsync(call.Cancellation);
        await NotifyPlacedAsync(placement, call.Cancellation);
        return Payload(pool);
    }

    private async ValueTask NotifyPlacedAsync(BountyPlacement placement, CancellationToken cancellationToken)
    {
        if (notifyPlaced is not null)
            await notifyPlaced(placement, cancellationToken);
    }

    private async ValueTask<PyValue> GetBountiesAsync(ServiceCall call)
    {
        var ids = RequestedIds(call);
        var pools = (await store.PoolsOfAsync(ids, call.Cancellation)).ToDictionary(pool => pool.TargetId);
        return new PyList([.. ids.Select(id => (PyValue)new PyTuple(
        [
            InventoryWire.Id(id),
            Payload(pools.GetValueOrDefault(id) ?? new BountyPool(id, BountyOwnerKind.Unknown, 0, 0, 0)),
        ]))]);
    }

    private async ValueTask<PyValue> GetMyBountiesAsync(ServiceCall call)
    {
        var contributions = await store.ContributionsOfAsync(call.Session.CharacterId ?? 0, call.Cancellation);
        return new PyList([.. contributions.Select(contribution => (PyValue)Py.KeyVal(
            ("contributionID", new PyLong(contribution.ContributionId)),
            ("targetID", InventoryWire.Id(contribution.Pool.TargetId)),
            ("amount", new PyReal((double)contribution.Amount)),
            ("corporationID", InventoryWire.Id(contribution.Pool.CorporationId)),
            ("allianceID", InventoryWire.Id(contribution.Pool.AllianceId))))]);
    }

    private async ValueTask<PyValue> TopBountiesAsync(BountyOwnerKind kind, ServiceCall call)
    {
        var pools = await store.TopPoolsAsync(kind, RankedRows, call.Cancellation);
        return Ranked(new PyList([.. pools.Select(pool => (PyValue)Payload(pool))]));
    }

    private async ValueTask<PyValue> TopHuntersAsync(BountyOwnerKind kind, ServiceCall call)
    {
        var hunters = await store.TopHuntersAsync(kind, RankedRows, call.Cancellation);
        var rows = hunters.Select((hunter, index) => (PyValue)Py.KeyVal(
            ("bountyHunterID", InventoryWire.Id(hunter.HunterId)),
            ("corporationID", InventoryWire.Id(hunter.CorporationId)),
            ("allianceID", InventoryWire.Id(hunter.AllianceId)),
            ("bountiesClaimed", new PyReal((double)hunter.BountiesClaimed)),
            ("numberOfKills", new PyInt(hunter.NumberOfKills)),
            ("rowNumber", new PyInt(index + 1))));
        return Ranked(new PyList([.. rows]));
    }

    /// <summary>The target's place among the pools of its kind, with its pool: an empty list when the target has no bounty of that kind.</summary>
    private async ValueTask<PyValue> SearchAsync(BountyOwnerKind kind, ServiceCall call)
    {
        var targetId = Id(call.Arg(0));
        var rank = await store.RankOfAsync(kind, targetId, call.Cancellation);
        if (rank is not { } position)
            return new PyList([]);
        var pool = (await store.PoolsOfAsync([targetId], call.Cancellation)).Single();
        return new PyList([(PyValue)new PyTuple([new PyInt(position), Payload(pool)])]);
    }

    private PyValue Ranked(PyValue rows) => Py.Tuple(rows, Py.FileTimeLong(Py.FileTimeFromUnixMs(nowUnixMs())));

    private static PyValue Payload(BountyPool pool)
    {
        var entries = new List<(string, PyValue)>
        {
            ("targetID", InventoryWire.Id(pool.TargetId)),
            ("bounty", new PyReal((double)pool.Bounty)),
        };
        if (pool.CorporationId != 0)
            entries.Add(("corporationID", InventoryWire.Id(pool.CorporationId)));
        if (pool.AllianceId != 0)
            entries.Add(("allianceID", InventoryWire.Id(pool.AllianceId)));
        return Py.KeyVal(entries.ToArray());
    }

    private static IReadOnlyList<long> RequestedIds(ServiceCall call)
    {
        var arg = call.Arg(0);
        IEnumerable<PyValue> items = arg switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [arg],
        };
        var requested = items.Select(Id).Where(id => id > 0).Distinct().ToArray();
        if (requested.Length > 0)
            return requested;
        long[] owners = [call.Session.CharacterId ?? 0, call.Session.GetInt("corpid") ?? 0, call.Session.GetInt("allianceid") ?? 0];
        return owners.Where(id => id > 0).Distinct().ToArray();
    }

    /// <summary>A placement retried by the same call keeps its key; a call without a call id gets a key of its own.</summary>
    private static string OperationKey(long placer, PyValue callId) =>
        Id(callId) is > 0 and var id ? $"bounty-place:{placer}:{id}" : $"bounty-place:{placer}:{Guid.NewGuid():N}";

    private static long Id(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) => (long)real.Value,
        _ => 0,
    };

    private static decimal Money(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) => (decimal)real.Value,
        _ => 0m,
    };

    private static Exception Custom(string message) =>
        Eve.Services.MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
