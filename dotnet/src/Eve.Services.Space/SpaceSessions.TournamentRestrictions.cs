namespace Eve.Services.Space;

public enum TournamentRestriction { Move, Warp, Lock }

public sealed partial class SpaceSessions
{
    // The Host supplies a query of the same durable match and participant rows
    // that own tournament membership and restriction state.
    public Func<long, TournamentRestriction, bool>? TournamentRestricted { get; set; }
}
