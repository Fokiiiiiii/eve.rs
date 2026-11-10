using Eve.Data;
using Eve.Host;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Space;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Ess_main_bank_calls_use_host_wire_and_persist_state_with_client_notifications()
    {
        const int systemId = 30_000_142;
        const long characterId = 140_000_001;
        const long shipId = 98_000_012;
        const long essId = 1_030_000_000_001;
        const long now = 1_700_000_000_000;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var walletSchema = "ew_" + suffix;
        var essSchema = "es_" + suffix;
        try
        {
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgEssStore.MigrateAsync(source, essSchema);
            var store = new PgEssStore(
                source,
                new WalletTransactions(source, walletSchema),
                essSchema,
                holderOf: (_, holderCharacterId, _, _) => ValueTask.FromResult<EssHolder?>(
                    holderCharacterId == characterId ? new(true, true, 50, shipId) : null),
                consumeKey: (_, _, _, _, _, _) => ValueTask.FromResult(true),
                quadrantOf: _ => EssReserveKeys.All[0].Quadrant);
            await store.RegisterAsync(systemId, essId, ownerId: 98_000_001, now);
            await store.CreditAsync(systemId, 10_000, 2_000);

            var service = new EssMgrService(new EssMgrCallbacks(
                ReadSystemStateAsync: async (requestedSystemId, cancellationToken) =>
                {
                    var state = requestedSystemId == systemId
                        ? await store.ReadAsync(systemId, cancellationToken)
                        : null;
                    return state is null ? null : PgEssStore.ToClient(state);
                },
                ExecuteAsync: async (request, cancellationToken) => await store.ExecuteAsync(request, now, cancellationToken)));
            Eve.Services.ClientSession? openedSession = null;
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    openedSession = session;
                    session.CharacterId = characterId;
                    session.Set("solarsystemid2", new PyInt(systemId));
                    session.Set("shipid", new PyLong(shipId));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            await front.ReceiveAsync();
            Assert.NotNull(openedSession);

            long callId = 0;
            async Task<PyValue> Rpc(string method, params PyValue[] args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                    Packets.Call(open, ++callId, "essMgr", method, args))));
                while (true)
                {
                    var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                    var response = MarshalCodec.Decode(reply.Payload);
                    var packet = Assert.IsType<MachoPacket>(
                        MachoPacket.Decode(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args)));
                    if (packet.Type == MachoMessage.CallRsp)
                    {
                        return Result(response)
                            ?? throw new Xunit.Sdk.XunitException($"{method} had no Host RPC result");
                    }
                    Assert.Contains(packet.Type, new[]
                    {
                        MachoMessage.Notification,
                        MachoMessage.SessionChangeNotification,
                        MachoMessage.SessionInitialStateNotification,
                    });
                }
            }

            async Task<PyTuple> AssertNotification(string name)
            {
                var sent = Assert.IsType<Send>(await front.ReceiveAsync());
                var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(sent.Payload)).Args);
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(fields));
                Assert.Equal(MachoMessage.Notification, packet.Type);
                Assert.Equal(name, packet.Dest.BroadcastId);
                Assert.Equal("clientID", packet.Dest.IdType);
                var payload = Assert.IsType<PyTuple>(packet.Payload);
                var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(payload.Items[0]).Items[1]);
                var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
                return Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(inner.Items[1]).Items[1]);
            }

            var initial = Assert.IsType<PyDict>(await Rpc("GetDataForClientSolarSystem"));
            Assert.Equal(new PyInt(systemId), Field(initial, "solarSystemID"));
            Assert.Equal(new PyInt(10_000), Field(initial, "mainValue"));
            Assert.Equal(new PyInt(2_000), Field(initial, "reserveValue"));
            Assert.Empty(Assert.IsType<PyDict>(Field(initial, "mainBankLink")).Entries);
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(now + EssRules.AutoPayIntervalMs).ToFileTime()),
                Field(initial, "nextScheduledAutopayment"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(now).ToFileTime()),
                Field(initial, "lastAutopayment"));
            Assert.Same(PyBool.False, await Rpc("IsClientLinkedToReserveBank"));
            Assert.Empty(Assert.IsType<PyList>(await Rpc("GetMainBankTheftsForClientSolarSystem")).Items);
            Assert.Empty(Assert.IsType<PyList>(await Rpc("GetReserveBankTheftsForClientSolarSystem")).Items);

            Assert.Same(PyNone.Instance, await Rpc("AttemptLinkToMainBank"));
            var linkArgs = await AssertNotification("OnESSMainBankLinkNotification");
            Assert.Equal(new PyInt(systemId), linkArgs.Items[0]);
            var changedArgs = await AssertNotification("OnSystemESSDataChanged");
            Assert.Equal(new PyInt(systemId), changedArgs.Items[0]);
            var linkNotificationData = Assert.IsType<PyDict>(changedArgs.Items[1]);
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(now + EssRules.AutoPayIntervalMs).ToFileTime()),
                Field(linkNotificationData, "nextScheduledAutopayment"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(now).ToFileTime()),
                Field(linkNotificationData, "lastAutopayment"));
            var linked = await store.ReadAsync(systemId);
            Assert.NotNull(linked?.MainLink);
            Assert.Equal(characterId, linked!.MainLink!.CharacterId);
            Assert.Equal(shipId, linked.MainLink.ShipId);

            var payload = Assert.IsType<PyDict>(await Rpc("GetDataForClientSolarSystem"));
            var mainLink = Assert.IsType<PyDict>(Field(payload, "mainBankLink"));
            Assert.Equal(linked.MainLink.Id, T(Field(mainLink, "linkID")));
            Assert.Equal(new PyInt(characterId), Field(mainLink, "characterID"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(now).ToFileTime()), Field(mainLink, "startedAt"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(now + EssRules.LinkDurationMs).ToFileTime()),
                Field(mainLink, "completesAt"));

            Assert.Same(PyNone.Instance, await Rpc("AttemptLinkToMainBank"));
            var stillLinked = await store.ReadAsync(systemId);
            Assert.NotNull(stillLinked?.MainLink);
            Assert.Equal(linked.MainLink.Id, stillLinked!.MainLink!.Id);

            Assert.Same(PyNone.Instance, await Rpc("RequestMainBankUnlink"));
            var disconnectArgs = await AssertNotification("OnMainBankPlayerLinkDisconnected");
            var disconnect = Assert.IsType<PyDict>(disconnectArgs.Items[0]);
            Assert.Equal(new PyInt(systemId), Field(disconnect, "solarSystemID"));
            Assert.Equal(new PyInt(characterId), Field(disconnect, "characterID"));
            Assert.Equal(linked.MainLink.Id, T(Field(disconnect, "linkID")));
            Assert.Equal(new PyInt(0), Field(disconnect, "reason"));
            changedArgs = await AssertNotification("OnSystemESSDataChanged");
            Assert.Equal(new PyInt(systemId), changedArgs.Items[0]);
            Assert.Empty(Assert.IsType<PyDict>(Field(Assert.IsType<PyDict>(changedArgs.Items[1]), "mainBankLink")).Entries);
            var unlinked = await store.ReadAsync(systemId);
            Assert.Null(unlinked?.MainLink);
            Assert.Empty(Assert.IsType<PyList>(await Rpc("GetMainBankTheftsForClientSolarSystem")).Items);

            var reserveKey = EssReserveKeys.All[0];
            Assert.Same(PyNone.Instance, await Rpc("RequestUnlockReserveBank", new PyInt(reserveKey.KeyTypeId)));
            changedArgs = await AssertNotification("OnSystemESSDataChanged");
            var opened = await store.ReadAsync(systemId);
            Assert.Equal(reserveKey.TotalPulses, opened!.PulsesRemaining);
            Assert.Equal(new PyInt(reserveKey.TotalPulses),
                Field(Assert.IsType<PyDict>(changedArgs.Items[1]), "reserveBankPulsesRemaining"));

            Assert.Same(PyNone.Instance, await Rpc("AttemptLinkToReserveBank"));
            linkArgs = await AssertNotification("OnESSReserveBankLinkNotification");
            Assert.Equal(new PyInt(systemId), linkArgs.Items[0]);
            changedArgs = await AssertNotification("OnSystemESSDataChanged");
            Assert.Equal(new PyInt(systemId), changedArgs.Items[0]);
            Assert.True((await store.ReadAsync(systemId))!.ReserveLinked.Contains(characterId));
            Assert.Same(PyBool.True, await Rpc("IsClientLinkedToReserveBank"));

            Assert.Same(PyNone.Instance, await Rpc("RequestReserveBankUnlink"));
            var reserveDisconnect = await AssertNotification("OnReserveBankPlayerLinkDisconnected");
            var reserveDisconnectData = Assert.IsType<PyDict>(reserveDisconnect.Items[0]);
            Assert.Equal(new PyInt(systemId), Field(reserveDisconnectData, "solarSystemID"));
            Assert.Equal(new PyInt(characterId), Field(reserveDisconnectData, "characterID"));
            Assert.Equal(new PyInt(0), Field(reserveDisconnectData, "reason"));
            changedArgs = await AssertNotification("OnSystemESSDataChanged");
            Assert.Equal(new PyInt(systemId), changedArgs.Items[0]);
            Assert.False((await store.ReadAsync(systemId))!.ReserveLinked.Contains(characterId));
            Assert.Same(PyBool.False, await Rpc("IsClientLinkedToReserveBank"));
        }
        finally
        {
            foreach (var schema in new[] { walletSchema, essSchema })
            {
                await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
