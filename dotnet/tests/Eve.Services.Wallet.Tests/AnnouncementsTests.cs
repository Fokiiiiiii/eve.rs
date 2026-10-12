using Eve.Macho;
using Eve.Marshal;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet.Tests;

public class AnnouncementsTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
    [Fact]
    public void The_character_is_told_the_reference_account_change_shape()
    {
        using var source=NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest("Host=127.0.0.1;Username=postgres"));
        var transactions=new WalletTransactions(source);
        var sessions=new CharacterSessions();
        var sink=new Sink();
        var session=new ClientSession(sink) { UserId=7,CharacterId=140000001 };
        sessions.Enter(140000001,session);
        using var announcer=new WalletAnnouncer(transactions,sessions);
        transactions.AnnounceCommitted(new WalletBalanceChange(140000001,1000,123.45m));
        var expected=ClientNotification.Build(7,session.NodeId,"OnAccountChange","clientID",
            new PyValue[] { new PyStr("cash"),new PyInt(140000001),new PyReal(123.45) },0);
        Assert.Equal(MarshalCodec.Encode(expected),MarshalCodec.Encode(Assert.Single(sink.Packets)));
    }
    [Fact]
    public void Corporation_changes_reach_its_members_with_the_correct_division_key()
    {
        using var source=NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest("Host=127.0.0.1;Username=postgres"));
        var transactions=new WalletTransactions(source);
        var sessions=new CharacterSessions();
        var memberSink=new Sink();
        var outsiderSink=new Sink();
        var member=new ClientSession(memberSink) { UserId=7,CharacterId=140000001 };
        member.Set("corpid",new PyInt(98000000));
        var outsider=new ClientSession(outsiderSink) { UserId=8,CharacterId=140000002 };
        outsider.Set("corpid",new PyInt(98000001));
        sessions.Enter(140000001,member);
        sessions.Enter(140000002,outsider);
        using var announcer=new WalletAnnouncer(transactions,sessions);
        transactions.AnnounceCommitted(new WalletBalanceChange(98000000,1006,10m));
        var expected=ClientNotification.Build(7,member.NodeId,"OnAccountChange","*corpid&corpAccountKey",
            new PyValue[] { new PyStr("cash7"),new PyInt(98000000),new PyReal(10) },0);
        Assert.Equal(MarshalCodec.Encode(expected),MarshalCodec.Encode(Assert.Single(memberSink.Packets)));
        Assert.Empty(outsiderSink.Packets);
    }
}
