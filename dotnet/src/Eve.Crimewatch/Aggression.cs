using Eve.Universe;

namespace Eve.Crimewatch;

/// <summary>An attack on the ship of another pilot, as the host reports it to crimewatch.</summary>
/// <param name="Attacker">The character that started the module.</param>
/// <param name="Target">The character that flies the ship attacked.</param>
/// <param name="TargetShipId">The ship attacked (a pilot that lost security status for a ship loses no more for it for a minute).</param>
/// <param name="SolarSystemId">Where it happened.</param>
/// <param name="Security">The security status of that system (as the game has it, not the class).</param>
/// <param name="Where">The security class of that system.</param>
/// <param name="TargetIsCapsule">Whether the ship attacked is a capsule.</param>
public sealed record AggressionRequest(long Attacker, long Target, long TargetShipId, long SolarSystemId, double Security, SecurityClass Where, bool TargetIsCapsule);

/// <summary>What an attack on another pilot's ship was, to the attacker.</summary>
public enum AggressionReason
{
    /// <summary>The law allows it (a war, an engagement, the same corporation where that is allowed): only the timers run.</summary>
    Legal,

    /// <summary>No crime where it happened (null security): only the timers run.</summary>
    NoCrime,

    /// <summary>The target is a criminal or a suspect already: anyone may shoot it, only the timers run.</summary>
    TargetFlagged,

    /// <summary>A crime of a suspect: shooting a ship in low security.</summary>
    Suspect,

    /// <summary>A crime of a criminal: shooting a ship in high security, or a capsule in low security. CONCORD answers it in high security, the security status falls and the target gets a kill right.</summary>
    Criminal,
}

/// <summary>What <see cref="Aggression.Record"/> made of the attacker, and why.</summary>
public sealed record AggressionOutcome(CombatState Attacker, AggressionReason Reason)
{
    public bool IsCriminalOffense => Reason == AggressionReason.Criminal;
}

/// <summary>
/// What an attack does to the attacker: weapons and PvP timers run in every system.
/// The flag depends on security class and target state; the caller applies any
/// security status, kill-right and CONCORD consequences.
/// </summary>
public static class Aggression
{
    /// <summary>The attacker after it attacked <paramref name="target"/> at <paramref name="nowMs"/> in a system of this class, and what the attack was.</summary>
    /// <param name="attacker">What crimewatch keeps for the attacker.</param>
    /// <param name="target">What crimewatch keeps for the pilot it attacked.</param>
    /// <param name="where">The security class of the system.</param>
    /// <param name="targetIsCapsule">Whether the target's ship is a capsule (a crime in low security too).</param>
    /// <param name="legal">Whether the law allows the attack (a war, a limited engagement, the same corporation).</param>
    public static AggressionOutcome Record(CombatState attacker, CombatState target, SecurityClass where, long solarSystemId, bool targetIsCapsule, bool legal, long nowMs) =>
        Record(attacker, target, where, solarSystemId, targetIsCapsule,
            new PolicyFacts(AttackerIsPlayer: true, TargetIsPlayerProperty: true, OtherLegalPermission: legal), nowMs);

    public static AggressionOutcome Record(CombatState attacker, CombatState target, SecurityClass where, long solarSystemId, bool targetIsCapsule, PolicyFacts facts, long nowMs)
    {
        var timed = attacker with
        {
            WeaponsUntilMs = Math.Max(attacker.WeaponsUntilMs, nowMs + CombatState.WeaponsDurationMs),
            PvpUntilMs = Math.Max(attacker.PvpUntilMs, nowMs + CombatState.PvpDurationMs),
            SolarSystemId = solarSystemId,
        };
        facts = facts with { TargetIsCapsule = targetIsCapsule, TargetFlag = target.FlagAt(nowMs) };
        var decision = PolicyEngine.Evaluate(new PolicyRequest(PolicyAction.Attack, where, facts, SafetyLevel.None));
        if (decision.Lawful)
        {
            return new AggressionOutcome(timed,
                target.FlagAt(nowMs) == Flag.None ? AggressionReason.Legal : AggressionReason.TargetFlagged);
        }
        if (decision.Consequence == Flag.None)
        {
            return new AggressionOutcome(timed, AggressionReason.NoCrime);
        }
        return new AggressionOutcome(timed.Flagged(decision.Consequence, nowMs),
            decision.Consequence == Flag.Criminal ? AggressionReason.Criminal : AggressionReason.Suspect);
    }

    public static AggressionOutcome Assist(
        CombatState assistant,
        CombatState recipient,
        SecurityClass where,
        long solarSystemId,
        PolicyFacts facts,
        long nowMs)
    {
        facts = facts with { TargetFlag = recipient.FlagAt(nowMs) };
        var decision = PolicyEngine.Evaluate(new PolicyRequest(PolicyAction.RemoteAssistance, where, facts, SafetyLevel.None));
        var timed = assistant with
        {
            WeaponsUntilMs = Math.Max(assistant.WeaponsUntilMs, recipient.WeaponsUntilMs),
            PvpUntilMs = Math.Max(assistant.PvpUntilMs, recipient.PvpUntilMs),
            NpcUntilMs = Math.Max(assistant.NpcUntilMs, recipient.NpcUntilMs),
            DisapprovalUntilMs = Math.Max(assistant.DisapprovalUntilMs, recipient.DisapprovalUntilMs),
            SolarSystemId = solarSystemId,
        };
        if (decision.Consequence == Flag.None)
        {
            return new AggressionOutcome(timed, decision.Lawful ? AggressionReason.Legal : AggressionReason.NoCrime);
        }
        return new AggressionOutcome(timed.Flagged(decision.Consequence, nowMs),
            decision.Consequence == Flag.Criminal ? AggressionReason.Criminal : AggressionReason.Suspect);
    }
}

public enum PolicyAction
{
    Attack,
    RemoteAssistance,
    Dock,
    GateJump,
    CynoJump,
    Warp,
}

public enum PolicyBlockReason
{
    None,
    Safety,
    SessionChangeTimer,
    WeaponsTimer,
    CriminalTimer,
}

public readonly record struct PolicyFacts(
    bool AttackerIsPlayer = false,
    bool TargetIsPlayerProperty = false,
    bool SamePilot = false,
    bool TargetIsCapsule = false,
    Flag TargetFlag = Flag.None,
    bool TargetIsOutlaw = false,
    bool ActiveWar = false,
    bool ActiveLimitedEngagement = false,
    bool LimitedEngagementInterference = false,
    bool FriendlyFireAllowed = false,
    bool WarInterference = false,
    bool HasWeaponsTimer = false,
    bool HasCriminalTimer = false,
    bool ActorIsCapsule = false,
    bool HasSessionChangeTimer = false,
    bool OtherLegalPermission = false);

public readonly record struct PolicyRequest(
    PolicyAction Action,
    SecurityClass SecurityClass,
    PolicyFacts Facts = default,
    int SafetyLevel = SafetyLevel.Full);

public readonly record struct PolicyDecision(
    bool Allowed,
    bool Lawful,
    Flag Consequence,
    int? SafetyThreshold,
    PolicyBlockReason BlockReason);

/// <summary>One source of truth for Crimewatch legality and movement restrictions.</summary>
public static class PolicyEngine
{
    public static PolicyDecision Evaluate(PolicyRequest request) => request.Action switch
    {
        PolicyAction.Attack => Attack(request),
        PolicyAction.RemoteAssistance => RemoteAssistance(request),
        PolicyAction.Dock or PolicyAction.GateJump or PolicyAction.CynoJump or PolicyAction.Warp => Movement(request),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, null),
    };

    private static PolicyDecision Attack(PolicyRequest request)
    {
        var facts = request.Facts;
        var playerCombat = facts.AttackerIsPlayer && facts.TargetIsPlayerProperty && !facts.SamePilot;
        var lawful = !playerCombat || facts.TargetFlag is Flag.Criminal or Flag.Suspect
            || facts.TargetIsOutlaw || facts.ActiveWar || facts.ActiveLimitedEngagement
            || facts.FriendlyFireAllowed || facts.OtherLegalPermission;
        var consequence = lawful ? Flag.None : AggressionFlag(request.SecurityClass, facts.TargetIsCapsule);
        return WithSafety(request.SafetyLevel, lawful, consequence);
    }

    private static PolicyDecision RemoteAssistance(PolicyRequest request)
    {
        var facts = request.Facts;
        var playerAssistance = facts.AttackerIsPlayer && facts.TargetIsPlayerProperty && !facts.SamePilot;
        var interference = (facts.LimitedEngagementInterference || facts.WarInterference)
            && !facts.OtherLegalPermission;
        var criminalTarget = facts.TargetFlag == Flag.Criminal;
        var suspectTarget = facts.TargetFlag == Flag.Suspect && !facts.OtherLegalPermission;
        var outlawTarget = facts.TargetIsOutlaw && !facts.OtherLegalPermission;
        var consequence = !playerAssistance ? Flag.None
            : Security.IsHighSecOrHigher(request.SecurityClass)
                ? (criminalTarget || suspectTarget || outlawTarget || interference ? Flag.Criminal : Flag.None)
            : Security.IsLowSec(request.SecurityClass)
                ? (criminalTarget ? Flag.Criminal : suspectTarget || interference ? Flag.Suspect : Flag.None)
            : Flag.None;
        return WithSafety(request.SafetyLevel, consequence == Flag.None, consequence);
    }

    private static PolicyDecision Movement(PolicyRequest request)
    {
        var facts = request.Facts;
        var action = request.Action;
        var needsSessionChange = action is PolicyAction.Dock or PolicyAction.GateJump or PolicyAction.CynoJump;
        var needsWeaponTimer = needsSessionChange;

        var reason = needsSessionChange && facts.HasSessionChangeTimer ? PolicyBlockReason.SessionChangeTimer
            : needsWeaponTimer && facts.HasWeaponsTimer ? PolicyBlockReason.WeaponsTimer
            : action is PolicyAction.Dock or PolicyAction.GateJump or PolicyAction.CynoJump or PolicyAction.Warp
                && Security.IsHighSecOrHigher(request.SecurityClass)
                && facts.HasCriminalTimer && !facts.ActorIsCapsule
                    ? PolicyBlockReason.CriminalTimer
            : PolicyBlockReason.None;
        return new(reason == PolicyBlockReason.None, true, Flag.None, null, reason);
    }

    private static PolicyDecision WithSafety(int safetyLevel, bool lawful, Flag consequence)
    {
        var threshold = consequence switch
        {
            Flag.Criminal => SafetyLevel.None,
            Flag.Suspect => SafetyLevel.Partial,
            _ => (int?)null,
        };
        var blocked = threshold is { } required && SafetyLevel.Normalize(safetyLevel) > required;
        return new(!blocked, lawful, consequence, threshold, blocked ? PolicyBlockReason.Safety : PolicyBlockReason.None);
    }

    private static Flag AggressionFlag(SecurityClass securityClass, bool targetIsCapsule) =>
        Security.IsHighSecOrHigher(securityClass) ? Flag.Criminal
        : Security.IsLowSec(securityClass) ? (targetIsCapsule ? Flag.Criminal : Flag.Suspect)
        : Flag.None;
}
