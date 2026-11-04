using Eve.Dogma;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/dogmaupdate</c>: the host reads the dogma tables (attributes, effects, types, groups and categories) from the SDE it runs on, and swaps them in while it runs.
/// The client clears its own effect cache when it sends the command; after reloading, the host resends the caller's active ship and fitted-module attributes.
/// </summary>
public sealed class DogmaUpdateCommands(
    DogmaStatic statics,
    Func<ClientSession, long, CancellationToken, Task>? refreshShipAttributes = null,
    Func<CancellationToken, Task<(int Attributes, int Effects)>>? reload = null)
{
    public async Task<string> RunAsync(ClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            var (attributes, effects) = await (reload is null
                ? statics.ReloadAsync(cancellationToken)
                : reload(cancellationToken));
            if (session.CharacterId is { } character && refreshShipAttributes is not null)
                await refreshShipAttributes(session, character, cancellationToken);
            return $"Dogma reloaded from the SDE: {attributes} attributes, {effects} effects.";
        }
        catch (InvalidOperationException error)
        {
            return $"Dogma cannot be reloaded: {error.Message}.";
        }
    }
}
