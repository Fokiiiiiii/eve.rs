using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using System.Text;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Vote_manager_rpc_flow_uses_the_host_wire_and_persists_cases_and_ballots()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var id = Guid.NewGuid().ToString("N")[..10];
        var corporationSchema = "vot_" + id;
        var charactersSchema = "vch_" + id;
        var walletSchema = "vwl_" + id;
        var skillsSchema = "vsk_" + id;
        try
        {
            await PgCharacterStore.MigrateAsync(source, charactersSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillsSchema);
            await CorporationStore.MigrateAsync(source, corporationSchema);
            var characters = new PgCharacterStore(source, charactersSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillsSchema);
            var transactions = new WalletTransactions(source, walletSchema);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
            var store = new CorporationStore(source, characters, wallets, transactions, skills, () => now, corporationSchema);

            long founder;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, "Host Vote Founder", 0, 1, 1, 1, 11, 1376,
                        1000044, null, 60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }

            var corporation = await store.CreateAsync(founder, new("Host Vote Works", "HVOTE", "", "", 0m), "vote-manager-host");
            var errors = new List<string>();
            Start(
                configure: registry => registry.Register(new VoteManagerService(store)),
                onProblem: (_, error) => { if (error is not null) errors.Add(error.ToString()); },
                sessionOpened: session =>
                {
                    session.CharacterId = founder;
                    session.Set("corporationID", new Eve.Marshal.PyInt(corporation.Id));
                    session.Set("corpid", new Eve.Marshal.PyInt(corporation.Id));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            await front.ReceiveAsync();

            async Task<Eve.Marshal.PyValue> Rpc(long callId, string method, params Eve.Marshal.PyValue[] args)
            {
                await front.SendAsync(new Packet(open.SessionId,
                    MarshalCodec.Encode(Packets.Call(open, callId, "voteManager", method, args))));
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                try
                {
                    return Result(MarshalCodec.Decode(reply.Payload)) ?? throw new Xunit.Sdk.XunitException("voteManager reply had no return value");
                }
                catch (Xunit.Sdk.XunitException error)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"{method} failed over Host: {Encoding.ASCII.GetString(reply.Payload.Span)}\n{string.Join("\n", errors)}", error);
                }
            }

            Assert.Equal(Eve.Marshal.PyBool.True, await Rpc(1, "CanViewVotes", new Eve.Marshal.PyInt(corporation.Id)));
            Assert.Equal(Eve.Marshal.PyBool.False, await Rpc(2, "CanRunForCEO"));

            var voteCaseId = Assert.IsType<Eve.Marshal.PyInt>(await Rpc(3, "InsertVoteCase",
                new Eve.Marshal.PyWString("Host vote"), new Eve.Marshal.PyWString("Choose one"),
                new Eve.Marshal.PyInt(4),
                new Eve.Marshal.PyList([new Eve.Marshal.PyWString("For"), new Eve.Marshal.PyWString("Against")]),
                new Eve.Marshal.PyInt(1)));
            Assert.True(voteCaseId.Value > 0);

            var cases = Assert.IsType<Eve.Marshal.PyDict>(await Rpc(4, "GetVoteCasesByCorporation",
                new Eve.Marshal.PyInt(corporation.Id)));
            Assert.Single(RowsetRows(Assert.IsType<Eve.Marshal.PyDict>(await Rpc(12,
                "GetVoteCasesByCorporation", new Eve.Marshal.PyInt(corporation.Id), new Eve.Marshal.PyInt(0)))
                .Entries.Single(entry => entry.Key == new Eve.Marshal.PyInt(2)).Value).Items);
            var openCases = RowsetRows(cases.Entries.Single(entry => entry.Key == new Eve.Marshal.PyInt(2)).Value);
            Assert.Single(openCases.Items);
            Assert.Equal(voteCaseId, VoteCaseField(Assert.IsType<Eve.Marshal.PyList>(openCases.Items[0]), 0));

            var voteCase = Assert.IsType<Eve.Marshal.PyObject>(await Rpc(5, "GetVoteCase",
                new Eve.Marshal.PyInt(corporation.Id), voteCaseId));
            Assert.Equal("Host vote", Eve.Macho.JsSemantics.StringValue(VoteCaseObjectField(voteCase, "voteCaseText")));

            var options = RowsetRows(await Rpc(6, "GetVoteCaseOptions",
                new Eve.Marshal.PyInt(corporation.Id), voteCaseId));
            Assert.Equal(2, options.Items.Count);
            Assert.Equal(new Eve.Marshal.PyInt(0), VoteCaseField(Assert.IsType<Eve.Marshal.PyList>(options.Items[0]), 1));
            Assert.Equal(new Eve.Marshal.PyInt(1), VoteCaseField(Assert.IsType<Eve.Marshal.PyList>(options.Items[1]), 1));

            Assert.IsType<Eve.Marshal.PyNone>(await Rpc(7, "InsertVote",
                new Eve.Marshal.PyInt(corporation.Id), voteCaseId, new Eve.Marshal.PyInt(1)));
            var ballots = RowsetRows(await Rpc(8, "GetVotes", voteCaseId));
            var personalBallot = Assert.IsType<Eve.Marshal.PyList>(ballots.Items.Single());
            Assert.Equal(new Eve.Marshal.PyInt(1), personalBallot.Items[0]);

            Assert.Empty(Assert.IsType<Eve.Marshal.PyDict>(
                await Rpc(9, "GetCorpSanctionedActions")).Entries);

            await using var persisted = source.CreateCommand(
                $"SELECT option_id FROM {corporationSchema}.vote_ballots WHERE vote_case_id=$1 AND shareholder_id=$2");
            persisted.Parameters.AddWithValue(voteCaseId.Value);
            persisted.Parameters.AddWithValue(founder);
            Assert.Equal(1L, Convert.ToInt64(await persisted.ExecuteScalarAsync()));
        }
        finally
        {
            foreach (var schema in new[] { corporationSchema, charactersSchema, walletSchema, skillsSchema })
            {
                await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static Eve.Marshal.PyValue VoteCaseField(Eve.Marshal.PyList row, int index) => row.Items[index];

    private static Eve.Marshal.PyList RowsetRows(Eve.Marshal.PyValue value)
    {
        var rowset = Assert.IsType<Eve.Marshal.PyObject>(value);
        var args = Assert.IsType<Eve.Marshal.PyDict>(rowset.Args);
        return Assert.IsType<Eve.Marshal.PyList>(args.Entries.Single(entry =>
            Eve.Macho.JsSemantics.StringValue(entry.Key) == "lines").Value);
    }

    private static Eve.Marshal.PyValue VoteCaseObjectField(Eve.Marshal.PyObject value, string name)
    {
        var args = Assert.IsType<Eve.Marshal.PyDict>(value.Args);
        var header = Assert.IsType<Eve.Marshal.PyTuple>(args.Entries.Single(entry =>
            Eve.Macho.JsSemantics.StringValue(entry.Key) == "header").Value);
        var line = Assert.IsType<Eve.Marshal.PyTuple>(args.Entries.Single(entry =>
            Eve.Macho.JsSemantics.StringValue(entry.Key) == "line").Value);
        var index = header.Items.ToList().FindIndex(column => Eve.Macho.JsSemantics.StringValue(column) == name);
        Assert.True(index >= 0);
        return line.Items[index];
    }
}
