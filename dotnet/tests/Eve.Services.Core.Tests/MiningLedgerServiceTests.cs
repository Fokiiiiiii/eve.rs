using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Core.Tests;

public sealed class MiningLedgerServiceTests
{
    private const long CharacterId = 90000001;
    private const long CorporationId = 98000001;
    private const long Director = 1;
    private const long Accountant = 256;
    private const long JuniorAccountant = 1L << 52;

    [Fact]
    public async Task Character_logs_use_only_persisted_visible_rows_with_exact_wire_fields_and_order()
    {
        const long now = 134100000000000100;
        var state = JsonNode.Parse("""
            {
              "characters": {
                "90000001": { "entries": [
                  { "entryID": 10, "eventDate": "134100000000000001", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 40, "quantityWasted": 2, "quantityCritical": 1,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 0,
                    "yieldKind": "ore", "characterVisibleAfter": "134100000000000001" },
                  { "entryID": 12, "eventDate": "134100000000000001", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 80, "quantityWasted": 0, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 0,
                    "yieldKind": "ore", "characterVisibleAfter": "134100000000000099" },
                  { "entryID": 13, "eventDate": "134100000000000099", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 100, "quantityWasted": 0, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 0,
                    "yieldKind": "ore", "characterVisibleAfter": "134100000000000101" },
                  { "entryID": 14, "eventDate": "133900000000000000", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 200, "quantityWasted": 0, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 0,
                    "yieldKind": "ore", "characterVisibleAfter": "133900000000000000" }
                ] }
              }
            }
            """)!.AsObject();
        var reads = 0;
        var service = new CharacterMiningLedgerService(_ => { reads++; return Task.FromResult(state); }, () => now);

        var request = Request("GetCharacterLogs");
        request.Session.Set("characterID", new PyInt(0));
        var result = Assert.IsType<PyList>(await service.CallAsync(request));

        Assert.Equal("characterMiningLedger", service.Name);
        Assert.True(service.HasMethod("GetCharacterLogs"));
        Assert.Equal(1, reads);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new PyInt(12), Field(result.Items[0], "entryID"));
        Assert.Equal(new PyInt(10), Field(result.Items[1], "entryID"));
        Assert.Equal(new PyStr("util.KeyVal"), Assert.IsType<PyObject>(result.Items[0]).Name);
        Assert.Equal(new PyLong(134100000000000001), Field(result.Items[0], "eventDate"));
        Assert.Equal(new PyInt(30000142), Field(result.Items[0], "solarSystemID"));
        Assert.Equal(Field(result.Items[0], "solarSystemID"), Field(result.Items[0], "solarsystemID"));
        Assert.Equal(new PyInt(80), Field(result.Items[0], "quantity"));
        Assert.Equal(new PyStr("ore"), Field(result.Items[0], "yieldKind"));
        Assert.Equal(14, Assert.IsType<PyDict>(Assert.IsType<PyObject>(result.Items[0]).Args).Entries.Count);
    }

    [Fact]
    public async Task Character_without_a_session_character_returns_empty_without_loading_the_table()
    {
        var reads = 0;
        var service = new CharacterMiningLedgerService(_ => { reads++; return Task.FromResult(new JsonObject()); }, () => 0);
        var request = Request("GetCharacterLogs", characterId: 0);

        var result = Assert.IsType<PyList>(await service.CallAsync(request));

        Assert.Empty(result.Items);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Corporation_observer_headers_require_a_ledger_role_and_sort_only_existing_visible_observers()
    {
        const long now = 134100000000000100;
        var state = JsonNode.Parse("""
            {
              "observers": {
                "100": { "itemID": 100, "corporationID": 98000001, "solarSystemID": 30000142,
                  "itemName": "Zeta", "entries": [ { "entryID": 1, "eventDate": "134100000000000001",
                    "observerVisibleAfter": "134100000000000001" } ] },
                "101": { "itemID": 101, "corporationID": 98000001, "solarSystemID": 30000143,
                  "itemName": "Alpha", "entries": [ { "entryID": 2, "eventDate": "134100000000000001",
                    "observerVisibleAfter": "134100000000000001" } ] },
                "102": { "itemID": 102, "corporationID": 98000001, "solarSystemID": 30000144,
                  "itemName": "Hidden", "entries": [ { "entryID": 3, "eventDate": "134100000000000001",
                    "observerVisibleAfter": "134100000000000101" } ] },
                "103": { "itemID": 103, "corporationID": 98000002, "solarSystemID": 30000145,
                  "itemName": "Other corporation", "entries": [ { "entryID": 4, "eventDate": "134100000000000001",
                    "observerVisibleAfter": "134100000000000001" } ] },
                "104": { "itemID": 104, "corporationID": 98000001, "solarSystemID": 30000146,
                  "itemName": "Empty", "entries": [] },
                "105": { "itemID": 105, "corporationID": 98000001, "solarSystemID": 30000147,
                  "itemName": "Expired", "entries": [ { "entryID": 5, "eventDate": "133900000000000000",
                    "observerVisibleAfter": "133900000000000000" } ] }
              }
            }
            """)!.AsObject();
        var reads = 0;
        var service = new CorpMiningLedgerService(_ => { reads++; return Task.FromResult(state); }, () => now);

        var unauthorized = Assert.IsType<PyList>(await service.CallAsync(Request("GetObserversWithMiningEvents", corporationId: CorporationId, role: 0)));
        var result = Assert.IsType<PyList>(await service.CallAsync(Request("GetObserversWithMiningEvents", corporationId: CorporationId, role: Accountant)));

        Assert.Equal("corpMiningLedger", service.Name);
        Assert.Empty(unauthorized.Items);
        Assert.Equal(1, reads);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new PyInt(101), Field(result.Items[0], "itemID"));
        Assert.Equal(new PyStr("Alpha"), Field(result.Items[0], "itemName"));
        Assert.Equal(new PyInt(30000143), Field(result.Items[0], "solarSystemID"));
        Assert.Equal(Field(result.Items[0], "solarSystemID"), Field(result.Items[0], "solarsystemID"));
        Assert.Equal(new PyInt(100), Field(result.Items[1], "itemID"));
    }

    [Theory]
    [InlineData(Director)]
    [InlineData(Accountant)]
    [InlineData(JuniorAccountant)]
    public async Task Each_reference_ledger_role_can_read_the_corporation_observer_ledger(long role)
    {
        const long now = 134100000000000100;
        var state = JsonNode.Parse("""
            {
              "observers": { "100": { "itemID": 100, "corporationID": 98000001,
                "entries": [
                  { "entryID": 3, "eventDate": "134100000000000001", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 30, "quantityWasted": 1, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 100,
                    "yieldKind": "ore", "observerVisibleAfter": "134100000000000001" },
                  { "entryID": 5, "eventDate": "134100000000000001", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 50, "quantityWasted": 0, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 100,
                    "yieldKind": "ice", "observerVisibleAfter": "134100000000000001" },
                  { "entryID": 6, "eventDate": "134100000000000099", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 60, "quantityWasted": 0, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 100,
                    "yieldKind": "ice", "observerVisibleAfter": "134100000000000101" },
                  { "entryID": 7, "eventDate": "133900000000000000", "characterID": 90000001,
                    "corporationID": 98000001, "solarSystemID": 30000142, "typeID": 17476,
                    "quantity": 70, "quantityWasted": 0, "quantityCritical": 0,
                    "shipTypeID": 543, "moduleTypeID": 18722, "observerItemID": 100,
                    "yieldKind": "ice", "observerVisibleAfter": "133900000000000000" }
                ] } }
            }
            """)!.AsObject();
        var service = new CorpMiningLedgerService(_ => Task.FromResult(state), () => now);

        var result = Assert.IsType<PyList>(await service.CallAsync(Request("GetObserverLedger", args: [new PyInt(100)],
            corporationId: CorporationId, role: role)));

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new PyInt(5), Field(result.Items[0], "entryID"));
        Assert.Equal(new PyInt(3), Field(result.Items[1], "entryID"));
        Assert.Equal(new PyInt(100), Field(result.Items[0], "observerItemID"));
        Assert.Equal(new PyStr("ice"), Field(result.Items[0], "yieldKind"));
    }

    [Fact]
    public async Task Missing_or_foreign_observer_and_unknown_methods_return_reference_empty_shapes()
    {
        var reads = 0;
        var state = JsonNode.Parse("""
            { "observers": { "100": { "itemID": 100, "corporationID": 98000001,
              "entries": [ { "entryID": 1, "eventDate": "134100000000000001", "observerVisibleAfter": "134100000000000001" } ] } } }
            """)!.AsObject();
        var service = new CorpMiningLedgerService(_ => { reads++; return Task.FromResult(state); }, () => 134100000000000100);

        var missing = Assert.IsType<PyList>(await service.CallAsync(Request("GetObserverLedger", args: [new PyInt(0)], corporationId: CorporationId, role: Accountant)));
        var foreign = Assert.IsType<PyList>(await service.CallAsync(Request("GetObserverLedger", args: [new PyInt(100)], corporationId: 98000002, role: Accountant)));
        var unknown = await service.CallAsync(Request("InventLedgerEntry", corporationId: CorporationId, role: Accountant));

        Assert.Empty(missing.Items);
        Assert.Empty(foreign.Items);
        Assert.Same(PyNone.Instance, unknown);
        Assert.Equal(1, reads);
    }

    private static ServiceCall Request(
        string method,
        PyValue[]? args = null,
        long characterId = CharacterId,
        long corporationId = CorporationId,
        long role = Accountant,
        CancellationToken cancellation = default)
    {
        var session = new ClientSession(new Sink());
        if (characterId > 0) session.Set("charid", new PyInt(characterId));
        if (corporationId > 0) session.Set("corpid", new PyInt(corporationId));
        session.Set("corprole", new PyLong(role));
        return new ServiceCall { Method = method, Args = args ?? [], Session = session, Cancellation = cancellation };
    }

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(pair => pair.Key == new PyStr(name)).Value;

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }
}
