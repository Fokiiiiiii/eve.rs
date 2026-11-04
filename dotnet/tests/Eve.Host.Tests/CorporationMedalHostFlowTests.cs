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

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [PgFact]
    public async Task Corporation_medal_reads_and_creation_notifications_cross_the_registered_host()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corpSchema = $"medal_c_{suffix}";
        var characterSchema = $"medal_h_{suffix}";
        var walletSchema = $"medal_w_{suffix}";
        var skillSchema = $"medal_s_{suffix}";
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
                    $"Medal Host Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000044, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }

            var corporation = await store.CreateAsync(founder,
                new NewCorporation("Medal Host Corporation", "MHC", "", "", 0.05m),
                $"medal-host:{founder}");
            var sessions = new CharacterSessions();
            var service = new CorporationService(store, characters, sessions);
            Start(configure: registry => registry.Register(service), sessionOpened: session =>
            {
                session.CharacterId = founder;
                session.Set("corpid", new PyLong(corporation.Id));
                sessions.Enter(founder, session);
            });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            long callId = 1;
            async Task<PyValue> Call(string method, params PyValue[] args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                    Packets.CallWithKwargs(open, callId++, "corporationSvc", method,
                        new PyTuple(args), Py.Dict(("machoVersion", new PyInt(1)))))));
                return WireResult(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            }

            var graphics = new PyList([Py.Tuple(new PyInt(1), new PyWString("medal.host"), new PyInt(7))]);
            var medalId = MedalNumber(await Call("CreateMedal", new PyWString("Host medal"),
                new PyWString("Awarded through Host"), graphics));
            Assert.True(medalId > 0);
            Assert.Equal("OnCorporationMedalAdded", ReadCorporationNotification(
                Assert.IsType<Send>(await front.ReceiveAsync())).Name);
            Assert.Equal(medalId, (await store.MedalAsync(medalId))!.Id);

            var all = Assert.IsType<PyTuple>(CachedPayload(await Call("GetAllCorpMedals", new PyInt(corporation.Id))));
            var medalRow = Assert.Single(Lines(all.Items[0]), row =>
                MedalNumber(Assert.IsType<PyList>(row).Items[0]) == medalId);
            Assert.Equal(new PyWString("Host medal"), Assert.IsType<PyList>(medalRow).Items[3]);
            Assert.Equal(new PyWString("medal.host"),
                Assert.IsType<PyList>(Assert.Single(Lines(all.Items[1]))).Items[2]);

            Assert.Same(PyNone.Instance, await Call("GiveMedalToCharacters", new PyInt(medalId),
                new PyList([new PyLong(founder)]), new PyWString("Host award")));
            Assert.Equal("OnMedalIssued", ReadCorporationNotification(
                Assert.IsType<Send>(await front.ReceiveAsync())).Name);

            var received = Assert.IsType<PyTuple>(await Call("GetMedalsReceived"));
            var receivedRow = Assert.IsType<PyList>(Assert.Single(Lines(received.Items[0])));
            Assert.Equal(new PyWString("Host award"), receivedRow.Items[4]);
            Assert.Single(Lines(received.Items[1]));

            var details = await Call("GetMedalDetails", new PyLong(medalId));
            var detail = Assert.Single(Assert.IsType<PyList>(MedalField(details, "info")).Items);
            Assert.Equal(new PyInt(medalId), MedalField(detail, "medalID"));
            Assert.Equal(new PyInt(founder), MedalField(detail, "creatorID"));
            Assert.Equal(new PyInt(1), MedalField(detail, "numberOfRecipients"));

            var confirmedMedalId = MedalNumber(await Call("CreateMedal", new PyWString("Confirmed medal"),
                new PyWString("Client confirmation path"), graphics, PyBool.True));
            Assert.True(confirmedMedalId > medalId);
            Assert.Equal("OnCorporationMedalAdded", ReadCorporationNotification(
                Assert.IsType<Send>(await front.ReceiveAsync())).Name);

            var employment = Assert.IsType<PyTuple>(await Call(
                "GetEmployementRecordAndCharacterTransfers", new PyInt(founder)));
            var employmentRow = Assert.IsType<PyList>(Assert.Single(Lines(employment.Items[0]), row =>
                MedalNumber(Assert.IsType<PyList>(row).Items[0]) == corporation.Id));
            Assert.Equal(new PyInt(corporation.Id), employmentRow.Items[0]);
            Assert.IsType<PyLong>(employmentRow.Items[1]);
            Assert.Equal(new PyInt(0), employmentRow.Items[2]);
            Assert.Empty(Assert.IsType<PyList>(employment.Items[1]).Items);
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

    private static PyValue WireResult(PyValue response)
    {
        var state = ((PyTuple)((PyObject)response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)state[0]).Value);
        var payload = (PyTuple)state[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private static PyValue CachedPayload(PyValue value) =>
        Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(value).Args).Items[1]).Decoded!;

    private static IReadOnlyList<PyValue> Lines(PyValue value) =>
        Assert.IsType<PyList>(MedalField(value, "lines")).Items;

    private static PyValue MedalField(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    private static long MedalNumber(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, got {value.GetType().Name}.")
    };
}
