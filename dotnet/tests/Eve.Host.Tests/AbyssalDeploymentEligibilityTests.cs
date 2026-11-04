using Eve.Content;
using Eve.Data;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalDeploymentEligibilityTests
{
    private static AbyssalParticipantPoseResult Pose(SimAbyssalDeploymentSnapshot snapshot) =>
        new(41, SimSceneKey.SolarSystem(30000142), [10, 20, 30], [0, 0, 1], null) { Deployment = snapshot };

    private static void AssertError(int code, MachoWrappedException error, params PyValue[] args) =>
        Assert.Equal(MachoErrors.UserErrorPayload("AbyssError",
            [new KeyValuePair<PyValue, PyValue>(new PyStr("errors"),
                new PyList([new PyTuple([new PyInt(code), new PyTuple(args)])]))]), error.Error.Payload);

    [PgFact]
    public async Task Official_type_list_uses_center_boundary_real_blocker_type_and_self_exclusion()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var rules = await AbyssalDeploymentEligibility.LoadAsync(sde, new ItemTypeCatalog(sde));
        var state = new SimAbyssalDeploymentSnapshot(false, false, false, false, false,
            [new SimAbyssalDeploymentObject(99, AbyssalTraceFactory.TraceTypeId, 1_000_000)]);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rules.ValidateNearbyAsync(41, state));
        AssertError(8, error, new PyInt(AbyssalTraceFactory.TraceTypeId));
        await rules.ValidateNearbyAsync(41, state with { NearbyObjects = [new(99, AbyssalTraceFactory.TraceTypeId, 1_000_000.01)] });
        await rules.ValidateNearbyAsync(41, state with { NearbyObjects = [new(41, AbyssalTraceFactory.TraceTypeId, 0)] });
        await rules.ValidateNearbyAsync(41, state with { NearbyObjects = [new(99, 603, 0), new(99, 603, 0)] }); // Ship category is not a disallowed type list entry.
    }

    [Theory]
    [InlineData("session", 11)] [InlineData("destruct", 18)] [InlineData("cloak", 15)]
    [InlineData("invulnerable", 17)] [InlineData("tether", 19)] [InlineData("warp", 14)]
    [InlineData("scramble", 20)] [InlineData("cyno", 22)]
    public void Source_activation_states_use_the_matching_client_error(string state, int code)
    {
        var snapshot = new SimAbyssalDeploymentSnapshot(state == "warp", state == "cloak", state == "cyno",
            state == "invulnerable", state == "scramble", []);
        var pose = Pose(snapshot) with { SessionChanging = state == "session", SelfDestructPending = state == "destruct", Tethered = state == "tether" };
        AssertError(code, Assert.Throws<MachoWrappedException>(() =>
        {
            AbyssalDeploymentEligibility.ValidateSessionState(pose);
            AbyssalDeploymentEligibility.ValidateShipEffects(pose);
        }));
    }

    [Theory]
    [InlineData(4, .8, true)] [InlineData(3, .8, false)] [InlineData(5, .7, true)]
    [InlineData(4, .7, false)] [InlineData(6, .6, true)] [InlineData(5, .6, false)]
    [InlineData(6, .5, false)] [InlineData(0, .95, false)]
    public void Source_security_bands_control_the_suspect_safety_requirement(int tier, double security, bool expected) =>
        Assert.Equal(expected, AbyssalDeploymentEligibility.RequiresSuspect(tier, security));

    [Fact]
    public void A_missing_snapshot_is_never_treated_as_clear_state()
    {
        var request = new AbyssalRunAdmissionRequest(1, 41, 2, AbyssalGameMode.SoloCruiser, null, 30000142, new(10, 20, 30), new(0, 0, 1));
        var pose = Pose(new(false, false, false, false, false, [])) with { Deployment = null };
        AssertError(22, Assert.Throws<MachoWrappedException>(() => AbyssalDeploymentEligibility.ValidateProbe(request, pose)));
    }
}
