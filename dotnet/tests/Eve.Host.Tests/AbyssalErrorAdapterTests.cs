using Eve.Content;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalErrorAdapterTests
{
    [Fact]
    public void Invalid_key_arguments_are_a_tuple_including_the_empty_case()
    {
        foreach (var args in new PyValue[][] { [], [new PyInt(2500)] })
        {
            var error = AbyssalErrorAdapter.ClientError(3, args);
            var expected = MachoErrors.UserErrorPayload("AbyssError",
                [new KeyValuePair<PyValue, PyValue>(new PyStr("errors"),
                    new PyList([new PyTuple([new PyInt(3), new PyTuple(args)])]))]);
            Assert.Equal(expected, error.Error.Payload);
        }
    }

    [Fact]
    public void Fleet_mismatch_uses_the_shipped_Abyss_error_code()
    {
        var error = AbyssalErrorAdapter.FromRule(
            new AbyssalRuleException(AbyssalRuleCode.FleetMismatch, "membership differs"),
            isGateActivation: false);

        var errors = new PyList([
            new PyTuple([new PyInt(25), new PyTuple([])]),
        ]);
        var payload = MachoErrors.UserErrorPayload(
            "AbyssError",
            [new KeyValuePair<PyValue, PyValue>(new PyStr("errors"), errors)]);
        Assert.Equal(payload, error.Error.Payload);
    }


    [Fact]
    public void Admission_conflict_uses_the_reference_custom_notification()
    {
        var error = AbyssalErrorAdapter.FromRule(
            new AbyssalRuleException(AbyssalRuleCode.AdmissionConflict, "run already active"),
            isGateActivation: false);

        var payload = MachoErrors.UserErrorPayload(
            "CustomNotify",
            [new KeyValuePair<PyValue, PyValue>(
                new PyStr("notify"),
                new PyStr("An Abyssal run is already active."))]);
        Assert.Equal(payload, error.Error.Payload);
    }

    [Fact]
    public void Denied_target_attempt_is_used_only_for_real_gate_validation()
    {
        var error = AbyssalErrorAdapter.FromRule(
            new AbyssalRuleException(AbyssalRuleCode.GateLocked, "the room is not clear"),
            isGateActivation: true);

        Assert.Equal(
            MachoErrors.UserErrorPayload("DeniedTargetAttemptFailed"),
            error.Error.Payload);
    }

    [Fact]
    public void A_non_gate_rule_error_is_never_converted_to_gate_denial()
    {
        var error = AbyssalErrorAdapter.FromRule(
            new AbyssalRuleException(AbyssalRuleCode.RunExpired, "deadline passed"),
            isGateActivation: true);

        var errors = new PyList([
            new PyTuple([new PyInt(32), new PyTuple([])]),
        ]);
        var payload = MachoErrors.UserErrorPayload(
            "AbyssError",
            [new KeyValuePair<PyValue, PyValue>(new PyStr("errors"), errors)]);
        Assert.Equal(payload, error.Error.Payload);
    }

    [Fact]
    public void High_security_filament_refusal_matches_the_client_notification()
    {
        var error = AbyssalErrorAdapter.SecurityRestricted();

        Assert.Equal(
            MachoErrors.UserErrorPayload(
                "CustomNotify",
                [new KeyValuePair<PyValue, PyValue>(
                    new PyStr("notify"),
                    new PyStr("Only Tranquil (Tier 0) Abyssal filaments can be activated in 0.9 or 1.0 security systems."))]),
            error.Error.Payload);
    }
}
