using Eve.Content;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>Maps Abyss Domain and admission failures to the shipped client's error envelope.</summary>
public static class AbyssalErrorAdapter
{
    public static MachoWrappedException FromRule(AbyssalRuleException error, bool isGateActivation)
    {
        ArgumentNullException.ThrowIfNull(error);
        Console.Error.WriteLine($"eve-host: abyssal.rule_refused code={(int)error.Code} gate={(isGateActivation ? 1 : 0)}");

        if (error.Code == AbyssalRuleCode.AdmissionConflict)
            return MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("An Abyssal run is already active.")));

        if (isGateActivation && error.Code is AbyssalRuleCode.GateLocked or AbyssalRuleCode.InvalidGate)
            return MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");

        var code = error.Code switch
        {
            AbyssalRuleCode.InvalidGameMode => 3,              // abyss.error.Error.INVALID_KEY_TYPE
            AbyssalRuleCode.TraceExpired => 30,                // ENTRANCE_MISSING
            AbyssalRuleCode.RunExpired => 32,                  // POCKET_COLLAPSING
            AbyssalRuleCode.RunNotActive => 30,                // ENTRANCE_MISSING
            AbyssalRuleCode.ParticipantLimitExceeded => 27,    // MAX_ENTERED
            AbyssalRuleCode.FleetMismatch => 25,               // SHIP_NOT_IN_SAME_FLEET
            AbyssalRuleCode.InvalidShipClass => 5,             // INVALID_SHIP_TYPE
            AbyssalRuleCode.ParticipantNotFound => 30,         // ENTRANCE_MISSING
            AbyssalRuleCode.InvalidGate => 30,                 // ENTRANCE_MISSING
            AbyssalRuleCode.InvalidRequest or
                AbyssalRuleCode.GateLocked or
                AbyssalRuleCode.RoomSpawnMismatch or
                AbyssalRuleCode.EntityNotInRoom or
                AbyssalRuleCode.ReturnNotRequested => 22,      // UNKNOWN_ERROR
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Code, "Unhandled Abyssal validation code."),
        };
        return ClientError(code);
    }

    public static MachoWrappedException SecurityRestricted() =>
        MachoErrors.ThrowUserError(
            "CustomNotify",
            ("notify", new PyStr("Only Tranquil (Tier 0) Abyssal filaments can be activated in 0.9 or 1.0 security systems."))
        );

    public static MachoWrappedException ClientError(int clientErrorCode, params PyValue[] args)
    {
        if (clientErrorCode is < 1 or > 37)
            throw new ArgumentOutOfRangeException(nameof(clientErrorCode));
        var errors = new PyList([
            new PyTuple([new PyInt(clientErrorCode), new PyTuple(args)]),
        ]);
        return MachoErrors.ThrowUserError("AbyssError", ("errors", errors));
    }
}
