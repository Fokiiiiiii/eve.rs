using System.Globalization;
using Eve.Services;

namespace Eve.Services.Slash;

/// <summary>A parsed V24 GM menu action for an Upwell structure or its moon extraction.</summary>
public sealed record StructureGmRequest(string Command, long StructureId, string Value = "", long DurationMs = 0, long ActorCharacterId = 0);

/// <summary>
/// The GM actions the V24 client sends from <c>structuremenu.py</c> and <c>moonmining/scheduling.py</c>.
/// SlashService applies its existing Staff-role gate before any handler can run.
/// </summary>
public sealed class StructureGmCommands(Func<StructureGmRequest, CancellationToken, Task<string>> execute)
{
    public const string StructureUsage = "Usage: /structure <state|timer|deploytimer|unanchor|abandontimer> <structureID> [value]";
    public const string ExtractionUsage = "Usage: /extraction advance <mid|stop|ready|decay> [structureID]";

    public Task<string> RunStructureAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var parts = Tokens(argument);
        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var structureId) || structureId <= 0)
            return Task.FromResult(StructureUsage);

        var command = parts[0].ToLowerInvariant();
        if (command == "unanchor")
        {
            if (parts.Length == 2)
                return execute(new StructureGmRequest("unanchor", structureId, "start", ActorCharacterId: session.CharacterId ?? 0), cancellationToken);
            if (parts.Length != 3)
                return Task.FromResult("Usage: /structure unanchor <structureID> [cancel|seconds]");
            if (parts[2].Equals("cancel", StringComparison.OrdinalIgnoreCase))
                return execute(new StructureGmRequest("unanchor", structureId, "cancel", ActorCharacterId: session.CharacterId ?? 0), cancellationToken);
            return TryDuration(parts[2], out var unanchorMs)
                ? execute(new StructureGmRequest("unanchor", structureId, "timer", unanchorMs, session.CharacterId ?? 0), cancellationToken)
                : Task.FromResult("Usage: /structure unanchor <structureID> [cancel|seconds]");
        }

        if (parts.Length != 3)
            return Task.FromResult(command is "state" or "timer" or "deploytimer" or "abandontimer" ? StructureUsage : $"Unknown /structure subcommand '{command}'. {StructureUsage}");

        if (command == "state")
            return int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var state)
                ? execute(new StructureGmRequest(command, structureId, state.ToString(CultureInfo.InvariantCulture), ActorCharacterId: session.CharacterId ?? 0), cancellationToken)
                : Task.FromResult("Usage: /structure state <structureID> <stateID>");

        if (command is "timer" or "deploytimer" or "abandontimer")
            return TryDuration(parts[2], out var durationMs)
                ? execute(new StructureGmRequest(command, structureId, DurationMs: durationMs, ActorCharacterId: session.CharacterId ?? 0), cancellationToken)
                : Task.FromResult($"Usage: /structure {command} <structureID> <seconds>");

        return Task.FromResult($"Unknown /structure subcommand '{command}'. {StructureUsage}");
    }

    public Task<string> RunExtractionAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var parts = Tokens(argument);
        if (parts.Length is < 2 or > 3 || !parts[0].Equals("advance", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ExtractionUsage);

        var structureId = parts.Length == 3
            ? long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
            : session.GetInt("structureid") ?? session.GetInt("structureID") ?? session.GetInt("shipid") ?? 0;
        if (structureId <= 0)
            return Task.FromResult($"Structure not found. {ExtractionUsage}");
        var phase = parts[1].ToLowerInvariant();
        return phase is "mid" or "stop" or "ready" or "decay"
            ? execute(new StructureGmRequest("advance", structureId, phase, ActorCharacterId: session.CharacterId ?? 0), cancellationToken)
            : Task.FromResult(ExtractionUsage);
    }

    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryDuration(string token, out long milliseconds)
    {
        milliseconds = 0;
        if (!decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > long.MaxValue / 1000m)
            return false;
        milliseconds = (long)Math.Round(seconds * 1000m, 0, MidpointRounding.AwayFromZero);
        return true;
    }
}
