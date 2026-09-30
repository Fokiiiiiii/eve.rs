using Eve.Services;

namespace Eve.Services.Slash;

/// <summary><c>/suicide</c>: the pilot's own ship is destroyed, as a shot that took the last of its hull would (the wreck, the items, the capsule). The words the reference answers with are its own.</summary>
public sealed class SuicideCommands(ISpaceDeath space)
{
    public async Task<string> RunAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.GetInt("solarsystemid") is null || session.GetInt("stationid") is not null)
        {
            return "You must be in space before using /suicide.";
        }
        var done = await space.SelfDestructAsync(session, cancellationToken);
        if (done is null)
        {
            return "Self-destruct failed: no destructible ship is in space.";
        }
        var wreck = done.WreckName is { } name ? $"{name} ({done.WreckId})" : "none";
        return $"Self-destructed ship {done.ShipId}. Wreck: {wreck}. Capsule: {done.CapsuleName} ({done.CapsuleId}).";
    }
}
