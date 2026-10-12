using System.Collections.Concurrent;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Trade;
using Eve.Universe;

namespace Eve.Services.Station;

/// <summary>
/// The direct trade between two pilots docked in the same place (<c>trademgr</c>). <c>InitiateTrade</c> validates the two pilots and returns the
/// trade as a moniker the client binds; the bound calls (<c>List</c>, <c>Add</c>, <c>MultiAdd</c>, <c>OfferMoney</c>, <c>MakeOffer</c>, <c>Abort</c>)
/// change the trade through <see cref="TradeAuthority"/>, then tell the two pilots what changed (after the commit).
/// </summary>
[Service("trademgr")]
public sealed partial class TradeMgrService(CharacterSessions sessions, TradeAuthority? trades = null, ItemTypeCatalog? types = null, StationCatalog? stations = null, Func<long>? nowUnixMs = null) : IBoundObjectReleaseHandler
{
    private const string MonikerName = "carbon.common.script.net.moniker.Moniker";
    private const string RowName = "carbon.common.script.sys.row.Row";

    /// <summary>The header of the trade's list row, in the client's order (the client reads <c>tradeContainerID</c>, <c>traders</c>, <c>money</c> and <c>items</c>).</summary>
    private static readonly string[] ListHeader = ["tradeContainerID", "traders", "state", "money", "items"];

    /// <summary>The bound objects of the trades: each bound object id names the trade it is for.</summary>
    private readonly ConcurrentDictionary<string, long> _bound = new(StringComparer.Ordinal);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    // ── InitiateTrade ────────────────────────────────────────────────────────

    [Method("InitiateTrade")]
    public async ValueTask<PyValue> InitiateTrade(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var initiator = call.Session;
        var initiatorCharacterId = initiator.CharacterId ?? initiator.GetInt("charID") ?? initiator.GetInt("characterID") ?? 0;
        var targetCharacterId = PositiveCharacterId(call.Arg(0));
        if (initiatorCharacterId <= 0 || targetCharacterId <= 0)
            throw Notify("Trade target could not be resolved.");
        if (initiatorCharacterId == targetCharacterId)
            throw Notify("You cannot trade with yourself.");

        var location = DockedLocationId(initiator);
        if (location <= 0)
            throw Notify("Both pilots must be docked in the same station or structure to trade.");

        var target = sessions.Of(targetCharacterId);
        if (target is null || DockedLocationId(target) <= 0)
            throw Notify("The selected pilot must be docked and online to trade.");
        if (location != DockedLocationId(target))
            throw Notify("Both pilots must be docked in the same station or structure to trade.");

        if (trades is null)
            throw Notify("Direct trading is not available.");
        var solarSystem = initiator.GetInt("solarsystemid2") ?? initiator.GetInt("solarsystemid") ?? target.GetInt("solarsystemid2") ?? target.GetInt("solarsystemid") ?? 0;
        TradeView view;
        try
        {
            view = await trades.InitiateAsync(initiatorCharacterId, targetCharacterId, location, solarSystem, call.Cancellation).ConfigureAwait(false);
        }
        catch (TradeRefusedException refused)
        {
            throw Notify(refused.Message);
        }

        // a pilot who disconnects in the trade ends it: its items go back to where they were
        initiator.Closed += () => _ = EndOnCloseAsync(view.TradeId);
        target.Closed += () => _ = EndOnCloseAsync(view.TradeId);

        var moniker = Moniker(view.TradeId);
        target.SendNotification("OnTradeInitiate", "charid", new PyInt(initiatorCharacterId), moniker);
        return moniker;
    }

    // ── MachoBindObject: the moniker's door ──────────────────────────────────

    /// <summary>
    /// <c>MachoBindObject((tradeID,), (method, args, kwargs))</c>: binds the trade for a participant, then makes the nested call, whose answer goes
    /// back with the bound object. A pilot who is not in the trade gets the same refusal as a missing trade.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        if (trades is null || IdOf(parameters) is not { } tradeId)
            throw Notify("Trade session is no longer available.");
        var view = await trades.ViewAsync(tradeId, call.Cancellation).ConfigureAwait(false);
        if (view is null || !view.IsOpen || !view.Involves(CharacterOf(call.Session)))
            throw Notify("Trade session is no longer available.");

        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = tradeId;
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(Now())))));
        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && Eve.Macho.JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. ArgsOf(nested.Items.Count > 1 ? nested.Items[1] : None)],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            }).ConfigureAwait(false);
        }
        return Py.Tuple(bound, result);
    }

    // ── the bound trade ──────────────────────────────────────────────────────

    /// <summary>The trade the bound call is for, checked against the caller: it must be open, the caller must be in it, and it must be docked where the trade is.</summary>
    private async ValueTask<TradeView> BoundViewAsync(ServiceCall call)
    {
        if (trades is null || call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var tradeId))
            throw Notify("Trade session is no longer available.");
        var view = await trades.ViewAsync(tradeId, call.Cancellation).ConfigureAwait(false);
        if (view is null || !view.IsOpen || !view.Involves(CharacterOf(call.Session)))
            throw Notify("Trade session is no longer available.");
        if (DockedLocationId(call.Session) != view.LocationId)
        {
            await EndAsync(view.TradeId, call.Cancellation).ConfigureAwait(false);
            throw Notify("Both pilots must be docked in the same station or structure to trade.");
        }
        return view;
    }

    /// <summary>The trade as the client's list row: the container, the traders, each side's acceptance, the ISK offers and the items in custody.</summary>
    [Method("List")]
    public async ValueTask<PyValue> List(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        return await ListRowAsync(view, call.Cancellation).ConfigureAwait(false);
    }

    [Method("GetSelfInvItem")]
    public async ValueTask<PyValue> GetSelfInvItem(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        var stationType = stations?.Get(view.LocationId)?.TypeId ?? 0;
        return Py.KeyVal(
            ("itemID", InventoryWire.Id(view.TradeId)),
            ("typeID", new PyInt(stationType)),
            ("ownerID", InventoryWire.Id(CharacterOf(call.Session))),
            ("locationID", InventoryWire.Id(view.LocationId)),
            ("flagID", new PyInt(ItemFlags.Hangar)),
            ("stacksize", new PyInt(1)),
            ("quantity", new PyInt(-1)),
            ("singleton", new PyInt(1)));
    }

    /// <summary><c>Add(itemID, sourceLocationID, qty=...)</c>: puts one item (or a part of its stack, <c>qty</c>) into the trade.</summary>
    [Method("Add")]
    public async ValueTask<PyValue> Add(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        var itemId = IdOf(call.Arg(0)) ?? throw Notify("That item is no longer there.");
        var quantity = IdOf(Kwarg(call.Kwargs, "qty")) ?? 0;
        await StageAsync(view, CharacterOf(call.Session), itemId, quantity, call.Cancellation).ConfigureAwait(false);
        return PyBool.True;
    }

    /// <summary><c>MultiAdd(itemIDs, sourceLocationID)</c>: puts several whole items into the trade.</summary>
    [Method("MultiAdd")]
    public async ValueTask<PyValue> MultiAdd(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        var ids = ArgsOf(call.Arg(0)).Select(IdOf).Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return PyBool.False;
        foreach (var itemId in ids)
            await StageAsync(view, CharacterOf(call.Session), itemId, 0, call.Cancellation).ConfigureAwait(false);
        return PyBool.True;
    }

    /// <summary><c>OfferMoney(amount)</c>: the ISK the caller offers (cannot exceed its wallet). Any change clears both acceptances.</summary>
    [Method("OfferMoney")]
    public async ValueTask<PyValue> OfferMoney(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        var amount = Math.Round((decimal)NumberOf(call.Arg(0)), 2);
        try
        {
            var change = await trades!.SetMoneyAsync(view.TradeId, CharacterOf(call.Session), amount, call.Cancellation).ConfigureAwait(false);
            await AnnounceAsync(change, call.Cancellation).ConfigureAwait(false);
        }
        catch (TradeRefusedException refused)
        {
            throw Notify(refused.Message);
        }
        return PyBool.True;
    }

    /// <summary>
    /// <c>MakeOffer(manifest)</c>: the caller accepts what it was shown (<c>tradeContainerID</c>, <c>money</c>, <c>tradeItems</c>). A manifest that is
    /// not the trade's clears both acceptances and tells the caller so; when both accept, the trade completes.
    /// </summary>
    [Method("MakeOffer")]
    public async ValueTask<PyValue> MakeOffer(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        var (container, money, items) = ManifestOf(call.Arg(0));
        if (container != view.TradeId)
            money = [];  // a manifest for another trade: the money check fails, so the offer is a mismatch
        TradeOfferResult outcome;
        try
        {
            outcome = await trades!.OfferAsync(view.TradeId, CharacterOf(call.Session), money, items, call.Cancellation).ConfigureAwait(false);
        }
        catch (TradeRefusedException refused)
        {
            throw Notify(refused.Message);
        }

        switch (outcome.Outcome)
        {
            case TradeOfferOutcome.Mismatch:
                await AnnounceAsync(outcome.Change, call.Cancellation).ConfigureAwait(false);
                call.Session.SendNotification("OnTradeOfferReset", "clientID", new PyLong(view.TradeId), PyBool.True);
                return PyBool.False;
            case TradeOfferOutcome.Pending:
                await AnnounceAsync(outcome.Change, call.Cancellation).ConfigureAwait(false);
                return PyBool.True;
            case TradeOfferOutcome.Refused:
                await AnnounceAsync(outcome.Change, call.Cancellation).ConfigureAwait(false);
                throw Notify(outcome.Reason ?? "Trade completion failed.");
            default:
                await AnnounceCompletedAsync(outcome, call.Cancellation).ConfigureAwait(false);
                return PyBool.True;
        }
    }

    /// <summary><c>Abort()</c>: a participant cancels the trade; every item goes back.</summary>
    [Method("Abort")]
    public async ValueTask<PyValue> Abort(ServiceCall call)
    {
        var view = await BoundViewAsync(call).ConfigureAwait(false);
        await EndAsync(view.TradeId, call.Cancellation).ConfigureAwait(false);
        return PyBool.True;
    }

    // ── changes and what the pilots are told ─────────────────────────────────

    private async Task StageAsync(TradeView view, long characterId, long itemId, long quantity, CancellationToken cancellationToken)
    {
        TradeChange change;
        try
        {
            change = await trades!.StageAsync(view.TradeId, characterId, itemId, quantity, cancellationToken).ConfigureAwait(false);
        }
        catch (TradeRefusedException refused)
        {
            throw Notify(refused.Message);
        }
        await AnnounceAsync(change, cancellationToken).ConfigureAwait(false);
    }

    private static long CharacterOf(ClientSession session) => session.CharacterId ?? session.GetInt("charID") ?? session.GetInt("characterID") ?? 0;

    /// <summary>Ends a trade (cancel or disconnect) and tells both pilots.</summary>
    private async Task EndAsync(long tradeId, CancellationToken cancellationToken)
    {
        if (trades is null) return;
        var change = await trades.AbortAsync(tradeId, cancellationToken).ConfigureAwait(false);
        if (change is null) return;
        await AnnounceAsync(change, cancellationToken).ConfigureAwait(false);
        foreach (var traderId in new[] { change.View.InitiatorId, change.View.TargetId })
            sessions.Of(traderId)?.SendNotification("OnTradeCancel", "clientID", new PyLong(tradeId));
    }

    private async Task EndOnCloseAsync(long tradeId)
    {
        try
        {
            await EndAsync(tradeId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"eve-host: trade {tradeId} could not be ended when a pilot disconnected: {error.Message}");
        }
    }

    /// <summary>After a change: the item moves, the priming of the items, the ISK offers and the acceptances, to both pilots.</summary>
    private async Task AnnounceAsync(TradeChange change, CancellationToken cancellationToken)
    {
        var view = change.View;
        var staged = change.Moves.Where(m => !m.Removed).Select(m => new PyLong(m.After.Id)).ToList();
        foreach (var traderId in new[] { view.InitiatorId, view.TargetId })
        {
            var session = sessions.Of(traderId);
            if (session is null) continue;
            foreach (var move in change.Moves)
                session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(await RowAsync(move.After, cancellationToken).ConfigureAwait(false)), Differences(move), PyNone.Instance);
            if (staged.Count > 0)
                session.SendNotification("OnPrimingNeededForTradeItems", "clientID", new PyList([.. staged]));
            session.SendNotification("OnTradeMoneyOffer", "clientID", new PyLong(view.TradeId), new PyList([new PyReal((double)view.InitiatorIsk), new PyReal((double)view.TargetIsk)]));
            session.SendNotification("OnTradeOffer", "clientID", new PyLong(view.TradeId), new PyList([new PyInt(view.InitiatorAccepted ? 1 : 0), new PyInt(view.TargetAccepted ? 1 : 0)]));
        }
    }

    /// <summary>The trade completed: the items it moved, then <c>OnTradeComplete</c> with the items the pilots received.</summary>
    private async Task AnnounceCompletedAsync(TradeOfferResult outcome, CancellationToken cancellationToken)
    {
        var view = outcome.Change.View;
        var received = new List<PyValue>();
        foreach (var move in outcome.Change.Moves)
            received.Add(InventoryWire.PackedRow(await RowAsync(move.After, cancellationToken).ConfigureAwait(false)));
        foreach (var traderId in new[] { view.InitiatorId, view.TargetId })
        {
            var session = sessions.Of(traderId);
            if (session is null) continue;
            foreach (var move in outcome.Change.Moves)
                session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(await RowAsync(move.After, cancellationToken).ConfigureAwait(false)), Differences(move), PyNone.Instance);
            session.SendNotification("OnTradeComplete", "clientID", new PyLong(view.TradeId), new PyList([.. received]));
        }
    }

    private static PyDict Differences(TradeItemMove move)
    {
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (move.After.OwnerId != move.PreviousOwnerId)
            differences.Add(new(new PyInt(2), InventoryWire.Id(move.PreviousOwnerId)));
        if (move.After.LocationId != move.PreviousLocationId)
            differences.Add(new(new PyInt(3), InventoryWire.Id(move.PreviousLocationId)));
        if (move.After.Flag != move.PreviousFlag)
            differences.Add(new(new PyInt(4), new PyInt(move.PreviousFlag)));
        if (move.After.Quantity != move.PreviousQuantity)
            differences.Add(new(new PyInt(9), InventoryWire.Id(move.PreviousQuantity)));
        return new PyDict(differences);
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    private async Task<PyValue> ListRowAsync(TradeView view, CancellationToken cancellationToken)
    {
        var items = new List<PyValue>();
        foreach (var item in view.Items)
            items.Add(InventoryWire.PackedRow(await RowAsync(item, cancellationToken).ConfigureAwait(false)));
        var header = new PyList([.. ListHeader.Select(name => (PyValue)new PyStr(name))]);
        var line = new PyList([
            InventoryWire.Id(view.TradeId),
            new PyList([InventoryWire.Id(view.InitiatorId), InventoryWire.Id(view.TargetId)]),
            new PyList([new PyInt(view.InitiatorAccepted ? 1 : 0), new PyInt(view.TargetAccepted ? 1 : 0)]),
            new PyList([new PyReal((double)view.InitiatorIsk), new PyReal((double)view.TargetIsk)]),
            PyValue.PythonSet(items),
        ]);
        return new PyObject(new PyStr(RowName), new PyDict([
            new(new PyStr("header"), header),
            new(new PyStr("line"), line),
        ]));
    }

    private async Task<ItemRow> RowAsync(Item item, CancellationToken cancellationToken)
    {
        if (types is null) throw Notify("Direct trading is not available.");
        var type = await types.GetAsync(item.TypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"the item type {item.TypeId} is not in the static data");
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type.GroupId, type.CategoryId);
    }

    private PyValue Moniker(long tradeId) => new PyObject(new PyStr(MonikerName),
        Py.Tuple(new PyStr("trademgr"), new PyInt(MachoMessage.ProxyNodeId), Py.Tuple(new PyLong(tradeId)), None));

    private long Now() => nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── argument reading ─────────────────────────────────────────────────────

    /// <summary>The manifest of <c>MakeOffer</c>: the container, the ISK in trader order and the item ids of the set (each item an object with <c>itemID</c>).</summary>
    internal static (long Container, List<decimal> Money, HashSet<long> Items) ManifestOf(PyValue manifest)
    {
        var container = IdOf(FieldOf(manifest, "tradeContainerID")) ?? 0;
        var money = ArgsOf(FieldOf(manifest, "money")).Select(v => Math.Round((decimal)NumberOf(v), 2)).ToList();
        var items = new HashSet<long>();
        foreach (var entry in ArgsOf(FieldOf(manifest, "tradeItems")))
        {
            if (ItemIdOf(entry) is { } id) items.Add(id);
        }
        return (container, money, items);
    }

    /// <summary>A named field of a marshalled object (a KeyVal, or a dict in its arguments); None when it has none.</summary>
    private static PyValue FieldOf(PyValue value, string name)
    {
        if (value is PyPackedRow packed)
        {
            foreach (var (key, field) in packed.Fields())
                if (key == name) return field;
            return None;
        }
        var entries = value switch
        {
            PyObjectEx ex => ex.Dict,
            PyObject { Args: PyDict dict } => dict.Entries,
            PyDict dict => dict.Entries,
            _ => null,
        };
        if (entries is null) return None;
        foreach (var (key, field) in entries)
        {
            if (Eve.Macho.JsSemantics.StringValue(key) == name) return field;
        }
        return None;
    }

    /// <summary>
    /// The item id of one element of a trade's item set: a plain id, an object with <c>itemID</c>, a packed row, or a row object whose
    /// <c>line</c> starts with the id (the forms EveJS reads, <c>tradeMgrService.js</c> <c>getItemIDFromManifestEntry</c>).
    /// </summary>
    private static long? ItemIdOf(PyValue entry)
    {
        if (IdOf(entry) is { } direct) return direct;
        if (IdOf(FieldOf(entry, "itemID")) is { } named) return named;
        if (entry is PyObject { Args: PyDict } && FieldOf(entry, "line") is PyList { Items.Count: > 0 } line && IdOf(line.Items[0]) is { } fromLine)
            return fromLine;
        return null;
    }

    /// <summary>
    /// The elements of a list, tuple, or Python set (its marshalled form: the set's first element in the header, the rest in the list
    /// tail); empty for anything else.
    /// </summary>
    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        PyObjectEx { Header: PyTuple { Items.Count: 2 } header } ex when header.Items[0] is PyToken { Value: "__builtin__.set" } && header.Items[1] is PyTuple { Items.Count: 1 } payload
            => (payload.Items[0] is PyList first ? first.Items : []).Concat(ex.List),
        _ => [],
    };

    private static PyValue Kwarg(PyValue kwargs, string name) => FieldOf(kwargs, name);

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static double NumberOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real => real.Value,
        _ => 0,
    };

    private static long PositiveCharacterId(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => 0,
    };

    private static long DockedLocationId(ClientSession session) => session.DockedLocationId ?? 0;

    private static MachoWrappedException Notify(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
