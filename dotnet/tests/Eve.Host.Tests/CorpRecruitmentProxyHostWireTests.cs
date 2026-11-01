using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class CorpRecruitmentProxyHostWireTests
{
    [PgFact]
    public async Task Recruitment_ads_read_update_and_delete_through_registered_host()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corpSchema = $"crp_c_{suffix}";
        var characterSchema = $"crp_h_{suffix}";
        var walletSchema = $"crp_w_{suffix}";
        var skillSchema = $"crp_s_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corpSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var store = new CorporationStore(source, characters, wallets,
                new WalletTransactions(source, walletSchema), skills, () => now, corpSchema);
            long founder;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Recruitment Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000004, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }
            var corporation = await store.CreateAsync(founder,
                new NewCorporation("Recruitment Wire Corp", $"RW{suffix[..3]}", "", "", 0.05m),
                $"recruitment-wire:{founder}");

            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 55, CharacterId = founder };
            var sessions = new CharacterSessions();
            sessions.Enter(founder, session);
            var service = (IService)new CorpRecruitmentProxyService(store, characters, () => now, sessions);
            var registry = new ServiceRegistry();
            registry.Register(service);
            Assert.Same(service, registry.Lookup("corpRecProxy"));
            var dispatcher = new PacketDispatcher(registry, () => now);
            var callId = 0;
            async Task<PyValue> Call(string method, PyValue[] args, bool notifies = false,
                params KeyValuePair<PyValue, PyValue>[] extraKwargs)
            {
                var before = capture.Packets.Count;
                Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(++callId, method, args, extraKwargs), session));
                Assert.Equal(before + (notifies ? 2 : 1), capture.Packets.Count);
                Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.CallRsp"),
                    Assert.IsType<PyObject>(capture.Packets[before]).Name);
                if (notifies)
                    Assert.Equal("OnCorporationRecruitmentAdChanged", NotificationName(capture.Packets[before + 1]));
                return Result(capture.Packets[before]);
            }

            var adId = Assert.IsType<PyInt>(await Call("CreateRecruitmentAd",
                [new PyInt(7), new PyInt(1), new PyInt(2), new PyStr("Looking for pilots"),
                 Py.List(new PyInt(founder)), new PyStr("Join us"), new PyInt(4), new PyInt(100),
                 new PyInt(8)], notifies: true)).Value;
            var recruiters = Assert.IsType<PyList>(await Call("GetRecruiters", [new PyInt(adId)]));
            Assert.Equal(new PyValue[] { new PyInt(founder) }, recruiters.Items);

            var ad = Assert.IsType<PyObject>(await Call("GetRecruitmentAd",
                [new PyInt(corporation.Id), new PyInt(adId)]));
            Assert.Equal(new PyValue[] { new PyStr("adID"), new PyStr("corporationID"), new PyStr("allianceID"),
                new PyStr("channelID"), new PyStr("typeMask"), new PyStr("langMask"), new PyStr("description"),
                new PyStr("title"), new PyStr("createDateTime"), new PyStr("expiryDateTime"),
                new PyStr("hourMask1"), new PyStr("minSP"), new PyStr("otherMask"), new PyStr("memberCount") },
                AsList(Field(ad, "header")));
            Assert.Equal(new PyInt(100), AsList(Field(ad, "line"))[11]);
            Assert.Equal(new PyInt(8), AsList(Field(ad, "line"))[12]);

            Assert.Single(Assert.IsType<PyList>(await Call("GetRecruitmentAdsForCorporation", [])).Items);
            Assert.Single(Assert.IsType<PyList>(await Call("GetRecruitmentAdsByCorpID",
                [new PyInt(corporation.Id)])).Items);
            Assert.Single(Assert.IsType<PyList>(await Call("SearchCorpAds",
                [new PyInt(0), new PyInt(0), PyBool.False, PyBool.False, new PyInt(0), new PyInt(0),
                 new PyInt(0), new PyInt(1), new PyInt(1), new PyInt(0), new PyInt(0)])).Items);

            Assert.IsType<PyNone>(await Call("UpdateRecruitmentAd",
                [new PyInt(adId), new PyInt(4), new PyInt(16), new PyStr("Updated description"),
                 Py.List(new PyInt(founder)), new PyStr("Updated title"), new PyInt(0), new PyInt(2)],
                notifies: true,
                new(new PyStr("minSP"), new PyInt(321)),
                new(new PyStr("otherMask"), new PyInt(7))));
            ad = Assert.IsType<PyObject>(await Call("GetRecruitmentAd",
                [new PyInt(corporation.Id), new PyInt(adId)]));
            var values = AsList(Field(ad, "line"));
            Assert.Equal(new PyInt(321), values[11]);
            Assert.Equal(new PyInt(7), values[12]);
            Assert.Equal(new PyWString("Updated description"), values[6]);
            Assert.Equal(new PyWString("Updated title"), values[7]);

            Assert.IsType<PyNone>(await Call("DeleteRecruitmentAd", [new PyInt(adId)], notifies: true));
            Assert.IsType<PyNone>(await Call("GetRecruitmentAd",
                [new PyInt(corporation.Id), new PyInt(adId)]));
            Assert.Empty(Assert.IsType<PyList>(await Call("GetRecruitmentAdsForCorporation", [])).Items);
        }
        finally
        {
            foreach (var schema in new[] { corpSchema, characterSchema, walletSchema, skillSchema })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static PyValue ClientCallPacket(int callId, string method, PyValue[] arguments,
        IReadOnlyList<KeyValuePair<PyValue, PyValue>> extraKwargs)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "corpRecProxy",
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            .. extraKwargs]);
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(arguments), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(),
                new PyInt(7), payload, new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance,
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
    }

    private static PyValue Result(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .Single(entry => entry.Key == new PyStr(name)).Value;

    private static IReadOnlyList<PyValue> AsList(PyValue value) =>
        Assert.IsType<PyList>(value).Items;

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal)) return null;
        return MachoAddress.Decode(args.Items[2]).BroadcastId;
    }
}
