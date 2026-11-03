namespace Eve.Space;

public enum SimTournamentMetricsStatus : byte { Success = 0, Unavailable = 1, Error = 2 }
public enum SimTournamentMetricsPilotState : byte { Live = 0, Destroyed = 1 }
public enum SimTournamentMetricsHeat : byte { Unknown = 0, Off = 1, On = 2 }
public enum SimTournamentMetricsEhp : byte { Unknown = 0, Finite = 1, Infinite = 2 }
public enum SimTournamentCompanionKind : byte { Drone = 0, Fighter = 1 }
public sealed record SimTournamentMetricsPilot(long CharacterId, long ShipId, byte Team);
public sealed record SimTournamentMetricsFitted(long ItemId, int TypeId, int Flag, SimTournamentMetricsHeat Heat);
public sealed record SimTournamentMetricsRunning(long ItemId, int EffectId, long TargetId, int? ChargeTypeId, long? Rounds);
public sealed record SimTournamentMetricsCompanion(long ShipId, int TypeId, long ControllerShipId, int Activity, SimTournamentCompanionKind Kind, int? Members = null);
public sealed record SimTournamentMetricsPilotSample(SimTournamentMetricsPilot Pilot, IReadOnlyList<SimTournamentMetricsFitted> Fitted,
    IReadOnlyList<SimTournamentMetricsRunning> Running, IReadOnlyList<SimTournamentMetricsCompanion> Companions, SimTournamentMetricsEhp EhpStatus, double? Ehp, SimTournamentMetricsPilotState State = SimTournamentMetricsPilotState.Live);
public sealed record SimTournamentMetricsSample(long SampleAt, IReadOnlyList<double>? AppliedDamage, IReadOnlyList<SimTournamentMetricsPilotSample> Pilots);
public sealed record SimRegisterTournamentMetrics(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster) : SimMessage;
public sealed record SimRegisterTournamentMetricsReply(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster, SimTournamentMetricsStatus Status, string Error) : SimMessage;
public sealed record SimReadTournamentMetrics(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster) : SimMessage;
public sealed record SimReadTournamentMetricsReply(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster, SimTournamentMetricsStatus Status, string Error, SimTournamentMetricsSample? Sample) : SimMessage;
public sealed record SimReadTournamentMetricsV2(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster) : SimMessage;
public sealed record SimReadTournamentMetricsV2Reply(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster, SimTournamentMetricsStatus Status, string Error, SimTournamentMetricsSample? Sample) : SimMessage;
public sealed record SimReleaseTournamentMetrics(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster) : SimMessage;
public sealed record SimReleaseTournamentMetricsReply(ulong RequestId, SimSceneKey Scene, long MatchId, long Generation, long Start, IReadOnlyList<SimTournamentMetricsPilot> Roster, SimTournamentMetricsStatus Status, string Error) : SimMessage;
