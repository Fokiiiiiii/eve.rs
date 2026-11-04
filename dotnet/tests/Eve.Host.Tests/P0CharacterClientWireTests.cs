using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_unbound_character_info_and_random_name_replies_match_the_creation_clients()
    {
        const long characterId = 90_000_201;
        var characters = new Dictionary<long, Character>
        {
            [characterId] = WireCharacter(characterId, 1, "Wire Pilot", 1, 1, 30_000_142),
        };
        var service = CharUnboundWireService(CreationCatalog(), characters);

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = 1);
        var (front, open) = await OpenFrontAsync();

        var rows = Assert.IsType<PyList>(await CallOverHost(
            front, open, 1, "charUnboundMgr", "GetCharacterInfo", Py.Tuple([])));
        var row = Assert.IsType<PyObject>(Assert.Single(rows.Items));
        Assert.Equal(new PyInt(characterId), RpcCoverageWireField(row, "characterID"));
        var characterName = Assert.IsType<PyBytes>(RpcCoverageWireField(row, "characterName"));
        Assert.Equal("Wire Pilot"u8.ToArray(), characterName.Value.ToArray());

        var nameBytes = Assert.IsType<PyBytes>(await CallOverHost(
            front, open, 2, "charUnboundMgr", "GetValidRandomName", Py.Tuple(new PyInt(1))));
        var name = System.Text.Encoding.UTF8.GetString(nameBytes.Value.Span);
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate(name));
        Assert.DoesNotContain(characters.Values, character => character.Name == name);
    }

    [Fact]
    public async Task Char_unbound_creation_returns_the_created_character_id_over_host_wire()
    {
        const long characterId = 90_000_202;
        const long createdCharacterId = 90_000_203;
        var characters = new Dictionary<long, Character>
        {
            [characterId] = WireCharacter(characterId, 1, "Wire Pilot", 1, 1, 30_000_142),
        };
        CreationRequest? received = null;
        var service = CharUnboundWireService(
            CreationCatalog(),
            characters,
            createCharacter: (request, _) =>
            {
                received = request;
                characters[createdCharacterId] = WireCharacter(
                    createdCharacterId, request.AccountId, request.Name, request.Gender, request.BloodlineId, 30_000_142);
                return Task.FromResult<CreationOutcome>(new Created(createdCharacterId, 90_000_204));
            });

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = 1);
        var (front, open) = await OpenFrontAsync();

        var reply = await CallOverHost(
            front,
            open,
            1,
            "charUnboundMgr",
            "CreateCharacterWithDoll",
            Py.Tuple(
                new PyStr("Created Pilot"),
                new PyInt(1),
                new PyInt(2),
                new PyInt(2),
                new PyInt(0),
                PyNone.Instance,
                PyNone.Instance,
                new PyInt(11),
                PyNone.Instance,
                PyNone.Instance));

        Assert.Equal(new PyInt(createdCharacterId), reply);
        Assert.Equal(
            new CreationRequest(1, "Created Pilot", 1, 2, 2, 0, 11, null, null),
            received);
        Assert.Equal("Created Pilot", characters[createdCharacterId].Name);
    }

    [Fact]
    public async Task Char_unbound_identity_edits_return_none_and_update_character_and_session_state()
    {
        const long characterId = 90_000_205;
        var characters = new Dictionary<long, Character>
        {
            [characterId] = WireCharacter(characterId, 1, "Wire Pilot", 1, 1, 30_000_142),
        };
        var service = CharUnboundWireService(CreationCatalog(), characters);
        ClientSession? session = null;

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: opened =>
            {
                opened.UserId = 1;
                opened.CharacterId = characterId;
                session = opened;
            });
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 1, "charUnboundMgr", "UpdateCharacterGender",
            Py.Tuple(new PyLong(characterId), new PyInt(2))));
        Assert.Equal(2, characters[characterId].Gender);
        Assert.Equal(new PyInt(2), session!.Get("genderID"));

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 2, "charUnboundMgr", "UpdateCharacterBloodline",
            Py.Tuple(new PyLong(characterId), new PyInt(2))));
        Assert.Equal(2, characters[characterId].BloodlineId);
        Assert.Equal(new PyInt(2), session.Get("bloodlineID"));
    }

    [Fact]
    public async Task Population_cap_move_returns_none_and_saves_the_offered_system_over_host_wire()
    {
        const long characterId = 90_000_211;
        const long occupyingCharacterId = 90_000_212;
        const int homeSystemId = 30_000_142;
        const int firstAlternativeId = 30_000_143;
        const int secondAlternativeId = 30_000_144;
        var character = WireCharacter(characterId, 1, "Moving Pilot", 1, 1, homeSystemId);
        var populationSessions = new CharacterSessions();
        var population = new SystemPopulation(
            populationSessions,
            new SolarSystemCatalog([
                new SolarSystemInfo(homeSystemId, "Home", 20_000_020, 10_000_002, 0.5),
                new SolarSystemInfo(firstAlternativeId, "Alternative One", 20_000_020, 10_000_002, 0.5),
                new SolarSystemInfo(secondAlternativeId, "Alternative Two", 20_000_020, 10_000_002, 0.5),
            ]),
            capacity: 1);
        var service = new PopulationCapService(
            characters: null!,
            population: population,
            readCharacterById: (id, _) => Task.FromResult<Character?>(id == characterId ? character : null),
            writeCharacterSolarSystem: (id, systemId, constellationId, regionId, _) =>
            {
                character = character with
                {
                    SolarSystemId = systemId,
                    ConstellationId = constellationId,
                    RegionId = regionId,
                };
                return Task.CompletedTask;
            });

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.UserId = 1;
                session.CharacterId = occupyingCharacterId;
                session.Set("solarsystemid2", new PyInt(homeSystemId));
                populationSessions.Enter(occupyingCharacterId, session);
            });
        var (front, open) = await OpenFrontAsync();

        async Task<PyValue> BoundCall(uint callId, string method, PyValue args)
        {
            var bindArgs = Py.Tuple(
                Py.Tuple(new PyLong(characterId), new PyInt(1)),
                Py.Tuple(new PyStr(method), args, ClientKwargs()));
            var binding = Assert.IsType<PyTuple>(await CallOverHost(
                front, open, callId, "populationCap", "MachoBindObject", bindArgs));
            return binding.Items[1];
        }

        var slots = Assert.IsType<PyDict>(await BoundCall(
            1, "GetCharacterLoadSlot", Py.Tuple(new PyLong(characterId))));
        Assert.Equal(2, slots.Entries.Count);
        var target = Assert.IsType<PyInt>(slots.Entries[0].Key).Value;
        var slotKey = Assert.IsType<PyBytes>(slots.Entries[0].Value);

        Assert.Same(PyNone.Instance, await BoundCall(
            2,
            "MoveCharacterToNewSystem",
            Py.Tuple(new PyLong(characterId), new PyInt(target), slotKey)));
        Assert.Equal(target, character.SolarSystemId);
        Assert.Equal(20_000_020, character.ConstellationId);
        Assert.Equal(10_000_002, character.RegionId);
    }

    private static CharUnboundMgrService CharUnboundWireService(
        CreationCatalog catalog,
        Dictionary<long, Character> characters,
        Func<CreationRequest, CancellationToken, Task<CreationOutcome>>? createCharacter = null) =>
        new(
            accounts: null!,
            characters: null!,
            items: null!,
            training: null!,
            wallets: null!,
            creation: null!,
            catalog: catalog,
            online: null!,
            entryHooks: [],
            random: new Random(1),
            readCharactersForAccount: (account, _) => Task.FromResult<IReadOnlyList<Character>>(
                characters.Values.Where(character => character.AccountId == account).OrderBy(character => character.Id).ToArray()),
            readCharacterById: (id, _) => Task.FromResult<Character?>(characters.GetValueOrDefault(id)),
            isCharacterNameTaken: (name, _) => Task.FromResult(characters.Values.Any(character =>
                StringComparer.OrdinalIgnoreCase.Equals(character.Name, name))),
            writeCharacterGender: (id, gender, _) =>
            {
                characters[id] = characters[id] with { Gender = gender };
                return Task.CompletedTask;
            },
            writeCharacterBloodline: (id, bloodlineId, raceId, typeId, _) =>
            {
                characters[id] = characters[id] with
                {
                    BloodlineId = bloodlineId,
                    RaceId = raceId,
                    TypeId = typeId,
                };
                return Task.CompletedTask;
            },
            createCharacter: createCharacter);

    private static CreationCatalog CreationCatalog() => new(
        [new Race(1, 601, "Ibis")],
        [new Bloodline(1, 1), new Bloodline(2, 1)],
        [new StarterLocation(1_000_006, null, 60_000_610, 30_002_775, 20_000_406, 10_000_033)]);

    private static Character WireCharacter(
        long id, int accountId, string name, int gender, int bloodlineId, int solarSystemId) =>
        new(
            id, accountId, name, gender, 1, bloodlineId, 1, 35, 1373, 1_000_009,
            null, null, 0, 0, solarSystemId, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
}
