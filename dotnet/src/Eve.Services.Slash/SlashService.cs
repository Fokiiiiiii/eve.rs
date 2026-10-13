using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's commands the client sends over RPC (<c>slash</c>): <c>SlashCmd("/name arguments")</c> runs one and answers with what it says. Only the
/// commands the modules that exist can carry out are here: <c>/dmg</c> and <c>/heal</c> (<see cref="ShipHealthCommands"/>) <c>/deathtest</c> (<see cref="DeathTestCommands"/>), <c>/suicide</c> (<see cref="SuicideCommands"/>), <c>/npc</c> (<see cref="NpcCommands"/>) and the item commands <c>/giveitem</c>, <c>/fit</c> and <c>/unfit</c> (<see cref="ItemCommands"/>) and <c>/giveskill</c> (<see cref="SkillCommands"/>); any other is answered as unknown.
/// </summary>
[Service("slash")]
public sealed partial class SlashService(ShipHealthCommands health, DeathTestCommands? death = null, SuicideCommands? suicide = null, NpcCommands? npcs = null, ItemCommands? items = null, SkillCommands? skills = null)
{
    private readonly Dictionary<string, Func<ServiceCall, string, Task<string>>> _commands =
        Commands(health, death, suicide, npcs, items, skills);

    // Dispatch and discovery share the same registrations; absent providers expose no command.
    private static Dictionary<string, Func<ServiceCall, string, Task<string>>> Commands(
        ShipHealthCommands health, DeathTestCommands? death, SuicideCommands? suicide,
        NpcCommands? npcs, ItemCommands? items, SkillCommands? skills)
    {
        var commands = new Dictionary<string, Func<ServiceCall, string, Task<string>>>(StringComparer.Ordinal)
        {
            ["/dmg"] = (call, argument) => health.DamageAsync(call.Session, argument, call.Cancellation),
            ["/heal"] = (call, _) => health.HealAsync(call.Session, call.Cancellation),
        };
        if (death is not null) commands["/deathtest"] = (call, argument) => death.RunAsync(call.Session, argument, call.Cancellation);
        if (suicide is not null) commands["/suicide"] = (call, _) => suicide.RunAsync(call.Session, call.Cancellation);
        if (npcs is not null) commands["/npc"] = (call, argument) => npcs.RunAsync(call.Session, argument, call.Cancellation);
        if (items is not null)
        {
            commands["/giveitem"] = (call, argument) => items.GiveAsync(call.Session, argument, call.Cancellation);
            commands["/fit"] = (call, argument) => items.FitAsync(call.Session, argument, call.Cancellation);
            commands["/unfit"] = (call, argument) => items.UnfitAsync(call.Session, argument, call.Cancellation);
        }
        if (skills is not null) commands["/giveskill"] = (call, argument) => skills.GiveAsync(call.Session, argument, call.Cancellation);
        return commands;
    }

    /// <summary><c>ReportSlashCommandUsage(command)</c>: the client says a command was used; nothing is kept.</summary>
    [Method("ReportSlashCommandUsage")]
    public ValueTask<PyValue> ReportSlashCommandUsage(ServiceCall call) => ValueTask.FromResult<PyValue>(PyNone.Instance);

    /// <summary><c>SlashCmd(command)</c>: runs the command; <c>/</c> returns the V24 command catalog as a UserError.</summary>
    [Method("SlashCmd")]
    public async ValueTask<PyValue> SlashCmd(ServiceCall call)
    {
        var text = (JsSemantics.StringValue(call.Arg(0)) ?? "").Trim();
        if (text == "/")
        {
            var names = _commands.Keys.OrderBy(commandName => commandName, StringComparer.Ordinal).Select(commandName => $"'{commandName[1..]}'");
            throw MachoErrors.ThrowUserError("", ("reason", new PyStr($"Commands: [{string.Join(", ", names)}]")));
        }
        var space = text.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();
        var message = _commands.TryGetValue(name, out var command)
            ? await command(call, argument)
            : $"Unknown command: {text}. Use /help.";
        return new PyStr(message);
    }
}
