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
    /// <summary><c>ReportSlashCommandUsage(command)</c>: the client says a command was used; nothing is kept.</summary>
    [Method("ReportSlashCommandUsage")]
    public ValueTask<PyValue> ReportSlashCommandUsage(ServiceCall call) => ValueTask.FromResult<PyValue>(PyNone.Instance);

    /// <summary><c>SlashCmd(command)</c>: runs the command and answers with its message.</summary>
    [Method("SlashCmd")]
    public async ValueTask<PyValue> SlashCmd(ServiceCall call)
    {
        var text = (JsSemantics.StringValue(call.Arg(0)) ?? "").Trim();
        var space = text.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();
        var message = name switch
        {
            "/dmg" => await health.DamageAsync(call.Session, argument, call.Cancellation),
            "/heal" => await health.HealAsync(call.Session, call.Cancellation),
            "/deathtest" when death is not null => await death.RunAsync(call.Session, argument, call.Cancellation),
            "/suicide" when suicide is not null => await suicide.RunAsync(call.Session, call.Cancellation),
            "/npc" when npcs is not null => await npcs.RunAsync(call.Session, argument, call.Cancellation),
            "/giveitem" when items is not null => await items.GiveAsync(call.Session, argument, call.Cancellation),
            "/fit" when items is not null => await items.FitAsync(call.Session, argument, call.Cancellation),
            "/unfit" when items is not null => await items.UnfitAsync(call.Session, argument, call.Cancellation),
            "/giveskill" when skills is not null => await skills.GiveAsync(call.Session, argument, call.Cancellation),
            _ => $"Unknown command: {text}. Use /help.",
        };
        return new PyStr(message);
    }
}
