using Eve.Marshal;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>The words of <c>/skyhook state</c> (the client's skyhook menu) with the write given: each outcome has its reply, and a bad state is the usage.</summary>
public class SkyhookCommandTests
{
    [Fact]
    public async Task Each_outcome_has_its_reply_and_a_state_outside_the_client_set_is_the_usage()
    {
        var outcome = SkyhookStateOutcome.Changed;
        var commands = new SkyhookCommands((id, state, token) => Task.FromResult(outcome));
        var session = Rig.Session(7);

        Assert.Equal("Set skyhook 1234 to hull_vulnerable(214).", await commands.RunAsync(session, "state 1234 214", CancellationToken.None));
        outcome = SkyhookStateOutcome.Unchanged;
        Assert.Equal("Skyhook 1234 is already hull_vulnerable(214).", await commands.RunAsync(session, "state 1234 214", CancellationToken.None));
        outcome = SkyhookStateOutcome.NotFound;
        Assert.Equal("Skyhook not found: 1234.", await commands.RunAsync(session, "state 1234 214", CancellationToken.None));
        Assert.Equal(SkyhookCommands.Usage, await commands.RunAsync(session, "state 1234 999", CancellationToken.None));
        Assert.Equal(SkyhookCommands.Usage, await commands.RunAsync(session, "state", CancellationToken.None));
    }
}
