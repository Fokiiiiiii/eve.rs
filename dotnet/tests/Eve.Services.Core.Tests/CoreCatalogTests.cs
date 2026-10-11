
namespace Eve.Services.Core.Tests;

/// <summary>The catalog of the core services.</summary>

/// <summary>Wire contracts for the legacy client's lookupSvc owner searches.</summary>
using Eve.Marshal;

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
    public async Task Faction_lookup_returns_the_reference_owner_rows()
    {
        var service = CoreServices.CreateLookupService(new TestDirectory
        {
            Factions = [new(500001, "Caldari State"), new(500002, "Gallente Federation")],
        });

        Assert.True(service.HasMethod("LookupFactions"));
        var rows = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(
            Request("LookupFactions", new Eve.Marshal.PyStr("Caldari"))));

        var row = Assert.Single(rows.Items);
        Assert.Equal(["factionID", "factionName", "ownerID", "ownerName", "typeID", "groupID"], Keys(row));
        Assert.Equal(500001, Number(Field(row, "factionID")));
        Assert.Equal("Caldari State", Text(Field(row, "factionName")));
        Assert.Equal(30, Number(Field(row, "typeID")));
        Assert.Equal(19, Number(Field(row, "groupID")));
    }

    [Fact]
    public async Task Location_group_lookup_returns_the_reference_keyval_shape_and_known_alias()
    {
        var service = CoreServices.CreateLookupService(new TestDirectory
        {
            Locations =
            [
                new(30000142, "Jita", 5, 5, 30000142, 20000020, 10000002),
                new(30000144, "Perimeter", 5, 5, 30000144, 20000020, 10000002),
            ],
        });
        var search = new Eve.Marshal.PyStr("jita");
        var group = new Eve.Marshal.PyInt(5);

        Assert.True(service.HasMethod("LookupLocationsByGroup"));
        Assert.True(service.HasMethod("LookupKnownLocationsByGroup"));
        var locations = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(
            Request("LookupLocationsByGroup", group, search)));
        Assert.Single(locations.Items);
        Assert.Equal(["itemID", "itemName", "typeID", "groupID", "solarSystemID", "constellationID", "regionID"], Keys(locations.Items[0]));
        Assert.Equal(30000142, Number(Field(locations.Items[0], "itemID")));
        Assert.Equal("Jita", Text(Field(locations.Items[0], "itemName")));

        var known = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(
            Request("LookupKnownLocationsByGroup", group, search)));
        Assert.Equal(locations, known);
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
        public IReadOnlyList<LookupLocation> Locations { get; init; } = [];

        public ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Characters);
        public ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Corporations);
        public ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Alliances);
        public ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Factions);
        public ValueTask<IReadOnlyList<LookupLocation>> LocationsByGroupAsync(int groupId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LookupLocation>>([.. Locations.Where(location => location.GroupId == groupId)]);
    }
}

public sealed class SkyhookResourceServiceTests
{

[Fact]
    public async Task Reads_reject_non_skyhook_static_structure_types()
    {
        var staticStructure = Site() with { TypeId = 35832 };
        var service = Create(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(staticStructure),
            (_, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([]),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(null));

        var result = await service.GetSkyhookAsync(Call(7), staticStructure.SkyhookId);

        Assert.Equal(SkyhookResourceResultCode.NotFound, result.Code);
    }

    [Fact]
    public async Task Activation_and_deactivation_require_owner_role_and_persist_state()
    {
        var stored = Site(active: false);
        var mayManage = false;
        var writes = 0;
        var service = Create(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(stored),
            (_, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([stored]),
            (_, _, permission, _) => ValueTask.FromResult(
                permission == SkyhookResourcePermission.Read || mayManage),
            (_, _, active, _) =>
            {
                writes++;
                stored = stored with { ProductionActive = active };
                return ValueTask.FromResult<SkyhookResourceSnapshot?>(stored);
            });

        var foreignCorporation = await service.ActivateAsync(Call(8), stored.SkyhookId);
        Assert.Equal(SkyhookResourceResultCode.Forbidden, foreignCorporation.Code);
        Assert.Equal(0, writes);

        var ownerWithoutRole = await service.ActivateAsync(Call(7), stored.SkyhookId);
        Assert.Equal(SkyhookResourceResultCode.Forbidden, ownerWithoutRole.Code);
        Assert.Equal(0, writes);

        mayManage = true;
        var activated = await service.ActivateAsync(Call(7), stored.SkyhookId);
        Assert.Equal(SkyhookResourceResultCode.Ok, activated.Code);
        Assert.True(activated.Value!.ProductionActive);

        var deactivated = await service.DeactivateAsync(Call(7), stored.SkyhookId);
        Assert.Equal(SkyhookResourceResultCode.Ok, deactivated.Code);
        Assert.False(deactivated.Value!.ProductionActive);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task Current_reagent_read_uses_authoritative_stock_without_inventing_flat_yield()
    {
        var site = Site(
            vulnerability: new SkyhookTheftVulnerability(true, 1030, 1090),
            reagents:
            [
                new SkyhookReagentResource(
                    TypeId: 81100,
                    SecureStock: 0,
                    InsecureStock: 95),
            ]);
        var service = Create(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(site),
            (_, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([]),
            (_, resource, permission, _) =>
            {
                if (permission == SkyhookResourcePermission.Read)
                    Assert.Equal(site.TheftVulnerability, resource.TheftVulnerability);
                return ValueTask.FromResult(true);
            },
            (_, _, _, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(null));

        var current = await service.GetReagentsInSkyhookNowAsync(Call(7), site.SkyhookId);

        Assert.Equal(SkyhookResourceResultCode.Ok, current.Code);
        Assert.Equal(new SkyhookReagentAmounts(0, 95), current.Value);
    }

    [Fact]
    public async Task Corporation_listing_filters_to_rows_owned_by_the_requesting_corporation()
    {
        var owned = Site(id: 9001, corporationId: 7);
        var otherCorporation = Site(id: 9002, corporationId: 8);
        var service = Create(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(null),
            (_, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([otherCorporation, owned]),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(null));

        var result = await service.GetAllByCorporationAsync(Call(7), 7);

        Assert.Equal(SkyhookResourceResultCode.Ok, result.Code);
        Assert.Equal(owned, Assert.Single(result.Value!));
    }

    [Fact]
    public async Task Workforce_read_distinguishes_known_empty_workforce_from_missing_skyhook()
    {
        var site = Site(workforce: null);
        var service = Create(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(site),
            (_, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([]),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(null));

        var result = await service.GetCurrentWorkforceForSkyhookAsync(Call(7), site.SkyhookId);

        Assert.Equal(SkyhookResourceResultCode.Ok, result.Code);
        Assert.Null(result.Value!.Amount);
    }

    private static SkyhookResourceService Create(
        Func<long, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> read,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<SkyhookResourceSnapshot>>> readByCorporation,
        Func<ServiceCall, SkyhookResourceSnapshot, SkyhookResourcePermission, CancellationToken, ValueTask<bool>> authorize,
        Func<ServiceCall, SkyhookResourceSnapshot, bool, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> persist) =>
        new(read, readByCorporation, authorize, persist);

    private static ServiceCall Call(long corporationId)
    {
        var session = new ClientSession { CharacterId = 7001 };
        session.Set("corpid", new Eve.Marshal.PyInt(corporationId));
        return new ServiceCall
        {
            Method = "SkyhookResourceTest",
            Args = [],
            Session = session,
        };
    }

    private static SkyhookResourceSnapshot Site(
        long id = 9001,
        long corporationId = 7,
        bool active = true,
        long? workforce = 250,
        IReadOnlyList<SkyhookReagentResource>? reagents = null,
        SkyhookTheftVulnerability? vulnerability = null) =>
        new(
            SkyhookId: id,
            TypeId: 81080,
            SolarSystemId: 30000142,
            PlanetId: 4000001,
            CorporationId: corporationId,
            AllianceId: 9900001,
            ProductionActive: active,
            Workforce: workforce,
            ResourceVersion: "1.0.0",
            Reagents: reagents ?? [],
            TheftVulnerability: vulnerability);
}

public sealed class SkyhookResourceMgrProtocolTests
{
    [Fact]
    public async Task Corporation_skyhook_rpc_uses_session_corporation_and_returns_client_keyvals()
    {
        var skyhook = Site() with { State = 213 };
        var other = skyhook with
        {
            SkyhookId = 9000,
            PlanetId = 4000002,
            State = null,
            AllianceId = null,
        };
        long requestedCorporation = 0;
        var service = CreateService(
            (_, _, _, _) => ValueTask.FromResult(true),
            (corporationId, _) =>
            {
                requestedCorporation = corporationId;
                return ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([other, skyhook]);
            },
            skyhook);

        Assert.Equal("colonyResourcesMgr", service.Name);
        Assert.True(service.HasMethod("GetMyCorporationSkyhooks"));
        foreach (var localOnly in new[]
        {
            "GetSkyhook",
            "GetCurrentWorkforceForSkyhook",
            "GetTheftVulnerabilityForSkyhook",
            "IsProductionActiveAtSkyhook",
            "ActivateSkyhook",
            "DeactivateSkyhook",
            "GetReagentsInSkyhookNow",
        })
            Assert.False(service.HasMethod(localOnly), localOnly);

        var result = Assert.IsType<PyList>(
            await service.CallAsync(Call("GetMyCorporationSkyhooks", 7, corporationKey: "corporationID")));

        Assert.Equal(7L, requestedCorporation);
        Assert.Equal(2, result.Items.Count);
        var row = result.Items[0];
        Assert.Equal(
            ["skyhookID", "itemID", "solarSystemID", "planetID", "typeID", "state",
                "corporationID", "ownerCorpID", "allianceID"],
            Keys(row));
        Assert.Equal(new PyInt(skyhook.SkyhookId), Field(row, "skyhookID"));
        Assert.Equal(new PyInt(skyhook.SkyhookId), Field(row, "itemID"));
        Assert.Equal(new PyInt(skyhook.SolarSystemId), Field(row, "solarSystemID"));
        Assert.Equal(new PyInt(skyhook.PlanetId), Field(row, "planetID"));
        Assert.Equal(new PyInt(81080), Field(row, "typeID"));
        Assert.Equal(new PyInt(213), Field(row, "state"));
        Assert.Equal(new PyInt(7), Field(row, "corporationID"));
        Assert.Equal(new PyInt(7), Field(row, "ownerCorpID"));
        Assert.Equal(new PyInt(9900001), Field(row, "allianceID"));

        var unknownStateRow = result.Items[1];
        Assert.Equal(new PyInt(other.SkyhookId), Field(unknownStateRow, "skyhookID"));
        Assert.Equal(new PyInt(0), Field(unknownStateRow, "state"));
        Assert.Equal(new PyInt(0), Field(unknownStateRow, "allianceID"));
    }

    [Fact]
    public async Task Corporation_skyhook_rpc_maps_forbidden_to_the_standard_macho_user_error()
    {
        var skyhook = Site();
        var service = CreateService(
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _) => ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([skyhook]),
            skyhook);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(
            () => service.CallAsync(Call("GetMyCorporationSkyhooks", 7, characterId: 0)).AsTask());

        Assert.True(error.Error.IsUserError);
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    [Fact]
    public async Task Corporation_skyhook_rpc_returns_empty_list_without_session_corporation()
    {
        var reads = 0;
        var service = CreateService(
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _) =>
            {
                reads++;
                return ValueTask.FromResult<IReadOnlyList<SkyhookResourceSnapshot>>([]);
            },
            Site());

        var result = Assert.IsType<PyList>(
            await service.CallAsync(Call("GetMyCorporationSkyhooks", 0)));

        Assert.Empty(result.Items);
        Assert.Equal(0, reads);
    }

    private static SkyhookResourceMgrService CreateService(
        Func<ServiceCall, SkyhookResourceSnapshot, SkyhookResourcePermission, CancellationToken, ValueTask<bool>> authorize,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<SkyhookResourceSnapshot>>> readCorporation,
        SkyhookResourceSnapshot skyhook) =>
        new(new SkyhookResourceService(
            (_, _) => ValueTask.FromResult<SkyhookResourceSnapshot?>(skyhook),
            readCorporation,
            authorize,
            (_, current, active, _) =>
                ValueTask.FromResult<SkyhookResourceSnapshot?>(current with { ProductionActive = active })));

    private static ServiceCall Call(
        string method,
        long corporationId,
        long characterId = 7001,
        string corporationKey = "corpid")
    {
        var session = new ClientSession { CharacterId = characterId };
        session.Set(corporationKey, new PyInt(corporationId));
        return new ServiceCall
        {
            Method = method,
            Args = [],
            Session = session,
        };
    }

    private static SkyhookResourceSnapshot Site() =>
        new(
            SkyhookId: 9001,
            TypeId: 81080,
            SolarSystemId: 30000142,
            PlanetId: 4000001,
            CorporationId: 7,
            AllianceId: 9900001,
            ProductionActive: true,
            Workforce: 250,
            ResourceVersion: "1.0.0",
            Reagents:
            [
                new SkyhookReagentResource(
                    TypeId: 81100,
                    SecureStock: 4,
                    InsecureStock: 6),
            ],
            TheftVulnerability: new SkyhookTheftVulnerability(true, 1030, 1090));

    private static PyValue Field(PyValue value, string name)
    {
        var obj = Assert.IsType<PyObject>(value);
        var dictionary = Assert.IsType<PyDict>(obj.Args);
        return dictionary.Entries.Single(
            entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;
    }

    private static IReadOnlyList<string> Keys(PyValue value)
    {
        var obj = Assert.IsType<PyObject>(value);
        var dictionary = Assert.IsType<PyDict>(obj.Args);
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(obj.Name).Value);
        return dictionary.Entries.Select(
            entry => Assert.IsType<PyStr>(entry.Key).Value).ToArray();
    }
}

public class CoreCatalogTests
{
    [Fact]
    public void The_catalog_names_the_core_services()
    {
        var created = CoreServices.Create(new NoAccounts(), () => 0);
        var createdNames = created.Select(service => service.Name).Order().ToArray();

        var expectedNames = new[]
        {
                "abyssal_content_manager", "alphaInjectorMgr", "authentication", "bannedwords", "browserLockdownSvc", "cache", "calendarMgr", "calendarProxy",
                "chatAuthenticationService", "communityTrackMgr", "contextualOfferMgr", "contractProxy", "dynamicBountyMgr", "ingamereport", "jumpCloneSvc", "jumpTimers", "nonDiminishingInjectionMgr", "overviewPresetMgr", "ProjectDiscovery", "eveguard_report", "expertSystemMgr", "infoGatheringMgr", "kiringMgr", "loginRewardFacilities", "localizationServer", "LPStoreMgr",
                "dungeonExplorationMgr", "epicArcStatus", "keeper", "miningScanMgr", "node_graph", "peaceTreatyMgr", "petitioner", "pingService", "posMgr", "publicQaToolsServer", "seasonManager", "subscriptionMgr",
                "custom_agency_provider", "superWeaponMgr", "tourneyMgr",
                "raffleMgr", "raffleProxy", "serviceGatewayMgr", "sharedSettingsMgr", "shipStanceMgr",
                "solarsystemInterferenceMgr", "survey", "warStatisticMgr",
        }.Order().ToArray();
        Assert.Equal(expectedNames, createdNames);
        Assert.All(createdNames, name => Assert.Contains(name, CoreServices.Names));
    }


    [Fact]
    public async Task Core_factory_exposes_node_graph_callback_for_host_wiring()
    {
        Eve.Services.Core.NodeGraphRequest? observed = null;
        var services = CoreServices.Create(
            new NoAccounts(),
            () => 0,
            nodeGraphRpcCallback: (request, _) =>
            {
                observed = request;
                return ValueTask.CompletedTask;
            });
        var service = Assert.Single(services, candidate => candidate.Name == "node_graph");
        var session = new Eve.Services.ClientSession();

        var response = await service.CallAsync(new Eve.Services.ServiceCall
        {
            Method = "qa_unsubscribe",
            Args = Array.Empty<Eve.Marshal.PyValue>(),
            Session = session,
        });

        Assert.Same(Eve.Marshal.PyNone.Instance, response);
        Assert.Equal(Eve.Services.Core.NodeGraphOperation.QaUnsubscribe, observed!.Operation);
        Assert.Same(session, observed.Session);
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
