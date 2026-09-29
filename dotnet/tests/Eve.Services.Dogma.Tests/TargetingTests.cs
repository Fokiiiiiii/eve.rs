using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

/// <summary>The targeting calls of the dogma location the client binds in space: what they pass on, what they answer and the errors of a lock that is refused.</summary>
public class TargetingTests
{
    private const long System = 30100032;
    private const long Ship = 9988400002151, Station = 60015249;

    private static async Task<(Rig Rig, ClientSession Session, string Oid)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId, station: null);
        session.Set("solarsystemid", new PyInt(System));
        return (rig, session, await rig.BindInSpaceAsync(session, System));
    }

    private static (string Name, Dictionary<string, PyValue> Values) UserError(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        return (((PyStr)arguments[0]).Value, ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value));
    }

    private static async Task<(string Name, Dictionary<string, PyValue> Values)> RefusedAsync(Rig rig, ClientSession session, string oid, TargetingResult refusal)
    {
        rig.Targeting.Answer = refusal;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.CallAsync(session, "AddTarget", oid, new PyLong(Ship)));
        return UserError(error);
    }

    [PgFact]
    public async Task A_ship_in_space_binds_dogma_to_its_solar_system_and_nothing_else()
    {
        var (rig, session, _) = await InSpaceAsync();
        await using var _ = rig;
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(30100034), new PyInt(5)), PyNone.Instance));
        // a ship in a station still binds to the station, not the system
        var docked = Rig.Session(session.CharacterId, id: 2);
        docked.Set("solarsystemid", new PyInt(System));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(docked, "MachoBindObject", null, Py.Tuple(new PyInt(System), new PyInt(5)), PyNone.Instance));
    }

    [PgFact]
    public async Task Adding_a_target_passes_it_on_and_answers_whether_the_lock_is_going_and_what_is_locked()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        rig.Targeting.Answer = new TargetingResult(true, true, []);
        var going = Assert.IsType<PyTuple>(await rig.CallAsync(session, "AddTarget", oid, new PyLong(Ship)));
        Assert.Equal((new PyInt(1), 0), (going.Items[0], ((PyList)going.Items[1]).Items.Count));
        rig.Targeting.Answer = new TargetingResult(true, false, [Station, Ship]);
        var had = Assert.IsType<PyTuple>(await rig.CallAsync(session, "AddTarget", oid, new PyInt(Station)));
        Assert.Equal(new PyInt(0), had.Items[0]);
        // ids are ints when they fit and longs when they do not, as the reference marshals them
        Assert.Equal([new PyInt(Station), new PyLong(Ship)], ((PyList)had.Items[1]).Items);
        Assert.Equal(
            [(TargetingOrder.Lock, new[] { Ship }), (TargetingOrder.Lock, new[] { Station })],
            rig.Targeting.Asked.Select(a => (a.Order, a.Targets)));
    }

    [PgFact]
    public async Task The_other_calls_pass_on_what_they_are_given_and_answer_as_the_reference_does()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        rig.Targeting.Answer = new TargetingResult(true, false, [Ship]);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "CancelAddTarget", oid, new PyLong(Ship)));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "RemoveTarget", oid, new PyLong(Ship)));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "RemoveTargets", oid, new PyList([new PyLong(Ship), new PyInt(Station), new PyInt(0)])));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "ClearTargets", oid));
        Assert.Equal([new PyLong(Ship)], ((PyList)await rig.CallAsync(session, "GetTargets", oid)).Items);
        Assert.Equal([new PyLong(Ship)], ((PyList)await rig.CallAsync(session, "GetTargeters", oid)).Items);
        Assert.Equal(
            [
                (TargetingOrder.Cancel, new[] { Ship }),
                (TargetingOrder.Remove, new[] { Ship }),
                (TargetingOrder.Remove, new[] { Ship, Station }),
                (TargetingOrder.Clear, Array.Empty<long>()),
                (TargetingOrder.Targets, Array.Empty<long>()),
                (TargetingOrder.Targeters, Array.Empty<long>()),
            ],
            rig.Targeting.Asked.Select(a => (a.Order, a.Targets)));
    }

    [PgFact]
    public async Task A_call_with_no_bound_object_the_session_was_given_or_no_ship_in_space_is_a_denied_ship_change()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        var unbound = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.CallAsync(session, "GetTargets", "N=65450:99999"));
        Assert.Equal("DeniedShipChanged", UserError(unbound).Name);
        rig.Targeting.Answer = null;
        var noShip = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.CallAsync(session, "GetTargets", oid));
        Assert.Equal("DeniedShipChanged", UserError(noShip).Name);
        Assert.All(rig.Targeting.Asked, a => Assert.Equal(TargetingOrder.Targets, a.Order));
    }

    [PgFact]
    public async Task A_lock_that_is_refused_is_the_error_the_reference_raises_for_the_reason()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        foreach (var reason in new[] { "NOT_IN_SPACE", "NO_SHIP_IN_SPACE", "SHIP_NOT_FOUND" })
        {
            Assert.Equal(("DeniedShipChanged", 0), (await RefusedAsync(rig, session, oid, TargetingResult.Refusal(reason))) is var (name, values) ? (name, values.Count) : default);
        }
        Assert.Equal("DeniedTargetSelf", (await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_SELF"))).Name);
        Assert.Equal("DeniedTargetSelfWarping", (await RefusedAsync(rig, session, oid, TargetingResult.Refusal("SOURCE_WARPING"))).Name);
        Assert.Equal("TargetingAttemptCancelled", (await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_NOT_FOUND"))).Name);

        var (warping, warpingValues) = await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_WARPING", 601));
        Assert.Equal(("DeniedTargetOtherWarping", new PyTuple([new PyInt(4), new PyInt(601)])), (warping, warpingValues["targetName"]));
        var (unknown, unknownValues) = await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_WARPING"));
        Assert.Equal(("DeniedTargetOtherWarping", new PyStr("that target")), (unknown, unknownValues["targetName"]));

        var (limit, limitValues) = await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_LOCK_LIMIT_REACHED", 0, 4));
        Assert.Equal(("TargetingSystemsAlreadyFullyUtilized", new PyInt(4)), (limit, limitValues["limit"]));

        var (invulnerable, invulnerableValues) = await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_INVULNERABLE"));
        Assert.Equal(("CustomNotify", new PyStr("You cannot lock that target while it is still invulnerable after undocking.")), (invulnerable, invulnerableValues["notify"]));
    }

    [PgFact]
    public async Task A_target_out_of_range_says_the_range_the_ship_locks_at_the_name_of_the_target_and_how_far_it_is()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        // as recorded from the reference: a whole range is an int, a distance with a fraction is a real
        var (name, values) = await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_OUT_OF_RANGE", 92885, 29700, 306288156879.55023));
        Assert.Equal("TargetNotWithinRangeGeneric", name);
        Assert.Equal((new PyInt(29700), new PyTuple([new PyInt(4), new PyInt(92885)]), new PyReal(306288156879.55023)), (values["desiredRange"], values["targetName"], values["actualDistance"]));
        Assert.Equal(new PyInt(0), (await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_OUT_OF_RANGE", 0, -5, -1)))
            .Values["desiredRange"]);
    }

    [PgFact]
    public async Task What_cannot_be_locked_is_a_failed_attempt_that_names_the_type()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        // as recorded: a planet
        var (name, values) = await RefusedAsync(rig, session, oid, TargetingResult.Refusal("TARGET_NOT_TARGETABLE", 2015));
        Assert.Equal(("DeniedTargetingAttemptFailed", new PyInt(2015)), (name, values["target"]));
        Assert.Equal(new PyInt(0), (await RefusedAsync(rig, session, oid, TargetingResult.Refusal("SOMETHING_NEW"))).Values["target"]);
    }
}
