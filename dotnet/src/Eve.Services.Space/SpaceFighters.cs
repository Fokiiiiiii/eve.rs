using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>
/// The player fighters of the pilots, as the host serves <c>fighterMgr</c> (EVE.js <c>fighterMgrService</c>). The fighter bay and the launch tubes are items of
/// the ship in the database: a squadron goes into a tube, and back into the bay, here, with the item change and the notices sent in the reference's order.
/// A squadron is out in space only in the simulation, and the protocol does not carry fighter squadrons yet (ADR 0015, WP-E to WP-G). So every call that
/// needs a squadron out in space (launch, recall, orders, abilities, abandon and scoop) is refused with its reason: nothing is answered as done.
/// </summary>
public sealed class SpaceFighters(CharacterShips ships, DogmaStatic statics, Func<ClientSession, int?> solarSystemOf) : ISpaceFighters
{
    private const string NoShipText = "Your ship could not be found.";
    private const string NotInSpaceText = "That fighter is not in space.";
    private const string NotInBayOrTubeText = "A fighter can only be scooped into the fighter bay or a launch tube.";
    private const string EmptyTubeText = "That launch tube is empty.";
    private const string LaunchUnavailableText =
        "Launching fighters is not available on this server yet: the simulation does not carry fighter squadrons (ADR 0015).";

    public async ValueTask<PyValue> GetFightersForShipAsync(ServiceCall call)
    {
        if (ShipOf(call.Session) is not { } shipId || call.Session.CharacterId is not { } characterId)
        {
            throw MachoErrors.ThrowUserError(NoShipText);
        }
        var inTubes = (await ships.FightersOfShipAsync(shipId, call.Cancellation))
            .Where(i => FighterTubes.IsTube(i.Flag))
            .OrderBy(i => i.Flag)
            .ToList();
        var inSpace = solarSystemOf(call.Session) is { } system
            ? await ships.FightersInSpaceAsync(characterId, system, call.Cancellation)
            : [];
        return new PyTuple([
            new PyList([.. inTubes.Select(i => FighterRow(i.Flag, i))]),
            new PyList([.. inSpace.Select(i => FighterRow(i.Flag, i))]),
            new PyDict([]),
        ]);
    }

    public async ValueTask<PyValue> LoadFightersToTubeAsync(ServiceCall call)
    {
        var (session, characterId, shipId) = Pilot(call);
        var tubeFlag = (int)IdOf(call.Arg(1));
        var move = await ships.LoadFighterToTubeAsync(characterId, shipId, IdOf(call.Arg(0)), tubeFlag, call.Cancellation);
        if (!move.Accepted)
        {
            throw MachoErrors.ThrowUserError(MessageOf(move));
        }
        var squadron = move.Change!.After;
        ShipNotifications.SendItemChange(session, move.Change!, RowOf);
        session.SendNotification("OnFighterTubeContentUpdate", "clientID",
            new PyInt(tubeFlag), InventoryWire.Id(squadron.Id), new PyInt(squadron.TypeId),
            new PyInt((int)FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity)));
        NotifyTubeState(session, tubeFlag, "READY");
        return PyBool.True;
    }

    public async ValueTask<PyValue> UnloadTubeToFighterBayAsync(ServiceCall call)
    {
        var (session, characterId, shipId) = Pilot(call);
        var tubeFlag = (int)IdOf(call.Arg(0));
        var move = await ships.UnloadTubeToBayAsync(characterId, shipId, tubeFlag, call.Cancellation);
        if (!move.Accepted)
        {
            throw MachoErrors.ThrowUserError(MessageOf(move));
        }
        ShipNotifications.SendItemChange(session, move.Change!, RowOf);
        session.SendNotification("OnFighterTubeContentEmpty", "clientID", new PyInt(tubeFlag));
        NotifyTubeState(session, tubeFlag, "EMPTY");
        return PyNone.Instance;
    }

    public async ValueTask<PyValue> LaunchFightersFromTubesAsync(ServiceCall call)
    {
        var (_, _, shipId) = Pilot(call);
        var inTubes = await ships.FightersOfShipAsync(shipId, call.Cancellation);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var tube in IdsOf(call.Arg(0)))
        {
            var message = inTubes.Any(i => i.Flag == tube) ? LaunchUnavailableText : EmptyTubeText;
            entries.Add(new(new PyInt((int)tube), MachoErrors.UserErrorPayload(message)));
        }
        return new PyDict(entries);
    }

    /// <summary>Each fighter the call names is refused: none of them can be in space while the simulation carries no squadron.</summary>
    public ValueTask<PyValue> RecallFightersToTubesAsync(ServiceCall call) => RefuseEachNotInSpace(call);

    public ValueTask<PyValue> CmdActivateAbilitySlotsAsync(ServiceCall call) => RefuseEachNotInSpace(call);

    public ValueTask<PyValue> CmdDeactivateAbilitySlotsAsync(ServiceCall call) => RefuseEachNotInSpace(call);

    public async ValueTask<PyValue> ExecuteMovementCommandOnFightersAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        foreach (var id in IdsOf(call.Arg(0)))
        {
            if (await SquadronInSpaceAsync(session, characterId, id, call.Cancellation) is not null)
            {
                throw MachoErrors.ThrowUserError(LaunchUnavailableText);
            }
        }
        throw MachoErrors.ThrowUserError("None of the selected fighters is in space.");
    }

    public async ValueTask<PyValue> CmdAbandonFighterAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var inSpace = await SquadronInSpaceAsync(session, characterId, IdOf(call.Arg(0)), call.Cancellation);
        throw MachoErrors.ThrowUserError(inSpace is null ? NotInSpaceText : LaunchUnavailableText);
    }

    public async ValueTask<PyValue> CmdScoopAbandonedFighterFromSpaceAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        if (!IsFighterSlot((int)IdOf(call.Arg(1))))
        {
            throw MachoErrors.ThrowUserError(NotInBayOrTubeText);
        }
        var inSpace = await SquadronInSpaceAsync(session, characterId, IdOf(call.Arg(0)), call.Cancellation);
        throw MachoErrors.ThrowUserError(inSpace is null ? NotInSpaceText : LaunchUnavailableText);
    }

    /// <summary>A fighter bay or launch tube: the bay (158) and the five tubes (159 to 163) hold squadrons.</summary>
    private static bool IsFighterSlot(int flag) => flag == CharacterShips.FighterBayFlag || FighterTubes.IsTube(flag);

    private static ValueTask<PyValue> RefuseEachNotInSpace(ServiceCall call)
    {
        var entries = IdsOf(call.Arg(0))
            .Select(id => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(id), MachoErrors.UserErrorPayload(NotInSpaceText)))
            .ToArray();
        return ValueTask.FromResult<PyValue>(new PyDict(entries));
    }

    /// <summary>The squadron as the database has it when it is the pilot's and out in the pilot's solar system (else null).</summary>
    private async Task<Item?> SquadronInSpaceAsync(ClientSession session, long characterId, long fighterId, CancellationToken cancellationToken)
    {
        if (solarSystemOf(session) is not { } system || await ships.ItemAsync(fighterId, cancellationToken) is not { } item)
        {
            return null;
        }
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        return item.OwnerId == characterId && item.LocationId == system && item.Flag == CharacterShips.InSpaceFlag
            && statics.Type(item.TypeId)?.CategoryId == CharacterShips.FighterCategory
            ? item
            : null;
    }

    private (ClientSession Session, long CharacterId, long ShipId) Pilot(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || ShipOf(call.Session) is not { } shipId)
        {
            throw MachoErrors.ThrowUserError(NoShipText);
        }
        return (call.Session, characterId, shipId);
    }

    private static long? ShipOf(ClientSession session) => session.GetInt("shipid");

    /// <summary>The tube's state notice: the state name, then the times (none for a state without a timer) and no error.</summary>
    private static void NotifyTubeState(ClientSession session, int tubeFlag, string state) =>
        session.SendNotification("OnFighterTubeTaskStatus", "clientID", new PyInt(tubeFlag), new PyStr(state), PyNone.Instance, PyNone.Instance, PyNone.Instance);

    private static string MessageOf(FighterMove move) => move.Refusal switch
    {
        FighterMoveRefusal.NoShip => NoShipText,
        FighterMoveRefusal.NotInBay => "That squadron is not in your ship's fighter bay.",
        FighterMoveRefusal.NotASquadron => "That item is not a fighter squadron.",
        FighterMoveRefusal.NoSuchTube => "That is not a fighter launch tube.",
        FighterMoveRefusal.TubeOccupied => "That launch tube already holds a squadron.",
        FighterMoveRefusal.TubeEmpty => EmptyTubeText,
        FighterMoveRefusal.NoSlotForClass => "Your ship has no launch slots for that squadron class.",
        FighterMoveRefusal.ClassSlotsFull => "Your ship's launch slots for that squadron class are full.",
        FighterMoveRefusal.TubesFull => "All of your ship's launch tubes are full.",
        FighterMoveRefusal.BayFull => $"There is not enough room in the fighter bay: {move.Available} m3 is free and {move.Volume} m3 is needed.",
        _ => throw new ArgumentOutOfRangeException(nameof(move)),
    };

    private static PyValue FighterRow(int flag, Item squadron) => new PyTuple([
        new PyInt(flag),
        InventoryWire.Id(squadron.Id),
        new PyInt(squadron.TypeId),
        new PyInt((int)FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity)),
    ]);

    private ItemRow RowOf(Item item)
    {
        var type = statics.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The ids a call names, as the drone orders read them (lists, tuples, sets), each once.</summary>
    private static IReadOnlyList<long> IdsOf(PyValue value) => SpaceDrones.IdsOf(value);

    private static long IdOf(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };
}
