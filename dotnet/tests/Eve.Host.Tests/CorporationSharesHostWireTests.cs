using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class CorporationSharesHostWireTests
{
    [PgFact]
    public async Task Registered_corporation_share_reads_include_foreign_holdings_and_enforce_ceo_access()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corpSchema = $"csw_c_{suffix}"; var characterSchema = $"csw_h_{suffix}";
        var walletSchema = $"csw_w_{suffix}"; var skillSchema = $"csw_s_{suffix}";
        var eventSchema = $"csw_e_{suffix}"; var schedulerSchema = $"csw_j_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corpSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var store = new CorporationStore(source, characters, wallets, new WalletTransactions(source, walletSchema), skills, () => now, corpSchema);
            async Task<long> Character(string name)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var tx = await connection.BeginTransactionAsync();
                var id = await characters.InsertAsync(connection, tx, new NewCharacter(1, name + Guid.NewGuid().ToString("N"),
                    0, 1, 1, 1, 11, 1376, 1000044, null, 60000001, 30000001, 20000001, 10000001, null, null));
                await skills.GrantAsync(connection, tx, id, [new CharacterSkill(3363, 1, 0)]);
                await wallets.OpenAsync(connection, tx, id, 5_000_000m, 10); await tx.CommitAsync(); return id;
            }
            var ceo = await Character("Share owner "); var issuerCeo = await Character("Share issuer ");
            var member = await Character("Share member ");
            var owner = await store.CreateAsync(ceo, new NewCorporation("Wire Share Owner", "WSOW", "", "", 0m), $"wire-owner:{ceo}");
            var issuer = await store.CreateAsync(issuerCeo, new NewCorporation("Wire Share Issuer", "WSIS", "", "", 0m), $"wire-issuer:{issuerCeo}");
            var application = await store.ApplyAsync(member, owner.Id, "");
            await store.RespondAsync(ceo, application, 6, "", ""); await store.RespondAsync(member, application, 2, "", "");
            await using var scheduler = new DurableScheduler(source, () => now, null, schedulerSchema);
            var bus = new EventBus(source, scheduler, null, eventSchema);
            var sessions = new CharacterSessions();
            CorporationShareNotifications.Register(store, characters, sessions, bus);
            await bus.PumpAsync();
            var service = (IService)new CorpRegistryService(store, characters, () => now);
            var registry = new ServiceRegistry(); registry.Register(service); Assert.Same(service, registry.Lookup("corpRegistry"));
            var dispatcher = new PacketDispatcher(registry, () => now);
            var ownerSink = new Capture(); var issuerSink = new Capture(); var memberSink = new Capture();
            var ownerSession = Session(ceo, owner.Id, ownerSink, 1);
            var issuerSession = Session(issuerCeo, issuer.Id, issuerSink, 2);
            var memberSession = Session(member, owner.Id, memberSink, 3);
            sessions.Enter(ceo, ownerSession); sessions.Enter(issuerCeo, issuerSession); sessions.Enter(member, memberSession);
            long callId = 1;
            async Task<PyValue> Invoke(ClientSession session, Capture sink, string method, params PyValue[] args)
            {
                var request = Request(callId++, method, args);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
                return sink.Packets[^1];
            }
            var moved = await Invoke(issuerSession, issuerSink, "MoveCompanyShares", new PyLong(issuer.Id), new PyLong(owner.Id), new PyInt(125));
            Assert.IsType<PyNone>(Result(moved));
            await bus.PumpAsync();
            Assert.Single(ownerSink.Packets);
            Assert.Empty(memberSink.Packets);
            Assert.True(issuerSink.Packets.Count >= 2);
            var added = Assert.Single(Notifications(ownerSink));
            Assert.Equal(owner.Id, Number(added[0])); Assert.Equal(issuer.Id, Number(added[1]));
            var addFields = Assert.IsType<PyDict>(added[2]);
            AssertPair(addFields, "shareholderID", null, owner.Id);
            AssertPair(addFields, "corporationID", null, issuer.Id);
            AssertPair(addFields, "shares", null, 125);
            var issuerUpdate = Assert.Single(Notifications(issuerSink));
            AssertPair(Assert.IsType<PyDict>(issuerUpdate[2]), "shares", 1000, 875);
            var replay = Request(1, "MoveCompanyShares", [new PyLong(issuer.Id), new PyLong(owner.Id), new PyInt(125)]);
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(replay)), issuerSession));
            await bus.PumpAsync();
            Assert.Single(Notifications(ownerSink)); Assert.Single(Notifications(issuerSink));
            var portfolio = Rows(Result(await Invoke(ownerSession, ownerSink, "GetSharesByShareholder", new PyInt(1))))
                .Select(Values).ToDictionary(row => Number(row[1]), row => Number(row[2]));
            Assert.Equal(1000, portfolio[owner.Id]); Assert.Equal(125, portfolio[issuer.Id]);
            var shareholders = Rows(Result(await Invoke(ownerSession, ownerSink, "GetShareholders", new PyLong(issuer.Id))))
                .Select(Values).ToDictionary(row => Number(row[0]), row => Number(row[2]));
            Assert.Equal(125, shareholders[owner.Id]);
            Assert.Equal(MachoMessage.ErrorResponse, PacketType(await Invoke(memberSession, memberSink, "GetShareholders", new PyLong(issuer.Id))));
            Assert.Equal(MachoMessage.ErrorResponse, PacketType(await Invoke(issuerSession, issuerSink, "GetShareholders", new PyLong(owner.Id))));
            await store.SetVoteShareholderAsync(issuer.Id, owner.Id, 100);
            await bus.PumpAsync();
            var updated = Notifications(ownerSink).Last();
            AssertPair(Assert.IsType<PyDict>(updated[2]), "shares", 125, 100);
            AssertPair(Assert.IsType<PyDict>(updated[2]), "corporationID", issuer.Id, issuer.Id);
            await store.SetVoteShareholderAsync(issuer.Id, owner.Id, 0);
            await bus.PumpAsync();
            var removed = Notifications(ownerSink).Last();
            AssertPair(Assert.IsType<PyDict>(removed[2]), "shareholderID", owner.Id, null);
            AssertPair(Assert.IsType<PyDict>(removed[2]), "shares", 100, null);
            Assert.Empty(Notifications(memberSink));
            Assert.Single(Notifications(issuerSink)); // foreign holder changes must not corrupt issuer's own cache
            await Task.WhenAll(store.SetVoteShareholderAsync(issuer.Id, owner.Id, 11),
                store.SetVoteShareholderAsync(issuer.Id, owner.Id, 22));
            await bus.PumpAsync();
            var changes = Notifications(ownerSink).TakeLast(2).Select(n => Assert.IsType<PyDict>(n[2])).ToArray();
            var first = Pair(changes[0], "shares"); var second = Pair(changes[1], "shares");
            Assert.IsType<PyNone>(first[0]);
            Assert.Equal(Number(first[1]), Number(second[0]));
            Assert.Contains(Number(second[1]), new long[] { 11, 22 });
        }
        finally
        {
            foreach (var schema in new[] { corpSchema, characterSchema, walletSchema, skillSchema, eventSchema, schedulerSchema })
            { await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await command.ExecuteNonQueryAsync(); }
        }
    }

    private static IReadOnlyList<PyValue> Pair(PyDict fields, string name) =>
        Assert.IsType<PyList>(fields.Entries.Single(x => x.Key switch { PyStr s => s.Value == name, PyBytes b => System.Text.Encoding.UTF8.GetString(b.Value.Span) == name, _ => false }).Value).Items;
    private static void AssertPair(PyDict fields, string name, long? before, long? after)
    {
        var pair = Pair(fields, name);
        if (before is null) Assert.IsType<PyNone>(pair[0]); else Assert.Equal(before.Value, Number(pair[0]));
        if (after is null) Assert.IsType<PyNone>(pair[1]); else Assert.Equal(after.Value, Number(pair[1]));
    }
    private static IEnumerable<PyValue[]> Notifications(Capture sink)
    {
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            if (((PyStr)address[1]).Value != "OnShareChange") continue;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            yield return [.. ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items];
        }
    }
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
    private static ClientSession Session(long character, long corporation, Capture sink, long id)
    {
        var session = new ClientSession(sink) { SessionId = id, CharacterId = character, ClientId = id };
        session.Set("charid", new PyLong(character)); session.Set("corpid", new PyLong(corporation)); return session;
    }
    private static PyValue Request(long id, string method, params PyValue[] args)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyLong(id), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "corpRegistry", CallId: new PyLong(id), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(args), new PyDict([])]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(), new PyInt(7), payload, new PyDict([]),
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
    }
    private static long PacketType(PyValue value) => Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(value).Args).Items[0]).Value;
    private static PyValue Result(PyValue value)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(value).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }
    private static IReadOnlyList<PyValue> Rows(PyValue value)
    {
        var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        var lines = fields.Entries.Single(x => x.Key switch { PyStr text => text.Value == "lines", PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span) == "lines", _ => false }).Value;
        return Assert.IsType<PyList>(lines).Items;
    }
    private static IReadOnlyList<PyValue> Values(PyValue row) => row switch
    {
        PyList list => list.Items,
        PyPackedRow packed => packed.Values,
        _ => Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(row))).Values
    };
    private static long Number(PyValue value) => value switch { PyInt x => x.Value, PyLong x => x.Value, _ => throw new InvalidOperationException("Expected integer") };
}
