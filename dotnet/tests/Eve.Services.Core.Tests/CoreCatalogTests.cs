
namespace Eve.Services.Core.Tests;

/// <summary>The catalog of the core services.</summary>

/// <summary>Wire contracts for the legacy client's lookupSvc owner searches.</summary>
public sealed class LookupSvcTests
{
    [Fact]
    public async Task Character_lookup_returns_ranked_keyvals_aliases_and_an_empty_list()
    {
        var service = CoreServices.CreateLookupService(new TestDirectory
        {
            Characters =
            [
                new(903, "xalpha", CorporationId: 10),
                new(902, "Alpha", CorporationId: 11),
                new(901, "alpha scout", CorporationId: 12, AllianceId: 13),
                new(8, "Alpha", CorporationId: 14),
                new(3, "Alpha", CorporationId: 15),
            ],
        });

        var result = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupCharacters", new Eve.Marshal.PyStr("ALPHA"))));
        Assert.Equal([3L, 8L, 902L, 901L, 903L], result.Items.Select(item => Number(Field(item, "characterID"))));
        Assert.Equal(["Alpha", "Alpha", "Alpha", "alpha scout", "xalpha"], result.Items.Select(item => Text(Field(item, "characterName"))));
        Assert.Equal(
            ["characterID", "characterName", "ownerID", "ownerName", "typeID", "groupID", "corporationID", "allianceID"],
            Keys(result.Items[0]));
        Assert.Equal("util.KeyVal", Assert.IsType<Eve.Marshal.PyStr>(Assert.IsType<Eve.Marshal.PyObject>(result.Items[0]).Name).Value);
        Assert.IsType<Eve.Marshal.PyInt>(Field(result.Items[0], "typeID"));
        Assert.Equal(new Eve.Marshal.PyInt(1), Field(result.Items[0], "groupID"));
        Assert.Equal(new Eve.Marshal.PyInt(0), Field(result.Items[0], "allianceID"));

        var evePlayerAlias = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupEvePlayerCharacters", new Eve.Marshal.PyStr("alpha"))));
        Assert.Equal(result, evePlayerAlias);
        var playerAlias = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupPlayerCharacters", new Eve.Marshal.PyStr("alpha"), new Eve.Marshal.PyInt(1))));
        Assert.Equal([3L, 8L, 902L], playerAlias.Items.Select(item => Number(Field(item, "ownerID"))));

        var noMatch = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupCharacters", new Eve.Marshal.PyStr("missing"))));
        Assert.Empty(noMatch.Items);
        Assert.True(service.HasMethod("LookupCharacters"));
        Assert.True(service.HasMethod("LookupEvePlayerCharacters"));
        Assert.True(service.HasMethod("LookupPlayerCharacters"));
    }

    [Fact]
    public async Task Corporation_lookup_returns_the_EveJS_row_shape_and_order()
    {
        var service = CoreServices.CreateLookupService(new TestDirectory
        {
            Corporations =
            [
                new(301, "Xenon Works", "XEN"),
                new(302, "Xenon", null, IsNpc: true),
                new(303, "Xenon Systems", "XNS", FactionId: 500),
            ],
        });

        var result = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupCorporations", new Eve.Marshal.PyStr("xenon"))));
        Assert.Equal(["Xenon", "Xenon Systems", "Xenon Works"], result.Items.Select(item => Text(Field(item, "corporationName"))));
        Assert.Equal(
            ["corporationID", "corporationName", "ownerID", "ownerName", "typeID", "groupID", "tickerName", "factionID", "isNPC"],
            Keys(result.Items[0]));
        Assert.Equal(new Eve.Marshal.PyInt(302), Field(result.Items[0], "corporationID"));
        Assert.Equal(new Eve.Marshal.PyInt(2), Field(result.Items[0], "typeID"));
        Assert.Equal(new Eve.Marshal.PyInt(2), Field(result.Items[0], "groupID"));
        Assert.Equal(Eve.Marshal.PyNone.Instance, Field(result.Items[0], "tickerName"));
        Assert.Equal(new Eve.Marshal.PyInt(0), Field(result.Items[0], "factionID"));
        Assert.Equal(Eve.Marshal.PyBool.True, Field(result.Items[0], "isNPC"));
        Assert.Equal(new Eve.Marshal.PyInt(500), Field(result.Items[1], "factionID"));

        var noMatch = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupCorporations", new Eve.Marshal.PyStr("unknown"))));
        Assert.Empty(noMatch.Items);
    }

    [Fact]
    public async Task Owner_search_variants_include_the_expected_character_corporation_alliance_and_faction_rows()
    {
        var service = CoreServices.CreateLookupService(new TestDirectory
        {
            Characters = [new(100, "Shared", CorporationId: 200, AllianceId: 300)],
            Corporations =
            [
                new(150, "Shared", "NPC", IsNpc: true),
                new(200, "Shared", "PLY"),
            ],
            Alliances = [new(300, "Shared", "SHR")],
            Factions = [new(400, "Shared")],
        });

        var owners = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupOwners", new Eve.Marshal.PyStr("Shared"), new Eve.Marshal.PyInt(2))));
        Assert.Equal([100L, 150L, 200L, 300L, 400L], owners.Items.Select(item => Number(Field(item, "ownerID"))));
        Assert.Equal(
            ["ownerID", "ownerName", "typeID", "groupID", "gender", "characterID", "corporationID", "allianceID"],
            Keys(owners.Items[0]));
        Assert.Equal(
            ["ownerID", "ownerName", "typeID", "groupID", "gender", "corporationID", "factionID", "tickerName", "isNPC"],
            Keys(owners.Items[1]));
        Assert.Equal(
            ["ownerID", "ownerName", "typeID", "groupID", "gender", "allianceID", "tickerName", "isNPC"],
            Keys(owners.Items[3]));
        Assert.Equal(
            ["ownerID", "ownerName", "typeID", "groupID", "gender", "factionID"],
            Keys(owners.Items[4]));

        var pcOwners = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupPCOwners", new Eve.Marshal.PyStr("Shared"), new Eve.Marshal.PyInt(2))));
        Assert.Equal([100L, 200L, 300L], pcOwners.Items.Select(item => Number(Field(item, "ownerID"))));

        var nonNpcAccountOwners = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupNoneNPCAccountOwners", new Eve.Marshal.PyStr("Shared"), new Eve.Marshal.PyInt(2))));
        Assert.Equal([100L, 200L], nonNpcAccountOwners.Items.Select(item => Number(Field(item, "ownerID"))));
        Assert.Empty(Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupOwners", new Eve.Marshal.PyStr("absent")))).Items);
    }

    [Fact]
    public async Task Warable_owner_lookup_uses_executor_permit_and_punctuation_insensitive_exact_search()
    {
        var service = CoreServices.CreateLookupService(new TestDirectory
        {
            Corporations =
            [
                new(200, "Elysian-Order", "ELY", AllowWar: false),
                new(201, "Elysian Works", "ELW"),
                new(202, "Elysian NPC", "NPC", IsNpc: true),
                new(203, "Elysian Strict", "ELS", WarPermit: 5),
            ],
            Alliances =
            [
                new(300, "Elysian Union", "EU", ExecutorCorporationId: 200),
                new(301, "Elysian Open", "EO", WarPermit: 1),
            ],
        });

        var partial = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupWarableCorporationsOrAlliances", new Eve.Marshal.PyStr("Elysian"))));
        Assert.Equal([200L, 201L, 202L, 203L, 300L, 301L], partial.Items.Select(item => Number(Field(item, "ownerID"))));
        Assert.Equal([0L, 1L, 0L, 1L, 0L, 1L], partial.Items.Select(item => Number(Field(item, "warPermit"))));
        Assert.Equal(["ownerID", "ownerName", "typeID", "warPermit"], Keys(partial.Items[0]));
        Assert.All(partial.Items.Take(4), item => Assert.Equal(2L, Number(Field(item, "typeID"))));
        Assert.All(partial.Items.Skip(4), item => Assert.Equal(16159L, Number(Field(item, "typeID"))));

        var exact = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Request("LookupWarableCorporationsOrAlliances", new Eve.Marshal.PyStr("Elysian Order"), Eve.Marshal.PyBool.True)));
        Assert.Single(exact.Items);
        Assert.Equal(new Eve.Marshal.PyInt(200), Field(exact.Items[0], "ownerID"));
    }


    [Fact]
    public async Task Legacy_delegate_sources_use_the_same_lookup_service()
    {
        var sources = new LookupSearchSources(
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(601, "Legacy Pilot")]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]));

        var service = CoreServices.CreateLookupService(sources);
        var rows = Assert.IsType<Eve.Marshal.PyList>(
            await service.CallAsync(Request("LookupCharacters", new Eve.Marshal.PyStr("legacy"))));

        Assert.Single(rows.Items);
        Assert.Equal(new Eve.Marshal.PyInt(601), Field(rows.Items[0], "characterID"));
    }

    private static ServiceCall Request(string method, params Eve.Marshal.PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = null!,
    };

    private static Eve.Marshal.PyValue Field(Eve.Marshal.PyValue value, string name)
    {
        var objectValue = Assert.IsType<Eve.Marshal.PyObject>(value);
        var dictionary = Assert.IsType<Eve.Marshal.PyDict>(objectValue.Args);
        return dictionary.Entries.Single(entry => Assert.IsType<Eve.Marshal.PyStr>(entry.Key).Value == name).Value;
    }

    private static string[] Keys(Eve.Marshal.PyValue value)
    {
        var objectValue = Assert.IsType<Eve.Marshal.PyObject>(value);
        var dictionary = Assert.IsType<Eve.Marshal.PyDict>(objectValue.Args);
        return dictionary.Entries.Select(entry => Assert.IsType<Eve.Marshal.PyStr>(entry.Key).Value).ToArray();
    }

    private static string Text(Eve.Marshal.PyValue value) => Assert.IsType<Eve.Marshal.PyStr>(value).Value;
    private static long Number(Eve.Marshal.PyValue value) => Assert.IsType<Eve.Marshal.PyInt>(value).Value;

    private sealed class TestDirectory : ILookupDirectory
    {
        public IReadOnlyList<LookupCharacter> Characters { get; init; } = [];
        public IReadOnlyList<LookupCorporation> Corporations { get; init; } = [];
        public IReadOnlyList<LookupAlliance> Alliances { get; init; } = [];
        public IReadOnlyList<LookupFaction> Factions { get; init; } = [];

        public ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Characters);
        public ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Corporations);
        public ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Alliances);
        public ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Factions);
    }
}

public class CoreCatalogTests
{
    [Fact]
    public void The_catalog_names_the_core_services()
    {
        var created = CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), () => 0);
        var createdNames = created.Select(service => service.Name).Order().ToArray();

        Assert.Equal(
            ["browserLockdownSvc", "calendarMgr", "calendarProxy", "pingService", "subscriptionMgr"],
            createdNames);
        Assert.All(createdNames, name => Assert.Contains(name, CoreServices.Names));
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }
}
