using System.Globalization;

namespace Eve.Services.Slash;

/// <summary>What a skyhook state change did: the skyhook is not there, it was in that state already, or it is set now.</summary>
public enum SkyhookStateOutcome
{
    NotFound,
    Unchanged,
    Changed,
}

/// <summary>
/// <c>/skyhook state &lt;skyhookID&gt; &lt;state&gt;</c>, sent by the client's skyhook menu (<c>GMSetSkyhookState</c>). The states are the client's
/// <c>orbitalSkyhook/const.py</c> states: 0 unknown, 210 shield vulnerable, 211 armor reinforce, 212 armor vulnerable, 213 hull reinforce, 214 hull vulnerable.
/// </summary>
public sealed class SkyhookCommands(Func<long, int, CancellationToken, Task<SkyhookStateOutcome>> setState)
{
    public const string Usage = "Usage: /skyhook state <skyhookID> <0|210|211|212|213|214>";

    private static readonly Dictionary<int, string> StateNames = new()
    {
        [0] = "unknown",
        [210] = "shield_vulnerable",
        [211] = "armor_reinforce",
        [212] = "armor_vulnerable",
        [213] = "hull_reinforce",
        [214] = "hull_vulnerable",
    };

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 3 || !tokens[0].Equals("state", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var skyhookId) || skyhookId <= 0
            || !int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var state) || !StateNames.ContainsKey(state))
            return Usage;
        var name = $"{StateNames[state]}({state})";
        return await setState(skyhookId, state, cancellationToken) switch
        {
            SkyhookStateOutcome.NotFound => $"Skyhook not found: {skyhookId}.",
            SkyhookStateOutcome.Unchanged => $"Skyhook {skyhookId} is already {name}.",
            _ => $"Set skyhook {skyhookId} to {name}.",
        };
    }
}
