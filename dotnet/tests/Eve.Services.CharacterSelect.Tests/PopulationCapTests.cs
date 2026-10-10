using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Universe;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>
/// The population cap of the selection screen: the answers the client reads, the system a pilot saves, and the refusal when a
/// system is full. The characters in the game are simulated by sessions entered in the registry (no client is involved).
/// </summary>
public class PopulationCapTests
{
    private const int Home = 30002775;      // the starter system of race 1 in the harness catalog
    private const int Near = 30000001;      // one light year from home
    private const int Nearer = 30000002;    // two light years from home
    private const int Far = 30000003;       // ten light years from home
    private const int Wormhole = 31000001;  // closer than the others, never offered
    private const int Pochven = 30000004;   // closer than the others, never offered (region 10000070)

    private static readonly SolarSystemCatalog Systems = new(
    [
        Sys(Home, 10000002, 20000020, 0),
        Sys(Near, 10000002, 20000021, 1),
        Sys(Nearer, 10000002, 20000021, 2),
        Sys(Far, 10000002, 20000022, 10),
        Sys(Wormhole, 11000001, 21000001, 0.5),
        Sys(Pochven, SystemPopulation.PochvenRegionId, 20000023, 0.25),
    ]);

    private static SolarSystemInfo Sys(int id, int region, int constellation, double lightYears) =>
        new(id, $"System {id}", constellation, region, 0.5, 0, lightYears * SolarSystemCatalog.MetersPerLightYear, 0, 0);

    private static readonly PyValue Doll = new PyDict([new(new PyStr("recorded"), new PyInt(1))]);
    private static readonly PyValue Portrait = new PyDict([new(new PyStr("recorded"), new PyInt(2))]);

    private static async Task<long> CreateAsync(Harness h, ClientSession session, string name)
    {
        var id = await h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr(name), new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(10), Doll, Portrait, new PyInt(35), PyNone.Instance, PyNone.Instance);
        return ((PyInt)id).Value;
    }

    /// <summary>A session that stands for a character in the game in a system (the registry is what the cap counts).</summary>
    private static ClientSession InGame(Harness h, long characterId, int system)
    {
        var session = new ClientSession(new Capture())
        {
            SessionId = 27_000_000_000 + characterId,
            ClientId = 28_000_000 + characterId,
            UserId = 9_000 + characterId,
        };
        session.Set("charid", new PyLong(characterId));
        session.Set("solarsystemid2", new PyInt(system));
        h.Sessions.Enter(characterId, session);
        return session;
    }

    private static async Task<PyValue> CallAsync(PopulationCapService service, ClientSession session, string method, params PyValue[] args) =>
        await ((IService)service).CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static Dictionary<long, string> Slots(PyValue value)
    {
        var dict = Assert.IsType<PyDict>(value);
        return dict.Entries.ToDictionary(entry => ((PyInt)entry.Key).Value, entry => ((PyStr)entry.Value).Value);
    }

    private static async Task<(PopulationCapService Service, ClientSession Session, long Character)> PilotInHomeAsync(Harness h, string name)
    {
        var (session, _) = await h.LoginAsync(name);
        var id = await CreateAsync(h, session, "Pop " + name);
        return (new PopulationCapService(h.Characters, h.Population!), session, id);
    }

    /// <summary>A pilot with no station on record: the only kind the cap may move.</summary>
    private static async Task<(PopulationCapService Service, ClientSession Session, long Character)> UndockedPilotAsync(Harness h, string name)
    {
        var pilot = await PilotInHomeAsync(h, name);
        await h.ExecuteAsync($"UPDATE {{characters}}.characters SET station_id = 0 WHERE id = {pilot.Character}");
        return pilot;
    }

    private static async Task<MachoError> RefusalAsync(Func<Task<PyValue>> call) =>
        (await Assert.ThrowsAsync<MachoWrappedException>(async () => await call())).Error;

    [PgFact]
    public async Task A_system_with_room_is_the_only_entry_and_its_slot_key_is_stable()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (service, session, character) = await PilotInHomeAsync(h, "room");

        var first = Slots(await CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));
        var second = Slots(await CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        Assert.Single(first);
        Assert.True(first.ContainsKey(Home));
        Assert.Equal(36, first[Home].Length);
        Assert.Equal(first, second);
    }

    [PgFact]
    public async Task A_full_home_offers_the_nearest_systems_with_room_and_never_wormhole_space_or_pochven()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (service, session, character) = await UndockedPilotAsync(h, "full");
        InGame(h, 91, Home); InGame(h, 92, Home);     // home is full
        InGame(h, 93, Near); InGame(h, 94, Near);     // Near is full, so it is not offered either

        var offered = Slots(await CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        Assert.Equal(new long[] { Nearer, Far }.Order().ToArray(), offered.Keys.Order().ToArray());
        Assert.DoesNotContain(Wormhole, offered.Keys);
        Assert.DoesNotContain(Pochven, offered.Keys);
    }

    [PgFact]
    public async Task Moving_to_an_offered_system_saves_it_on_the_character_with_its_constellation_and_region()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (service, session, character) = await UndockedPilotAsync(h, "move");
        InGame(h, 91, Home); InGame(h, 92, Home);
        var offered = Slots(await CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        await CallAsync(service, session, "MoveCharacterToNewSystem", new PyInt(character), new PyInt(Nearer), new PyStr(offered[Nearer]));

        var saved = await h.Characters.GetAsync(character);
        Assert.Equal(Nearer, saved!.SolarSystemId);
        Assert.Equal(20000021, saved.ConstellationId);
        Assert.Equal(10000002, saved.RegionId);
    }

    [PgFact]
    public async Task A_move_with_a_wrong_slot_key_or_to_a_full_system_is_refused_and_saves_nothing()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (service, session, character) = await UndockedPilotAsync(h, "refused");
        InGame(h, 91, Home); InGame(h, 92, Home);
        InGame(h, 93, Near); InGame(h, 94, Near);
        var offered = Slots(await CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        var wrongKey = await RefusalAsync(() => CallAsync(service, session, "MoveCharacterToNewSystem", new PyInt(character), new PyInt(Nearer), new PyStr("not-a-token")));
        var toFull = await RefusalAsync(() => CallAsync(service, session, "MoveCharacterToNewSystem", new PyInt(character), new PyInt(Near), new PyStr(offered.GetValueOrDefault(Near, "none"))));
        var unknown = await RefusalAsync(() => CallAsync(service, session, "MoveCharacterToNewSystem", new PyInt(character), new PyInt(Far + 1000), new PyStr("x")));

        Assert.True(wrongKey.IsUserError);
        Assert.True(toFull.IsUserError);
        Assert.True(unknown.IsUserError);
        Assert.Equal(Home, (await h.Characters.GetAsync(character))!.SolarSystemId);
    }

    [PgFact]
    public async Task A_single_alternative_is_saved_at_once_because_the_client_loads_a_single_entry_without_asking()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (service, session, character) = await UndockedPilotAsync(h, "single");
        // Home, Near and Far are full, Nearer has room: the only alternative is Nearer.
        InGame(h, 91, Home); InGame(h, 92, Home);
        InGame(h, 93, Near); InGame(h, 94, Near);
        InGame(h, 95, Far); InGame(h, 96, Far);

        var answer = Slots(await CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        Assert.Equal(new long[] { Nearer }, answer.Keys.ToArray());
        Assert.Equal(Nearer, (await h.Characters.GetAsync(character))!.SolarSystemId);
    }

    [PgFact]
    public async Task A_full_system_with_no_alternative_is_refused_with_a_custom_info_error()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 1);
        var (service, session, character) = await PilotInHomeAsync(h, "nowhere");
        InGame(h, 91, Home);
        InGame(h, 92, Near); InGame(h, 93, Nearer); InGame(h, 94, Far);

        var error = await RefusalAsync(() => CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        Assert.True(error.IsUserError);
        Assert.Equal(Home, (await h.Characters.GetAsync(character))!.SolarSystemId);
    }

    [PgFact]
    public async Task A_docked_character_in_a_full_system_is_refused_and_is_not_moved()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 1);
        var (service, session, character) = await PilotInHomeAsync(h, "docked");
        var record = await h.Characters.GetAsync(character);
        Assert.True(record!.StationId > 0, "a new character is docked at its starter station");
        InGame(h, 91, Home);

        var error = await RefusalAsync(() => CallAsync(service, session, "GetCharacterLoadSlot", new PyInt(character)));

        Assert.True(error.IsUserError);
        Assert.Equal(Home, (await h.Characters.GetAsync(character))!.SolarSystemId);
    }

    [PgFact]
    public async Task Selecting_a_character_into_a_full_system_is_refused_before_anything_changes()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 1);
        var (session, _) = await h.LoginAsync("select-full");
        var character = await CreateAsync(h, session, "Select Full");
        InGame(h, 91, Home);

        var error = await RefusalAsync(() => h.CallAsync(session, "SelectCharacterID", new PyInt(character)).AsTask());

        Assert.True(error.IsUserError);
        Assert.False(h.Online.IsOnline(character));
        Assert.Null(session.CharacterId);
    }

    [PgFact]
    public async Task The_bound_call_carries_the_moniker_answer_as_a_substruct_and_the_nested_result()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (service, session, character) = await PilotInHomeAsync(h, "bound");

        var bound = Assert.IsType<PyTuple>(await CallAsync(service, session, "MachoBindObject",
            Py.Tuple(new PyInt(character), new PyStr("groupCharacter")),
            Py.Tuple(new PyStr("GetCharacterLoadSlot"), Py.Tuple(new PyInt(character)), PyNone.Instance)));

        Assert.Equal(2, bound.Items.Count);
        Assert.IsType<PySubStruct>(bound.Items[0]);
        Assert.Single(Slots(bound.Items[1]));
    }

    [PgFact]
    public async Task Another_accounts_character_has_no_load_slots()
    {
        await using var h = await Harness.CreateAsync(systems: Systems, populationCapacity: 2);
        var (owner, _) = await h.LoginAsync("owner");
        var character = await CreateAsync(h, owner, "Owned");
        var (stranger, _) = await h.LoginAsync("stranger");
        var service = new PopulationCapService(h.Characters, h.Population!);

        Assert.Empty(Slots(await CallAsync(service, stranger, "GetCharacterLoadSlot", new PyInt(character))));
    }
}
