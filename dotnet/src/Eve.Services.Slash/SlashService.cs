using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's commands the client sends over RPC (<c>slash</c>): <c>SlashCmd("/name arguments")</c> runs one and answers with what it says. Only the
/// commands the modules that exist can carry out are here: <c>/dmg</c> and <c>/heal</c> (<see cref="ShipHealthCommands"/>) <c>/deathtest</c> (<see cref="DeathTestCommands"/>), <c>/suicide</c> (<see cref="SuicideCommands"/>), <c>/npc</c> (<see cref="NpcCommands"/>) and the item commands <c>/giveitem</c>, <c>/fit</c> and <c>/unfit</c> (<see cref="ItemCommands"/>) and <c>/giveskill</c> (<see cref="SkillCommands"/>); any other is answered as unknown.
/// </summary>
[Service("slash")]
public sealed partial class SlashService(ShipHealthCommands health, DeathTestCommands? death = null, SuicideCommands? suicide = null, NpcCommands? npcs = null, ItemCommands? items = null, SkillCommands? skills = null, IskCommands? isk = null, LoyaltyCommands? loyalty = null, StandingCommands? standings = null, TeleportCommands? teleport = null, AgentCommands? agent = null, FwCommands? fw = null, PiCommands? pi = null, IndustryCommands? industry = null, SpawnCommands? spawn = null, SkillBulkCommands? skillBulk = null, DogmaUpdateCommands? dogmaUpdate = null, KillRightCommands? killRight = null, AsteroidCommands? asteroids = null, SkyhookCommands? skyhook = null, EntityCommands? entity = null)
{
    private readonly Dictionary<string, Func<ServiceCall, string, Task<string>>> _commands =
        Commands(health, death, suicide, npcs, items, skills, isk, loyalty, standings, teleport, agent, fw, pi, industry, spawn, skillBulk, dogmaUpdate, killRight, asteroids, skyhook, entity);

    // Dispatch and discovery share the same registrations; absent providers expose no command.
    private static Dictionary<string, Func<ServiceCall, string, Task<string>>> Commands(
        ShipHealthCommands health, DeathTestCommands? death, SuicideCommands? suicide,
        NpcCommands? npcs, ItemCommands? items, SkillCommands? skills, IskCommands? isk, LoyaltyCommands? loyalty, StandingCommands? standings,
        TeleportCommands? teleport, AgentCommands? agent, FwCommands? fw, PiCommands? pi, IndustryCommands? industry, SpawnCommands? spawn, SkillBulkCommands? skillBulk, DogmaUpdateCommands? dogmaUpdate, KillRightCommands? killRight, AsteroidCommands? asteroids, SkyhookCommands? skyhook, EntityCommands? entity)
    {
        var commands = new Dictionary<string, Func<ServiceCall, string, Task<string>>>(StringComparer.Ordinal)
        {
            ["/dmg"] = (call, argument) => health.DamageAsync(call.Session, argument, call.Cancellation),
            ["/heal"] = (call, _) => health.HealAsync(call.Session, call.Cancellation),
            ["/repair"] = (call, _) => health.HealAsync(call.Session, call.Cancellation),
        };
        if (death is not null) commands["/deathtest"] = (call, argument) => death.RunAsync(call.Session, argument, call.Cancellation);
        if (suicide is not null) commands["/suicide"] = (call, _) => suicide.RunAsync(call.Session, call.Cancellation);
        if (npcs is not null) commands["/npc"] = (call, argument) => npcs.RunAsync(call.Session, argument, call.Cancellation);
        if (items is not null)
        {
            commands["/giveitem"] = (call, argument) => items.GiveAsync(call.Session, argument, call.Cancellation);
            commands["/ship"] = (call, argument) => items.ShipAsync(call.Session, argument, call.Cancellation, "ship");
            commands["/giveme"] = (call, argument) => items.ShipAsync(call.Session, argument, call.Cancellation, "giveme");
            commands["/createitem"] = (call, argument) => items.CreateAsync(call.Session, argument, call.Cancellation);
            commands["/fit"] = (call, argument) => items.FitAsync(call.Session, argument, call.Cancellation);
            commands["/unfit"] = (call, argument) => items.UnfitAsync(call.Session, argument, call.Cancellation);
            commands["/online"] = (call, argument) => items.OnlineAsync(call.Session, argument, call.Cancellation);
            commands["/load"] = (call, argument) => items.LoadAsync(call.Session, argument, call.Cancellation);
            commands["/unload"] = (call, argument) => items.UnloadAsync(call.Session, argument, call.Cancellation);
        }
        if (skills is not null) commands["/giveskill"] = (call, argument) => skills.GiveAsync(call.Session, argument, call.Cancellation);
        if (skills is not null) commands["/removeskill"] = (call, argument) => skills.RemoveAsync(call.Session, argument, call.Cancellation);
        if (entity is not null) commands["/entity"] = (call, argument) => entity.RunAsync(call.Session, argument, call.Cancellation);
        if (skyhook is not null) commands["/skyhook"] = (call, argument) => skyhook.RunAsync(call.Session, argument, call.Cancellation);
        if (asteroids is not null) commands["/asteroid"] = (call, argument) => asteroids.RunAsync(call.Session, argument, call.Cancellation);
        if (killRight is not null) commands["/killright"] = (call, argument) => killRight.RunAsync(call.Session, argument, call.Cancellation);
        if (dogmaUpdate is not null) commands["/dogmaupdate"] = (call, _) => dogmaUpdate.RunAsync(call.Session, call.Cancellation);
        if (skillBulk is not null)
        {
            commands["/givestarterskills"] = (call, argument) => skillBulk.GiveStarterAsync(call.Session, argument, call.Cancellation);
            commands["/randomizeskills"] = (call, _) => skillBulk.RandomizeAsync(call.Session, call.Cancellation);
            commands["/skillscopy"] = (call, argument) => skillBulk.CopyAsync(call.Session, argument, call.Cancellation);
        }
        if (isk is not null)
        {
            commands["/isk"] = (call, _) => isk.WalletAsync(call.Session, call.Cancellation);
            commands["/addisk"] = (call, argument) => isk.GiveAsync(call.Session, argument, call.Cancellation);
            commands["/wallet"] = (call, _) => isk.WalletAsync(call.Session, call.Cancellation);
            commands["/setisk"] = (call, argument) => isk.SetAsync(call.Session, argument, call.Cancellation);
        }
        if (loyalty is not null)
        {
            commands["/lp"] = (call, argument) => loyalty.SummaryAsync(call.Session, argument, call.Cancellation);
            commands["/addlp"] = (call, argument) => loyalty.AddAsync(call.Session, argument, call.Cancellation);
            commands["/setlp"] = (call, argument) => loyalty.SetAsync(call.Session, argument, call.Cancellation);
        }
        if (standings is not null)
        {
            commands["/setstanding"] = (call, argument) => standings.SetAsync(call.Session, argument, call.Cancellation);
            commands["/fullstandings"] = (call, argument) => standings.MaxAgentAsync(call.Session, argument, call.Cancellation);
            commands["/maxagentstandings"] = (call, argument) => standings.MaxAgentAsync(call.Session, argument, call.Cancellation);
        }
        if (teleport is not null)
        {
            commands["/solar"] = (call, argument) => teleport.SolarAsync(call.Session, argument, call.Cancellation);
            commands["/tele"] = (call, argument) => teleport.TeleAsync(call.Session, argument, call.Cancellation);
            commands["/tr"] = (call, argument) => teleport.TrAsync(call.Session, argument, call.Cancellation);
            commands["/warpto"] = (call, argument) => teleport.WarpToAsync(call.Session, argument, call.Cancellation);
        }
        if (agent is not null) commands["/agent"] = (call, argument) => agent.RunAsync(call.Session, argument, call.Cancellation);
        if (fw is not null) commands["/fw"] = (call, argument) => fw.RunAsync(call.Session, argument, call.Cancellation);
        if (pi is not null) commands["/pi"] = (call, argument) => pi.RunAsync(call.Session, argument, call.Cancellation);
        if (industry is not null) commands["/bp"] = (call, argument) => industry.RunAsync(call.Session, argument, call.Cancellation);
        if (spawn is not null)
        {
            commands["/spawn"] = (call, argument) => spawn.SpawnAsync(call.Session, argument, call.Cancellation);
            commands["/spawnn"] = (call, argument) => spawn.SpawnnAsync(call.Session, argument, call.Cancellation);
            commands["/unspawn"] = (call, argument) => spawn.UnspawnAsync(call.Session, argument, call.Cancellation);
            commands["/nuke"] = (call, _) => spawn.NukeAsync(call.Session, call.Cancellation);
        }
        return commands;
    }

    /// <summary>The answer to a session without a GM role: the commands are the game masters' tools only.</summary>
    public const string NotGameMaster = "Command requires a game master role.";

    /// <summary><c>ReportSlashCommandUsage(command)</c>: the client says a command was used; nothing is kept.</summary>
    [Method("ReportSlashCommandUsage")]
    public ValueTask<PyValue> ReportSlashCommandUsage(ServiceCall call) => ValueTask.FromResult<PyValue>(PyNone.Instance);

    /// <summary><c>SlashCmd(command)</c>: runs the command; <c>/</c> returns the V24 command catalog as a UserError.</summary>
    [Method("SlashCmd")]
    public async ValueTask<PyValue> SlashCmd(ServiceCall call)
    {
        if (!GameMasterRoles.Has(call.Session, GameMasterRoles.Staff)) return new PyStr(NotGameMaster);
        var text = (JsSemantics.StringValue(call.Arg(0)) ?? "").Trim();
        if (text == "/")
        {
            var names = _commands.Keys.OrderBy(commandName => commandName, StringComparer.Ordinal).Select(commandName => $"'{commandName[1..]}'");
            throw MachoErrors.ThrowUserError("", ("reason", new PyStr($"Commands: [{string.Join(", ", names)}]")));
        }
        var space = text.IndexOfAny([' ', '\t']);
        // the client sends the same names with and without the slash (the Insider's `heal 5 0`, `warpto 5`)
        var name = "/" + (space < 0 ? text : text[..space]).TrimStart('/').ToLowerInvariant();
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();
        var message = _commands.TryGetValue(name, out var command)
            ? await command(call, argument)
            : $"Unknown command: {text}. Use /help.";
        return new PyStr(message);
    }
}
