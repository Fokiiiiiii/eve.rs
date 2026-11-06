using System.Globalization;
using Eve.Marshal;
using Eve.ReadSide;
using Eve.Ships;

namespace Eve.Services.Core;

/// <summary>
/// The read-only EVE.js characterMiningLedger RPC over the mining ledger table, read through the rowset read side (<see cref="MiningLedgerQueries"/>): a list of
/// the pilot's visible entries, newest first, as the reference returns it.
/// </summary>
public sealed class CharacterMiningLedgerService(Eve.ReadSide.ReadSide reads, Func<long>? nowFileTime = null) : IService
{
    private readonly Func<long> _nowFileTime = nowFileTime ?? (() => DateTime.UtcNow.ToFileTimeUtc());

    public string Name => "characterMiningLedger";

    public bool HasMethod(string method) => method == "GetCharacterLogs";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = call.Session.GetInt("characterID") ?? 0;
        if (characterId <= 0)
            characterId = call.Session.GetInt("charid") ?? 0;
        if (characterId <= 0)
            return new PyList([]);

        var now = _nowFileTime();
        var rows = await reads.RowsAsync(MiningLedgerQueries.Character, [characterId, now, now - MiningLedgerStore.RetentionFileTimeTicks], call.Cancellation);
        return rows.ToKeyValList();
    }
}

/// <summary>
/// The read-only EVE.js corpMiningLedger RPCs over the mining ledger table: the observers of the corporation that have a ledger, and an observer's entries.
/// Only the corporation's ledger roles see them, as the reference checks.
/// </summary>
public sealed class CorpMiningLedgerService(
    Eve.ReadSide.ReadSide reads,
    Func<long, long, CancellationToken, ValueTask<bool>> canReadLedger,
    Func<long>? nowFileTime = null) : IService
{
    private readonly Func<long> _nowFileTime = nowFileTime ?? (() => DateTime.UtcNow.ToFileTimeUtc());

    public string Name => "corpMiningLedger";

    public bool HasMethod(string method) => method is "GetObserversWithMiningEvents" or "GetObserverLedger";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        call.Cancellation.ThrowIfCancellationRequested();
        var corporationId = Positive(call.Session.GetInt("corporationID") ?? 0);
        if (corporationId == 0)
            corporationId = Positive(call.Session.GetInt("corpid") ?? 0);
        var characterId = call.Session.CharacterId ?? call.Session.GetInt("charid") ?? 0;
        if (corporationId == 0 || characterId <= 0 || !await canReadLedger(corporationId, characterId, call.Cancellation))
            return new PyList([]);

        if (call.Method == "GetObserverLedger")
        {
            var observerId = Positive(call.Arg(0));
            if (observerId == 0)
                return new PyList([]);
            var now = _nowFileTime();
            var entries = await reads.RowsAsync(MiningLedgerQueries.Observer, [observerId, corporationId, now, now - MiningLedgerStore.RetentionFileTimeTicks],
                call.Cancellation);
            return entries.ToKeyValList();
        }

        var listedAt = _nowFileTime();
        var observers = await reads.RowsAsync(MiningLedgerQueries.Observers, [corporationId, listedAt, listedAt - MiningLedgerStore.RetentionFileTimeTicks],
            call.Cancellation);
        return observers.ToKeyValList();
    }

    private static long Positive(long value) => Math.Max(0, value);

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Positive(number.Value),
        PyLong number => Positive(number.Value),
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => long.TryParse(Eve.Macho.JsSemantics.StringValue(text)!, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Positive(parsed) : 0,
        _ => 0,
    };
}
