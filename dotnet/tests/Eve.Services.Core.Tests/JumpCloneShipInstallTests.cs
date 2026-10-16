using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The ship clone installation through the jump clone service's dispatch (eve-logic <c>clonejumpsvc.py</c>; EveJS <c>jumpCloneRuntime.js</c>
/// 2586-2717): the offer, the accept and the cancel, the notifications each pilot hears in the reference's order, and the refusals.
/// </summary>
public sealed class JumpCloneShipInstallTests
{
    private const long Offering = 2001, Target = 2002, Ship = 3001, TargetShip = 3002;
    private const decimal Fee = 900000m;

    [Fact]
    public async Task An_offer_is_heard_by_the_target_and_its_accept_installs_the_clone_and_tells_both_pilots_in_the_reference_order()
    {
        var rig = new Rig();
        var (offering, target) = rig.Pilots();

        Assert.Same(PyNone.Instance, await rig.Call(offering, "OfferShipCloneInstallation", new PyLong(Target)));
        var offered = Assert.Single(Notifications(rig.TargetSink));
        Assert.Equal("OnShipJumpCloneInstallationOffered", offered.Name);
        Assert.Equal(new long[] { Offering, Target, Ship, 0 }, Payload(offered.Args));
        Assert.Empty(Notifications(rig.OfferingSink));

        Assert.Same(PyNone.Instance, await rig.Call(target, "AcceptShipCloneInstallation"));
        var install = Assert.Single(rig.Store.Installs);
        Assert.Equal((Target, Ship, "ship", Fee, Offering), (install.Character, install.Install.LocationId, install.Install.LocationKind, install.Install.Fee, install.Install.OfferingCharacterId));
        Assert.Equal(0, install.Install.OwnerCorporationId);

        // the pilot hears the completion first, then the clone caches; the offering pilot hears the completion
        Assert.Equal(new[] { "OnShipJumpCloneInstallationOffered", "OnShipJumpCloneInstallationDone", "OnJumpCloneCacheInvalidated",
            "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated" }, Notifications(rig.TargetSink).Select(n => n.Name).ToArray());
        Assert.Equal(new[] { "OnShipJumpCloneInstallationDone" }, Notifications(rig.OfferingSink).Select(n => n.Name).ToArray());
        Assert.Equal(new long[] { Offering, Target, Ship, 0 }, Payload(Notifications(rig.OfferingSink)[0].Args));

        // the offer is spent: a second accept finds none
        Assert.Equal("Clone install offer expired", Notify(await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Call(target, "AcceptShipCloneInstallation"))));
    }

    [Fact]
    public async Task A_cancel_by_either_pilot_tells_both_and_a_cancel_with_no_offer_is_not_an_error()
    {
        var rig = new Rig();
        var (offering, target) = rig.Pilots();
        Assert.Same(PyNone.Instance, await rig.Call(offering, "CancelShipCloneInstallation"));
        Assert.Empty(Notifications(rig.TargetSink));

        await rig.Call(offering, "OfferShipCloneInstallation", new PyLong(Target));
        Assert.Same(PyNone.Instance, await rig.Call(offering, "CancelShipCloneInstallation"));

        Assert.Equal(new[] { "OnShipJumpCloneInstallationCanceled" }, Notifications(rig.OfferingSink).Select(n => n.Name).ToArray());
        Assert.Equal(new[] { "OnShipJumpCloneInstallationOffered", "OnShipJumpCloneInstallationCanceled" }, Notifications(rig.TargetSink).Select(n => n.Name).ToArray());
        Assert.Equal("Clone install offer expired", Notify(await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Call(target, "AcceptShipCloneInstallation"))));
        Assert.Empty(rig.Store.Installs);
    }

    [Fact]
    public async Task An_offer_is_refused_with_the_client_label_when_the_ship_has_no_clone_vat_bay_or_the_target_is_out_of_range()
    {
        var rig = new Rig { OfferingHasBay = false };
        var (offering, _) = rig.Pilots();
        Assert.Equal("InviteClone1", (await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Call(offering, "OfferShipCloneInstallation", new PyLong(Target)))).Error.UserErrorMessage);

        var far = new Rig { Distance = 20000 };
        var (farOffering, _) = far.Pilots();
        Assert.Equal("InviteClone2", (await Assert.ThrowsAsync<MachoWrappedException>(() => far.Call(farOffering, "OfferShipCloneInstallation", new PyLong(Target)))).Error.UserErrorMessage);
        Assert.Empty(Notifications(far.TargetSink));
    }

    [Fact]
    public async Task An_accept_is_refused_when_the_bay_is_full_or_the_pilot_cannot_pay_and_installs_nothing()
    {
        var full = new Rig { Capacity = 0 };
        var (offering, target) = full.Pilots();
        await full.Call(offering, "OfferShipCloneInstallation", new PyLong(Target));
        Assert.Equal("Clone bay is full", Notify(await Assert.ThrowsAsync<MachoWrappedException>(() => full.Call(target, "AcceptShipCloneInstallation"))));

        var broke = new Rig();
        broke.Store.Balance = 1000m;
        var (brokeOffering, brokeTarget) = broke.Pilots();
        await broke.Call(brokeOffering, "OfferShipCloneInstallation", new PyLong(Target));
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => broke.Call(brokeTarget, "AcceptShipCloneInstallation"));
        Assert.Equal("NotEnoughMoney", refused.Error.UserErrorMessage);
        Assert.Empty(broke.Store.Installs);
    }

    private static string Notify(MachoWrappedException error)
    {
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(header.Items[1]).Items[1]);
        return Assert.IsType<PyStr>(args.Entries.Single(entry => ((PyStr)entry.Key).Value == "notify").Value).Value;
    }

    private static long[] Payload(PyValue[] args) =>
        Assert.IsType<PyList>(Assert.Single(args)).Items.Select(item => item switch
        {
            PyInt number => (long)number.Value,
            PyLong number => number.Value,
            _ => throw new InvalidOperationException("a payload id is an integer"),
        }).ToArray();

    private static List<(string Name, PyValue[] Args)> Notifications(Sink sink)
    {
        var events = new List<(string Name, PyValue[] Args)>();
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            events.Add((((PyStr)address[1]).Value, [.. args]));
        }
        return events;
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed record Settled(long Character, JumpCloneInstall Install);

    private sealed class FakeStore : IJumpCloneStore
    {
        public decimal Balance { get; set; } = 5_000_000m;
        public List<Settled> Installs { get; } = [];
        private long _nextId = 140000000001;

        public ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JumpCloneSnapshot(Installs.Where(i => i.Character == characterId)
                .Select(i => new JumpCloneRecord(_nextId, i.Install.LocationId, i.Install.LocationKind, "", [])).ToArray(), 0));

        public ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken) => throw new NotSupportedException();

        /// <summary>The two skills of the clone limit: Infomorph Psychology (24242) and Advanced (33407) at one, so a limit of two.</summary>
        public ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(typeId is 24242 or 33407 ? 1 : 0);

        public ValueTask<JumpCloneRecord?> InstallAsync(long characterId, JumpCloneInstall install, CancellationToken cancellationToken)
        {
            if (Balance < install.Fee)
                return ValueTask.FromResult<JumpCloneRecord?>(null);
            Installs.Add(new Settled(characterId, install));
            return ValueTask.FromResult<JumpCloneRecord?>(new JumpCloneRecord(_nextId++, install.LocationId, install.LocationKind, "", []));
        }

        public ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken) => ValueTask.FromResult(Balance);
        // the clone jump is not part of these installs (CloneJumpTests cover it): a jump here is refused
        public ValueTask<CloneJumpResult> JumpAsync(long characterId, long targetCloneId, long originLocationId, decimal fee, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CloneJumpResult("CLONE_NOT_FOUND"));
    }

    private sealed class FakeSpace(Rig rig) : IJumpCloneSpace
    {
        public ValueTask<ShipCloneBay?> ActiveShipAsync(ClientSession session, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ShipCloneBay?>(session.CharacterId switch
            {
                Offering => new ShipCloneBay(Ship, true, rig.OfferingHasBay, rig.Capacity, 10000),
                Target => new ShipCloneBay(TargetShip, true, false, 0, 0),
                _ => null,
            });

        public ValueTask<double?> DistanceAsync(ClientSession first, long firstShipId, ClientSession second, long secondShipId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<double?>(rig.Distance);

        public ClientSession? SessionOf(long characterId) => characterId switch
        {
            Offering => rig.OfferingSession,
            Target => rig.TargetSession,
            _ => null,
        };

        public ValueTask<StructureCloneBay?> StructureBayAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<StructureCloneBay?>(null);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Store = new FakeStore();
            Space = new FakeSpace(this);
            Service = new JumpCloneService(Store, new JumpCloneSpaceLink { Space = Space });
            OfferingSession = new ClientSession(OfferingSink) { CharacterId = Offering };
            TargetSession = new ClientSession(TargetSink) { CharacterId = Target };
            OfferingSession.Set("shipid", new PyLong(Ship));
            TargetSession.Set("shipid", new PyLong(TargetShip));
        }

        public bool OfferingHasBay { get; init; } = true;
        public int Capacity { get; init; } = 2;
        public double Distance { get; init; } = 5000;
        public FakeStore Store { get; }
        public FakeSpace Space { get; }
        public JumpCloneService Service { get; }
        public Sink OfferingSink { get; } = new();
        public Sink TargetSink { get; } = new();
        public ClientSession OfferingSession { get; }
        public ClientSession TargetSession { get; }

        public (ClientSession Offering, ClientSession Target) Pilots() => (OfferingSession, TargetSession);

        public async Task<PyValue> Call(ClientSession session, string method, params PyValue[] args) =>
            await ((IService)Service).CallAsync(new ServiceCall { Method = method, Args = args, Session = session });
    }
}
