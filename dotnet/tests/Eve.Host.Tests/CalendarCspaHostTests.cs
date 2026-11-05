using System.Globalization;
using System.Text;
using Eve.Calendar;
using Eve.Data;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Wallet;
using Eve.Wallet;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Calendar_personal_invites_with_zero_cost_or_an_exempt_recipient_do_not_debit()
    {
        var ownerId = Random.Shared.NextInt64(6_000_000_000_000L, 9_000_000_000_000_000L);
        var zeroCostInvitee = ownerId + 1;
        var exemptInvitee = ownerId + 2;
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(
            PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        await PgWalletStore.MigrateAsync(dataSource);
        var wallets = await SeedWalletAsync(dataSource, ownerId);
        var costs = new Dictionary<long, int?> { [zeroCostInvitee] = 0, [exemptInvitee] = 500 };
        var billing = new CalendarCspaBilling(
            (id, _) => ValueTask.FromResult<int?>(costs.GetValueOrDefault(id) ?? 0),
            (recipientId, _, _) => ValueTask.FromResult(recipientId == exemptInvitee
                ? CalendarCspaRecipientRule.Exempt
                : CalendarCspaRecipientRule.Charge),
            wallets);
        var store = new CalendarStore(dataSource);
        Start(
            strictRpc: true,
            configure: registry => registry.Register(new CalendarMgrService(
                store, cspaBilling: billing, nowUnixMs: () => FixedNowMs)),
            sessionOpened: session => session.Set("charid", new PyLong(ownerId)));

        var (front, open) = await OpenCalendarFrontAsync(405);
        var date = DateTime.UtcNow.AddDays(2);
        var args = CreatePersonalEventArgs(date, zeroCostInvitee, exemptInvitee);
        var eventId = WireInteger(await HostRpcAsync(
            front, open, 1, "calendarMgr", "CreatePersonalEvent", args));

        Assert.Single(await store.ListMonthAsync(ownerId, 0, 0, date.Month, date.Year));
        Assert.Equal(new[] { ownerId, zeroCostInvitee, exemptInvitee }.Order(),
            (await store.GetResponsesToEventAsync(eventId, ownerId)).Select(row => row.CharacterId).Order());
        Assert.Equal(1_000_000m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Empty(await CspaJournalAsync(wallets, ownerId));
    }

    [PgFact]
    public async Task Calendar_personal_CSPA_decline_invalid_approval_retry_and_repeated_add_are_atomic()
    {
        var ownerId = Random.Shared.NextInt64(6_000_000_000_000L, 9_000_000_000_000_000L);
        var declinedInvitee = ownerId + 1;
        var firstInvitee = ownerId + 2;
        var secondInvitee = ownerId + 3;
        var addedInvitee = ownerId + 4;
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(
            PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        await PgWalletStore.MigrateAsync(dataSource);
        var wallets = await SeedWalletAsync(dataSource, ownerId);
        var sessions = new CharacterSessions();
        using var announcer = new WalletAnnouncer(wallets, sessions);
        var costs = new Dictionary<long, int?>
        {
            [declinedInvitee] = 200,
            [firstInvitee] = 600,
            [secondInvitee] = 400,
            [addedInvitee] = 500,
        };
        var billing = new CalendarCspaBilling(
            (id, _) => ValueTask.FromResult<int?>(costs.GetValueOrDefault(id) ?? 0),
            (_, _, _) => ValueTask.FromResult(CalendarCspaRecipientRule.Charge),
            wallets);
        var store = new CalendarStore(dataSource);
        var announcements = 0;
        wallets.BalanceCommitted += _ => Interlocked.Increment(ref announcements);
        Start(
            strictRpc: true,
            configure: registry => registry.Register(new CalendarMgrService(
                store, sessions: sessions, cspaBilling: billing, nowUnixMs: () => FixedNowMs)),
            sessionOpened: session =>
            {
                session.Set("charid", new PyLong(ownerId));
                sessions.Enter(ownerId, session);
            });

        var (front, open) = await OpenCalendarFrontAsync(406);
        var date = DateTime.UtcNow.AddDays(2);

        // The client decline path sends no retry. The denied call must leave no event or debit.
        var declined = await SendCalendarCallAsync(front, open, 1, "calendarMgr", "CreatePersonalEvent",
            CreatePersonalEventArgs(date, declinedInvitee));
        AssertContactCostNotApproved(declined, 200);
        Assert.Empty(await store.ListMonthAsync(ownerId, 0, 0, date.Month, date.Year));
        Assert.Equal(1_000_000m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Empty(await CspaJournalAsync(wallets, ownerId));
        Assert.Equal(0, announcements);

        // One error is shown for all charged invitees; too-small and malformed approval do not mutate.
        var paidCreateArgs = CreatePersonalEventArgs(date, firstInvitee, secondInvitee);
        var approval = await SendCalendarCallAsync(front, open, 2, "calendarMgr", "CreatePersonalEvent", paidCreateArgs);
        AssertContactCostNotApproved(approval, 1_000);
        var insufficient = await SendCalendarCallWithKwargsAsync(front, open, 3, "CreatePersonalEvent",
            new PyLong(999), paidCreateArgs);
        AssertContactCostNotApproved(insufficient, 1_000);
        var malformed = await SendCalendarCallWithKwargsAsync(front, open, 4, "CreatePersonalEvent",
            new PyStr("1000"), paidCreateArgs);
        AssertContactCostNotApproved(malformed, 1_000);
        Assert.Empty(await store.ListMonthAsync(ownerId, 0, 0, date.Month, date.Year));
        Assert.Equal(1_000_000m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Empty(await CspaJournalAsync(wallets, ownerId));
        Assert.Equal(0, announcements);

        var eventId = WireInteger(await HostRpcWithKwargsAndWalletChangeAsync(
            front, open, 5, "CreatePersonalEvent", new PyLong(1_000), paidCreateArgs, ownerId, 999_000m));
        var invitees = await store.GetResponsesToEventAsync(eventId, ownerId);
        Assert.Equal(3, invitees.Count);
        var createCharges = await CspaJournalAsync(wallets, ownerId);
        Assert.Equal(new[] { firstInvitee, secondInvitee }.Order(),
            createCharges.Select(entry => entry.ReferenceId!.Value).Order());
        Assert.Equal(new[] { -600m, -400m }.Order(),
            createCharges.Select(entry => entry.Amount).Order());
        Assert.Equal(1_000, createCharges.Sum(entry => -(int)entry.Amount));
        Assert.Equal(999_000m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Equal(1, announcements);

        // Participant additions use the same approval flow and only charge newly added recipients.
        var updateEventId = WireInteger(await HostRpcAsync(
            front, open, 6, "calendarMgr", "CreatePersonalEvent",
            CreatePersonalEventArgs(date, Array.Empty<long>())));
        var addArgs = new PyValue[]
        {
            new PyLong(updateEventId),
            new PyTuple([new PyLong(addedInvitee)]),
            new PyTuple([]),
        };
        var addApproval = await SendCalendarCallAsync(
            front, open, 7, "calendarMgr", "UpdateEventParticipants", addArgs);
        AssertContactCostNotApproved(addApproval, 500);
        Assert.Single(await store.GetResponsesToEventAsync(updateEventId, ownerId));
        Assert.Equal(999_000m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Equal(1, announcements);

        var addInsufficient = await SendCalendarCallWithKwargsAsync(
            front, open, 8, "UpdateEventParticipants", new PyLong(499), addArgs);
        AssertContactCostNotApproved(addInsufficient, 500);
        Assert.Single(await store.GetResponsesToEventAsync(updateEventId, ownerId));
        Assert.Equal(999_000m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Equal(1, announcements);

        Assert.IsType<PyNone>(await HostRpcWithKwargsAndWalletChangeAsync(
            front, open, 9, "UpdateEventParticipants", new PyLong(500), addArgs, ownerId, 998_500m));
        Assert.Equal(2, (await store.GetResponsesToEventAsync(updateEventId, ownerId)).Count);
        Assert.Equal(998_500m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Equal(3, (await CspaJournalAsync(wallets, ownerId)).Count);
        Assert.Equal(2, (await store.ListMonthAsync(ownerId, 0, 0, date.Month, date.Year)).Count);
        Assert.Equal(2, announcements);

        // Repeating the same addition sees an active participant and does not debit again.
        Assert.IsType<PyNone>(await HostRpcAsync(
            front, open, 10, "calendarMgr", "UpdateEventParticipants", addArgs));
        Assert.Equal(2, (await store.GetResponsesToEventAsync(updateEventId, ownerId)).Count);
        Assert.Equal(998_500m, await ReadWalletBalanceAsync(dataSource, ownerId));
        Assert.Equal(3, (await CspaJournalAsync(wallets, ownerId)).Count);
        Assert.Equal(2, announcements);
    }

    private static PyValue[] CreatePersonalEventArgs(DateTime date, params long[] invitees) =>
    [
        new PyLong(date.ToFileTimeUtc()),
        PyNone.Instance,
        new PyStr("CSPA test event"),
        PyNone.Instance,
        new PyInt(0),
        new PyTuple(invitees.Select(id => (PyValue)new PyLong(id)).ToArray()),
    ];

    private async Task<Send> SendCalendarCallWithKwargsAsync(
        TestFront front, SessionOpen open, long callId, string method, PyValue approvedCost, PyValue[] args)
    {
        var kwargs = new PyDict([
            .. TransportKwargs().Entries,
            new KeyValuePair<PyValue, PyValue>(new PyStr("approvedCost"), approvedCost),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, "calendarMgr", method, new PyTuple(args), kwargs))));
        return Assert.IsType<Send>(await front.ReceiveAsync());
    }

    private async Task<PyValue> HostRpcWithKwargsAsync(
        TestFront front, SessionOpen open, long callId, string method, PyValue approvedCost, PyValue[] args)
    {
        var send = await SendCalendarCallWithKwargsAsync(front, open, callId, method, approvedCost, args);
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.CallRsp, packet.Type);
        return Result(decoded)!;
    }

    private async Task<PyValue> HostRpcWithKwargsAndWalletChangeAsync(
        TestFront front, SessionOpen open, long callId, string method, PyValue approvedCost, PyValue[] args,
        long ownerId, decimal expectedBalance)
    {
        var kwargs = new PyDict([
            .. TransportKwargs().Entries,
            new KeyValuePair<PyValue, PyValue>(new PyStr("approvedCost"), approvedCost),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, "calendarMgr", method, new PyTuple(args), kwargs))));
        AssertWalletBalanceChange(Assert.IsType<Send>(await front.ReceiveAsync()), ownerId, expectedBalance);
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.CallRsp, packet.Type);
        return Result(decoded)!;
    }

    private static void AssertWalletBalanceChange(Send send, long ownerId, decimal expectedBalance)
    {
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnAccountChange", packet.Dest.BroadcastId);
        Assert.Equal("clientID", packet.Dest.IdType);
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(decoded).Args).Items;
        var envelope = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(fields[4]).Items[0]);
        var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(
            Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
        Assert.Equal(3, args.Items.Count);
        Assert.Equal("cash", TextValue(args.Items[0]));
        Assert.Equal(new PyLong(ownerId), args.Items[1]);
        Assert.Equal(new PyReal((double)expectedBalance), args.Items[2]);
    }

    private static void AssertContactCostNotApproved(Send send, long expectedCost)
    {
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        var response = Assert.IsType<PyTuple>(packet.Payload);
        var wrapped = Assert.IsType<PyTuple>(response.Items[2]);
        var errorStream = Assert.IsType<PySubStream>(wrapped.Items[0]);
        var userError = Assert.IsType<PyObjectEx>(errorStream.Decoded);
        var header = Assert.IsType<PyTuple>(userError.Header);
        Assert.Equal("eveexceptions.UserError", Assert.IsType<PyToken>(header.Items[0]).Value);
        var args = Assert.IsType<PyTuple>(header.Items[1]);
        Assert.Equal("ContactCostNotApproved", TextValue(args.Items[0]));
        var info = Assert.IsType<PyDict>(args.Items[1]);
        Assert.Equal(expectedCost, Assert.IsType<PyLong>(DictValue(info, "totalCost")).Value);
        Assert.Equal(expectedCost.ToString("N2", CultureInfo.InvariantCulture) + " ISK",
            TextValue(DictValue(info, "totalCostISK")));
    }

    private static PyValue DictValue(PyDict dictionary, string key) => dictionary.Entries
        .Single(entry => TextValue(entry.Key) == key).Value;

    private static string TextValue(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new Xunit.Sdk.XunitException($"Expected string payload, got {value.GetType().Name}.")
    };

    private static async Task<WalletTransactions> SeedWalletAsync(Npgsql.NpgsqlDataSource dataSource, long ownerId)
    {
        var wallets = new WalletTransactions(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await wallets.EnsureAsync(connection, transaction, ownerId);
        await wallets.AdjustAsync(connection, transaction, ownerId, 1_000_000, 0, 1,
            "calendar CSPA test seed", FixedNowMs, $"calendar-cspa-seed:{ownerId}");
        await transaction.CommitAsync();
        return wallets;
    }

    private static async Task<decimal> ReadWalletBalanceAsync(Npgsql.NpgsqlDataSource dataSource, long ownerId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT isk FROM wallet.balances WHERE owner_id = $1 AND account_key = 1000");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<JournalEntry>> CspaJournalAsync(WalletTransactions wallets, long ownerId) =>
        (await wallets.JournalForAccountAsync(ownerId, 1000, limit: 1000))
        .Where(entry => entry.RefTypeId == 35)
        .ToArray();
}
