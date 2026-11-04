using Eve.Corporations;
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
    public void Corporation_changes_reach_only_sessions_authorized_for_the_changed_division()
    {
        using var source=NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest("Host=127.0.0.1;Username=postgres"));
        var transactions=new WalletTransactions(source);
        var sessions=new CharacterSessions();
        var allowedSink=new Sink();
        var otherDivisionSink=new Sink();
        var accountantSink=new Sink();
        var unprivilegedSink=new Sink();
        var outsiderSink=new Sink();
        var allowed=new ClientSession(allowedSink) { UserId=7,CharacterId=140000001 };
        allowed.Set("rolesAtAll",new PyInt(CorporationRoles.WalletTakeRole(1006)));
        var otherDivision=new ClientSession(otherDivisionSink) { UserId=8,CharacterId=140000002 };
        otherDivision.Set("rolesAtAll",new PyInt(CorporationRoles.WalletTakeRole(1000)));
        var accountant=new ClientSession(accountantSink) { UserId=9,CharacterId=140000005 };
        accountant.Set("rolesAtAll",new PyInt(CorporationRoles.Accountant));
        var unprivileged=new ClientSession(unprivilegedSink) { UserId=9,CharacterId=140000003 };
        allowed.Set("corpid",new PyInt(98000000));
        otherDivision.Set("corpid",new PyInt(98000000));
        accountant.Set("corpid",new PyInt(98000000));
        unprivileged.Set("corpid",new PyInt(98000000));
        var outsider=new ClientSession(outsiderSink) { UserId=10,CharacterId=140000004 };
        outsider.Set("corpid",new PyInt(98000001));
        sessions.Enter(140000001,allowed);
        sessions.Enter(140000002,otherDivision);
        sessions.Enter(140000005,accountant);
        sessions.Enter(140000003,unprivileged);
        sessions.Enter(140000004,outsider);
        using var announcer=new WalletAnnouncer(transactions,sessions);
        transactions.AnnounceCommitted(new WalletBalanceChange(98000000,1006,10m));
        var expected=ClientNotification.Build(7,allowed.NodeId,"OnAccountChange","*corpid&corpAccountKey",
            new PyValue[] { new PyStr("cash7"),new PyInt(98000000),new PyReal(10) },0);
        Assert.Equal(MarshalCodec.Encode(expected),MarshalCodec.Encode(Assert.Single(allowedSink.Packets)));
        Assert.Empty(otherDivisionSink.Packets);
        Assert.Empty(accountantSink.Packets);
        Assert.Empty(unprivilegedSink.Packets);
        Assert.Empty(outsiderSink.Packets);
    }
}
