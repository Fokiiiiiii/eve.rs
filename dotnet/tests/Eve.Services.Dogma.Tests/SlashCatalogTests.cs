using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>The V24 slash client reads UserError.args[1]["reason"], then matches bare command names.</summary>
public class SlashCatalogTests
{
    // Catalog discovery must not touch any store or simulation. Null collaborators are tripwires;
    // the real command handlers still enforce their normal argument/session guards when dispatched.
    private static SlashService Service(bool death = false, bool suicide = false, bool npcs = false, bool items = false, bool skills = false) =>
        new(new ShipHealthCommands(null!, null!, () => 0),
            death ? new DeathTestCommands(null!, null!) : null,
            suicide ? new SuicideCommands(null!) : null,
            npcs ? new NpcCommands(null!) : null,
            items ? new ItemCommands(null!, null!, null!, () => 0) : null,
            skills ? new SkillCommands(null!, null!, null!, null, () => 0) : null);

    private static ClientSession Gm(ClientSession session)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        return session;
    }

    private static async Task<PyValue> CallAsync(SlashService service, string command, ClientSession? session = null) =>
        await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "SlashCmd",
            Args = [new PyStr(command)],
            Session = session ?? Gm(new ClientSession()),
            CallId = new PyInt(1),
        });

    private static async Task<string> CatalogAsync(SlashService service, string command = "/")
    {
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await CallAsync(service, command));
        Assert.Equal(MachoErrorType.WrappedException, error.Error.ErrorCode);
        Assert.Equal(MachoErrors.UserErrorClass, error.Error.ClassName);
        var payload = Assert.IsType<PyObjectEx>(error.Error.Payload);
        Assert.Equal(ObjectExKind.Ex1, payload.Kind);
        var header = Assert.IsType<PyTuple>(payload.Header);
        var arguments = Assert.IsType<PyTuple>(header.Items[1]);
        Assert.Equal(2, arguments.Items.Count);
        Assert.Equal("", Assert.IsType<PyStr>(arguments.Items[0]).Value);
        var values = Assert.IsType<PyDict>(arguments.Items[1]);
        var reason = Assert.Single(values.Entries);
        Assert.Equal("reason", Assert.IsType<PyStr>(reason.Key).Value);
        return Assert.IsType<PyStr>(reason.Value).Value;
    }

    [Theory]
    [InlineData("/")]
    [InlineData(" \t/ ")]
    public async Task Catalog_uses_the_clients_user_error_reason_and_bare_names(string command)
    {
        Assert.Equal("Commands: ['dmg', 'heal', 'repair']", await CatalogAsync(Service(), command));
    }

    [Fact]
    public async Task Catalog_lists_only_commands_with_wired_handlers()
    {
        var service = Service(death: true, suicide: true, npcs: true, items: true, skills: true);
        Assert.Equal("Commands: ['deathtest', 'dmg', 'fit', 'giveitem', 'giveme', 'giveskill', 'heal', 'npc', 'repair', 'ship', 'suicide', 'unfit']",
            await CatalogAsync(service));
    }

    [Theory]
    [InlineData(true, false, false, false, false, "Commands: ['deathtest', 'dmg', 'heal', 'repair']")]
    [InlineData(false, true, false, false, false, "Commands: ['dmg', 'heal', 'repair', 'suicide']")]
    [InlineData(false, false, true, false, false, "Commands: ['dmg', 'heal', 'npc', 'repair']")]
    [InlineData(false, false, false, true, false, "Commands: ['dmg', 'fit', 'giveitem', 'giveme', 'heal', 'repair', 'ship', 'unfit']")]
    [InlineData(false, false, false, false, true, "Commands: ['dmg', 'giveskill', 'heal', 'repair']")]
    public async Task Catalog_follows_each_optional_registration(bool death, bool suicide, bool npcs, bool items, bool skills, string expected)
    {
        Assert.Equal(expected, await CatalogAsync(Service(death, suicide, npcs, items, skills)));
    }

    [Theory]
    [InlineData("/npc")]
    [InlineData("/deathtest")]
    [InlineData("/suicide")]
    [InlineData("/giveitem")]
    [InlineData("/fit")]
    [InlineData("/unfit")]
    [InlineData("/giveskill")]
    [InlineData("/help")]
    [InlineData("/createitem")]
    [InlineData("")]
    public async Task Unwired_and_unimplemented_commands_still_answer_unknown(string command)
    {
        Assert.Equal($"Unknown command: {command}. Use /help.",
            Assert.IsType<PyStr>(await CallAsync(Service(), command)).Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dmg heavy")]
    [InlineData("/giveskill me all 5")]
    public async Task A_session_without_a_game_master_role_is_refused(string command)
    {
        Assert.Equal(SlashService.NotGameMaster, Assert.IsType<PyStr>(await CallAsync(Service(skills: true), command, new ClientSession())).Value);
    }

    [Fact]
    public async Task Registered_commands_keep_the_existing_dispatch_and_self_target_guards()
    {
        var service = Service(skills: true);
        Assert.Equal("Select a character before using /dmg.",
            Assert.IsType<PyStr>(await CallAsync(service, "/DMG Heavy")).Value);
        var session = Gm(new ClientSession { CharacterId = 90000001 });
        Assert.Equal("Character not found: 90000002. Use me, a character ID, or an exact character name.",
            Assert.IsType<PyStr>(await CallAsync(service, "/giveskill 90000002 3300 5", session)).Value);
    }
}
