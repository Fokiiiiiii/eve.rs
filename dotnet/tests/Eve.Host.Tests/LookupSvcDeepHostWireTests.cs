using System.Text;
using System.Text.Json;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Lookup_svc_all_client_methods_return_the_consumed_rows_over_host_wire()
    {
        var directory = new LookupAuditDirectory();
        Start(configure: registry => registry.Register(CoreServices.CreateLookupService(directory)));
        var (front, open) = await OpenFrontAsync();
        long callId = 1;

        var exactCharacters = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupCharacters", new PyStr("Shared Pilot"), new PyLong(1)));
        var character = Assert.Single(exactCharacters.Items);
        Assert.Equal(100, WireInteger(WireField(character, "characterID")));
        Assert.Equal(100, WireInteger(WireField(character, "ownerID")));
        Assert.Equal("Shared Pilot", WireText(WireField(character, "characterName")));
        var exactLongMiss = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupCharacters", new PyStr("Pilot"), new PyLong(1)));
        Assert.Empty(exactLongMiss.Items);

        var playerCharacters = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupEvePlayerCharacters", new PyStr("Shared"), new PyInt(0)));
        Assert.Single(playerCharacters.Items);
        Assert.Equal(100, WireInteger(WireField(playerCharacters.Items[0], "characterID")));

        var corporations = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupCorporations", new PyStr("Shared")));
        Assert.Equal(3, corporations.Items.Count);
        Assert.Equal("Shared Corp", WireText(WireField(corporations.Items.Single(row => WireInteger(WireField(row, "corporationID")) == 200), "corporationName")));

        var factions = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupFactions", new PyStr("Shared")));
        Assert.Single(factions.Items);
        Assert.Equal(400, WireInteger(WireField(factions.Items[0], "factionID")));

        var locations = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupLocationsByGroup", new PyInt(5), new PyStr("Shared")));
        var knownLocations = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupKnownLocationsByGroup", new PyLong(5), new PyStr("Shared")));
        Assert.Single(locations.Items);
        Assert.Single(knownLocations.Items);
        Assert.Equal(30_000_142, WireInteger(WireField(locations.Items[0], "itemID")));
        Assert.Equal("Shared System", WireText(WireField(knownLocations.Items[0], "itemName")));

        var owners = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupOwners", new PyStr("Shared"), new PyInt(0)));
        var pcOwners = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupPCOwners", new PyStr("Shared"), new PyLong(0)));
        var accountOwners = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupNoneNPCAccountOwners", new PyStr("Shared"), new PyInt(0)));
        Assert.Equal(7, owners.Items.Count);
        Assert.Equal(5, pcOwners.Items.Count);
        Assert.Equal(3, accountOwners.Items.Count);
        foreach (var result in new[] { owners, pcOwners, accountOwners })
        {
            var row = result.Items.Single(item => WireInteger(WireField(item, "ownerID")) == 100);
            Assert.Equal("Shared Pilot", WireText(WireField(row, "ownerName")));
            Assert.IsType<PyInt>(WireField(row, "typeID"));
        }

        var warable = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupWarableCorporationsOrAlliances", new PyStr("Shared"), PyValue.Of(false)));
        Assert.Equal(5, warable.Items.Count);
        Assert.Equal(0, WireInteger(WireField(warable.Items.Single(row => WireInteger(WireField(row, "ownerID")) == 200), "warPermit")));
        Assert.Equal(1, WireInteger(WireField(warable.Items.Single(row => WireInteger(WireField(row, "ownerID")) == 201), "warPermit")));
        Assert.Equal(0, WireInteger(WireField(warable.Items.Single(row => WireInteger(WireField(row, "ownerID")) == 202), "warPermit")));
        Assert.Equal(0, WireInteger(WireField(warable.Items.Single(row => WireInteger(WireField(row, "ownerID")) == 300), "warPermit")));
        Assert.Equal(1, WireInteger(WireField(warable.Items.Single(row => WireInteger(WireField(row, "ownerID")) == 301), "warPermit")));

        directory.Characters = [new LookupCharacter(101, "Shared Pilot Updated")];
        var refreshed = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId, "lookupSvc", "LookupEvePlayerCharacters", new PyStr("Shared"), new PyInt(0)));
        Assert.Equal(101, WireInteger(WireField(Assert.Single(refreshed.Items), "characterID")));
    }

    [Fact]
    public async Task Lookup_svc_none_searches_are_empty_and_every_search_method_keeps_its_named_error()
    {
        var directory = new LookupAuditDirectory();
        Start(configure: registry => registry.Register(CoreServices.CreateLookupService(directory)));
        var (front, open) = await OpenFrontAsync();
        long callId = 1;

        var emptyOwners = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupOwners", PyNone.Instance, PyNone.Instance));
        var emptyLocations = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, callId++, "lookupSvc", "LookupKnownLocationsByGroup", new PyInt(5), PyNone.Instance));
        Assert.Empty(emptyOwners.Items);
        Assert.Empty(emptyLocations.Items);
        Assert.Equal(0, directory.ReadCount);

        var overlong = new PyStr(new string('x', 257));
        (string Method, PyValue[] Args)[] invalidCalls =
        [
            ("LookupCharacters", [overlong]),
            ("LookupEvePlayerCharacters", [overlong]),
            ("LookupCorporations", [overlong]),
            ("LookupFactions", [overlong]),
            ("LookupLocationsByGroup", [new PyInt(5), overlong]),
            ("LookupKnownLocationsByGroup", [new PyLong(5), overlong]),
            ("LookupOwners", [overlong]),
            ("LookupPCOwners", [overlong]),
            ("LookupNoneNPCAccountOwners", [overlong]),
            ("LookupWarableCorporationsOrAlliances", [overlong]),
        ];

        foreach (var (method, args) in invalidCalls)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, "lookupSvc", method, new PyTuple(args), TransportKwargs()))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.ErrorResponse, response.Type);
            var error = Assert.IsType<PyTuple>(response.Payload);
            Assert.Equal((long)MachoMessage.CallReq, WireInteger(error.Items[0]));
            Assert.Equal((long)MachoErrorType.WrappedException, WireInteger(error.Items[1]));
            var wire = Encoding.ASCII.GetString(send.Payload.Span);
            Assert.Contains("eveexceptions.UserError", wire);
            Assert.Contains("CustomInfo", wire);
            Assert.Contains("Lookup text is too long.", wire);
            Assert.DoesNotContain("exceptions.RuntimeError", wire);
        }

        Assert.Equal(0, directory.ReadCount);
    }

    private sealed class LookupAuditDirectory : ILookupDirectory
    {
        public IReadOnlyList<LookupCharacter> Characters { get; set; } = [new LookupCharacter(
            100, "Shared Pilot", CorporationId: 200, AllianceId: 300)];
        public IReadOnlyList<LookupCorporation> Corporations { get; } =
        [
            PersistedCorporation(200, "Shared Corp", "SHR", isNpc: false, allowWar: false),
            PersistedCorporation(201, "Shared Open Corp", "OPEN", isNpc: false, allowWar: true),
            PersistedCorporation(202, "Shared NPC Corp", "NPC", isNpc: true, allowWar: null),
        ];
        public IReadOnlyList<LookupAlliance> Alliances { get; } =
        [
            new LookupAlliance(300, "Shared Closed Alliance", ExecutorCorporationId: 200),
            new LookupAlliance(301, "Shared Open Alliance", ExecutorCorporationId: 201),
        ];
        public IReadOnlyList<LookupFaction> Factions { get; } = [new LookupFaction(400, "Shared Faction")];
        public IReadOnlyList<LookupLocation> Locations { get; } =
        [
            new LookupLocation(30_000_142, "Shared System", 5, 5, 30_000_142, 20_000_020, 10_000_002),
        ];
        public int ReadCount { get; private set; }

        private static LookupCorporation PersistedCorporation(long id, string name, string ticker, bool isNpc, bool? allowWar)
        {
            var settings = JsonSerializer.SerializeToElement(new { allowWar });
            var corporation = new Eve.Corporations.Corporation(
                id, name, ticker, 0, 0, null, 0, 0, 0m, 0m, "", "", 10, isNpc, false, 0, settings);
            return PersistenceLookupDirectory.MapCorporation(corporation, 400);
        }

        public ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(Characters);
        }

        public ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(Corporations);
        }

        public ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(Alliances);
        }

        public ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(Factions);
        }

        public ValueTask<IReadOnlyList<LookupLocation>> LocationsByGroupAsync(int groupId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<IReadOnlyList<LookupLocation>>(Locations.Where(location => location.GroupId == groupId).ToArray());
        }

        public ValueTask<IReadOnlyList<LookupType>> TypesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LookupType>>([]);
    }
}
