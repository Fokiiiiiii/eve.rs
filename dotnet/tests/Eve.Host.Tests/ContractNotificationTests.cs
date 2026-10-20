using Eve.Host.Persistence;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class ContractNotificationTests
{
    [Fact]
    public void Corporation_notifications_reach_only_connected_characters_in_the_owner_corporation()
    {
        var sessions = new CharacterSessions();
        var first = Session(sessions, 1001, 2001);
        var second = Session(sessions, 1002, 2001);
        _ = Session(sessions, 1003, 2002);

        var recipients = ContractNotifications.Recipients(
            new ContractNotifications.Payload("OnContractCreated", 77, 0, 2001, 0), sessions);

        Assert.Equal(2, recipients.Count);
        Assert.Contains(first, recipients);
        Assert.Contains(second, recipients);
    }

    [Fact]
    public void Alliance_notifications_reach_only_connected_characters_in_the_alliance()
    {
        var sessions = new CharacterSessions();
        var member = Session(sessions, 2001, 3001, 4001);
        _ = Session(sessions, 2002, 3002, 4001);
        _ = Session(sessions, 2003, 3003, 4002);

        var recipients = ContractNotifications.Recipients(
            new ContractNotifications.Payload("OnContractAssigned", 77, 0, 0, 4001), sessions);

        Assert.Equal(2, recipients.Count);
        Assert.Contains(member, recipients);
        Assert.Contains(sessions.All().Single(session => session.CharacterId == 2002), recipients);
    }

    private static ClientSession Session(CharacterSessions sessions, long characterId, long corporationId, long allianceId = 0)
    {
        var session = new ClientSession { CharacterId = characterId };
        session.Set("corpid", new PyLong(corporationId));
        session.Set("allianceid", new PyLong(allianceId));
        sessions.Enter(characterId, session);
        return session;
    }
}
