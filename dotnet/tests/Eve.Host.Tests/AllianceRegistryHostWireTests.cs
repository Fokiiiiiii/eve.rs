using System.Text;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Corporations;
using Eve.Skills;
using Eve.Wallet;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Alliance_creation_emits_client_complete_fields_through_registered_host()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        await rig.FundAsync(characterId, 3_000_000_000m);
        await rig.SetSkillLevelAsync(characterId, 3732, 5);
        var wallets = new PgWalletStore(rig.Source, rig.WalletSchema);
        var store = new CorporationStore(rig.Source, rig.Characters, wallets,
            new WalletTransactions(rig.Source, rig.WalletSchema), new PgSkillStore(rig.Source, rig.SkillsSchema),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rig.CorporationsSchema);
        var corporation = await store.CreateAsync(characterId,
            new NewCorporation("Alliance Create", "ALCR", "", "", 0), $"host-alliance-create:{Guid.NewGuid():N}");
        var allianceRegistry = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("allianceRegistry"));
        var corpRegistry = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("corpRegistry"));
        ClientSession? actorSession = null;

        Start(configure: registry =>
        {
            registry.Register(allianceRegistry);
            registry.Register(corpRegistry);
        }, sessionOpened: session =>
        {
            actorSession = session;
            session.CharacterId = characterId;
            session.Set("charid", new PyLong(characterId));
            session.Set("corpid", new PyLong(corporation.Id));
            session.Set("corporationID", new PyLong(corporation.Id));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var createResult = await CallOverHostAfterQueuedNotifications(front, open, 1,
            "corpRegistry", "CreateAlliance", Py.Tuple(
                new PyWString("Host Alliance"), new PyWString("HOST"), new PyWString(""), new PyWString("")));
        Assert.NotNull(createResult);
        var allianceId = Number(createResult);
        var notification = ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
        Assert.Equal(("OnAllianceChanged", "ownerid"), (notification.Name, notification.IdType));
        var payload = Assert.IsType<PyTuple>(Assert.Single(notification.Args)).Items;
        Assert.Equal(allianceId, Number(payload[0]));
        var changes = Assert.IsType<PyDict>(payload[1]);
        Assert.Equal(18, changes.Entries.Count);
        Assert.Equal(new PyValue[] { PyNone.Instance, PyNone.Instance },
            Assert.IsType<PyList>(Assert.Single(changes.Entries,
                entry => T(entry.Key) == "warFactionID").Value).Items);

        Assert.Equal("OnAllianceMemberChanged",
            ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync())).Name);
        Assert.Equal("OnCorporationChanged",
            ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync())).Name);
        actorSession!.Set("allianceid", new PyLong(allianceId));
        actorSession.Set("allianceID", new PyLong(allianceId));
        const long toId = 98_000_002;
        Assert.Same(PyNone.Instance, await CallOverHostAfterQueuedNotifications(front, open, 2,
            "allianceRegistry", "SetRelationship", Py.Tuple(new PyReal(4.5), new PyLong(toId))));
        var relationshipNotice = ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
        Assert.Equal(("OnAllianceRelationshipChanged", "ownerid"),
            (relationshipNotice.Name, relationshipNotice.IdType));
        var relationshipPayload = Assert.IsType<PyTuple>(Assert.Single(relationshipNotice.Args)).Items;
        var relationshipChanges = Assert.IsType<PyDict>(relationshipPayload[2]);
        Assert.Equal(new PyValue[] { PyNone.Instance, new PyReal(4.5) },
            Assert.IsType<PyList>(Assert.Single(relationshipChanges.Entries,
                entry => T(entry.Key) == "relationship").Value).Items);

        var relationships = Assert.IsType<PyObjectEx>(await CallOverHostAfterQueuedNotifications(front, open, 3,
            "allianceRegistry", "GetRelationships", Py.Tuple([])));
        var relationshipRow = Assert.IsType<PyPackedRow>(Assert.Single(relationships.Dict).Value);
        Assert.Equal(new PyValue[] { new PyInt(toId), new PyReal(4.5) }, relationshipRow.Values);

        var missingAlliance = Packets.Call(open, 4, "allianceRegistry", "GetAlliance",
            Py.Tuple(new PyLong(allianceId + 1000)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(missingAlliance)));
        var missingAllianceReply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(missingAllianceReply, "exceptions.RuntimeError", "allianceRegistry.GetAlliance");
    }

    [Fact]
    public void Add_bulletin_keeps_client_keyword_arguments_and_positional_defaults()
    {
        var keywordCall = new ServiceCall
        {
            Method = "AddBulletin",
            Args = [new PyWString("title"), new PyWString("body")],
            Kwargs = Py.Dict(
                ("bulletinID", new PyLong(4_000_000_001)),
                ("editDateTime", new PyLong(134_273_341_290_000_000)),
                ("machoVersion", new PyInt(1))),
            Session = new ClientSession(),
        };

        var (keywordBulletinId, keywordEditDateTime) =
            AllianceRegistryService.BulletinEditArguments(keywordCall);
        Assert.Equal(new PyLong(4_000_000_001), keywordBulletinId);
        Assert.Equal(new PyLong(134_273_341_290_000_000), keywordEditDateTime);

        var positionalCall = new ServiceCall
        {
            Method = "AddBulletin",
            Args =
            [
                new PyWString("title"),
                new PyWString("body"),
                new PyInt(42),
                new PyLong(134_273_341_290_000_000),
            ],
            Session = new ClientSession(),
        };
        var (positionalBulletinId, positionalEditDateTime) =
            AllianceRegistryService.BulletinEditArguments(positionalCall);
        Assert.Equal(new PyInt(42), positionalBulletinId);
        Assert.Equal(new PyLong(134_273_341_290_000_000), positionalEditDateTime);

        var createCall = new ServiceCall
        {
            Method = "AddBulletin",
            Args = [new PyWString("title"), new PyWString("body")],
            Session = new ClientSession(),
        };
        var (createBulletinId, createEditDateTime) =
            AllianceRegistryService.BulletinEditArguments(createCall);
        Assert.Same(PyNone.Instance, createBulletinId);
        Assert.Same(PyNone.Instance, createEditDateTime);
    }

    [Fact]
    public async Task Alliance_registry_membership_error_uses_the_registered_host_route_with_macho_kwargs()
    {
        var allianceRegistry = new AllianceRegistryService(null!, null!, () => 0);
        Start(configure: registry => registry.Register(allianceRegistry));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = Py.Dict(("machoVersion", new PyInt(1)));
        var request = Packets.CallWithKwargs(
            open, 1, "allianceRegistry", "GetMembers", Py.Tuple([]), kwargs);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(reply, "eveexceptions.UserError", "CustomInfo");
        Assert.Contains("Alliance membership required.", Encoding.ASCII.GetString(reply.Payload.Span));
    }
    [Fact]
    public async Task Alliance_registry_validation_exception_is_wrapped_by_the_host_route()
    {
        var allianceRegistry = new AllianceRegistryService(null!, null!, () => 0);
        Start(configure: registry => registry.Register(allianceRegistry));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var request = Packets.Call(
            open, 1, "allianceRegistry", "GetRankedAlliances", Py.Tuple(new PyInt(-1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(reply, "exceptions.RuntimeError", "allianceRegistry.GetRankedAlliances");
    }

    [Fact]
    public async Task Alliance_registry_missing_alliance_is_an_error_not_a_null_row()
    {
        var allianceRegistry = new AllianceRegistryService(null!, null!, () => 0);
        Start(configure: registry => registry.Register(allianceRegistry));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var request = Packets.Call(open, 1, "allianceRegistry", "GetAlliance", Py.Tuple([]));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(reply, "exceptions.RuntimeError", "allianceRegistry.GetAlliance");
    }

}
