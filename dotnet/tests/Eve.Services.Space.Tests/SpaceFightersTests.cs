using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The fighter calls the host serves without a squadron in space (<see cref="SpaceFighters"/>): what the ship holds, and the refusals of the calls that
/// need a squadron out in space, each with its reason. The pilot flies a Cockroach, which has no launch tubes and no fighter bay.
/// </summary>
public sealed class SpaceFightersTests
{
    private const int NoSquadron = 7_001;

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static string UserMessage(PyValue payload) => new MachoError(MachoErrorType.WrappedException, payload).UserErrorMessage;

    [PgFact]
    public async Task A_ship_with_no_squadrons_answers_empty_tubes_space_and_states()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var fighters = new SpaceFighters(rig.Ships, rig.Statics, _ => null);

        var answer = Assert.IsType<PyTuple>(await fighters.GetFightersForShipAsync(Call("GetFightersForShip", session)));

        Assert.Equal(3, answer.Items.Count);
        Assert.Empty(Assert.IsType<PyList>(answer.Items[0]).Items);
        Assert.Empty(Assert.IsType<PyList>(answer.Items[1]).Items);
        Assert.Empty(Assert.IsType<PyDict>(answer.Items[2]).Entries);
    }

    [PgFact]
    public async Task A_squadron_that_is_not_in_the_ship_is_refused_with_its_reason_and_a_tube_that_is_empty_says_so()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var fighters = new SpaceFighters(rig.Ships, rig.Statics, _ => null);

        var load = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fighters.LoadFightersToTubeAsync(Call("LoadFightersToTube", session, new PyInt(NoSquadron), new PyInt(159))).AsTask());
        Assert.Equal("That squadron is not in your ship's fighter bay.", load.Error.UserErrorMessage);

        var launch = Assert.IsType<PyDict>(await fighters.LaunchFightersFromTubesAsync(Call("LaunchFightersFromTubes", session, new PyList([new PyInt(159)]))));
        Assert.Equal("That launch tube is empty.", UserMessage(Assert.Single(launch.Entries).Value));
    }

    [PgFact]
    public async Task Orders_and_abilities_for_squadrons_out_in_space_are_refused_per_fighter_and_never_answered_as_done()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var fighters = new SpaceFighters(rig.Ships, rig.Statics, _ => null);
        var ids = new PyList([new PyInt(NoSquadron)]);

        foreach (var method in new[] { "RecallFightersToTubes", "CmdActivateAbilitySlots", "CmdDeactivateAbilitySlots" })
        {
            var result = method == "RecallFightersToTubes"
                ? await fighters.RecallFightersToTubesAsync(Call(method, session, ids))
                : method == "CmdActivateAbilitySlots"
                    ? await fighters.CmdActivateAbilitySlotsAsync(Call(method, session, ids, new PyInt(1)))
                    : await fighters.CmdDeactivateAbilitySlotsAsync(Call(method, session, ids, new PyInt(1)));
            var entry = Assert.Single(Assert.IsType<PyDict>(result).Entries);
            Assert.Equal(new PyInt(NoSquadron), entry.Key);
            Assert.Equal("That fighter is not in space.", UserMessage(entry.Value));
        }

        var abandon = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fighters.CmdAbandonFighterAsync(Call("CmdAbandonFighter", session, new PyInt(NoSquadron))).AsTask());
        Assert.Equal("That fighter is not in space.", abandon.Error.UserErrorMessage);

        var movement = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fighters.ExecuteMovementCommandOnFightersAsync(Call("ExecuteMovementCommandOnFighters", session, ids, new PyStr("STOP"))).AsTask());
        Assert.Equal("None of the selected fighters is in space.", movement.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task A_scoop_is_refused_for_the_cargo_hold_before_the_squadron_is_looked_at()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var fighters = new SpaceFighters(rig.Ships, rig.Statics, _ => null);

        var cargo = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fighters.CmdScoopAbandonedFighterFromSpaceAsync(Call("CmdScoopAbandonedFighterFromSpace", session, new PyInt(NoSquadron), new PyInt(5))).AsTask());

        Assert.Equal("A fighter can only be scooped into the fighter bay or a launch tube.", cargo.Error.UserErrorMessage);
    }
}
