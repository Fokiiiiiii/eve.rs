using Eve.Dogma;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/dogmaupdate</c>: the host reads the dogma tables (attributes, effects, types, groups and categories) from the SDE it runs on, and swaps them in while it runs.
/// The client clears its own effect cache when it sends the command, so the reply is the server's part: how many attributes and effects the reload read.
/// </summary>
public sealed class DogmaUpdateCommands(DogmaStatic statics)
{
    public async Task<string> RunAsync(ClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            var (attributes, effects) = await statics.ReloadAsync(cancellationToken);
            return $"Dogma reloaded from the SDE: {attributes} attributes, {effects} effects.";
        }
        catch (InvalidOperationException error)
        {
            return $"Dogma cannot be reloaded: {error.Message}.";
        }
    }
}
