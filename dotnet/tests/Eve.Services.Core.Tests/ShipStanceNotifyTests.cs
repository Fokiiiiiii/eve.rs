using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>
/// A stance set answers after its commit: <c>OnStanceActive</c> (shipID, stanceID) goes to the pilot, then a ship in space is sent again. A stance that did not
/// </summary>
public sealed class ShipStanceNotifyTests
{
    private const long ShipId = 74000001, CharacterId = 44;

    [Fact]
    public async Task A_changed_stance_is_announced_to_the_pilot_and_the_ship_in_space_is_sent_again_after_the_commit()
    {
        var sink = new RecordingSink();
        var session = Session(sink);
        var refreshedWith = new List<int>();
        IService service = new ShipStanceMgrService(
            (shipId, stanceId, characterId, _) => ValueTask.FromResult<ShipStanceChange?>(new(true, 1, Changed: true)),
            (refreshed, _) =>
            {
                refreshedWith.Add(sink.Packets.Count);
                Assert.Same(session, refreshed);
                return Task.CompletedTask;
            });

        Assert.Equal(new PyInt(1), await service.CallAsync(Call(session, new PyLong(ShipId), new PyInt(3))));

        var packet = Assert.Single(sink.Packets);
        Assert.Equal("OnStanceActive", NotificationName(packet));
        Assert.Equal([1], refreshedWith);
    }

    [Fact]
    public async Task An_unchanged_stance_announces_nothing_and_does_not_send_the_ship_again()
    {
        var sink = new RecordingSink();
        var session = Session(sink);
        var refreshes = 0;
        IService service = new ShipStanceMgrService(
            (_, _, _, _) => ValueTask.FromResult<ShipStanceChange?>(new(true, 2)),
            (_, _) =>
            {
                refreshes++;
                return Task.CompletedTask;
            });

        Assert.Equal(new PyInt(2), await service.CallAsync(Call(session, new PyLong(ShipId), new PyInt(2))));

        Assert.Empty(sink.Packets);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public async Task A_stance_is_not_switched_in_station_and_the_pilot_is_told_why()
    {
        var sink = new RecordingSink();
        var session = Session(sink);
        var changed = 0;
        IService service = new ShipStanceMgrService(
            (_, _, _, _) =>
            {
                changed++;
                return ValueTask.FromResult<ShipStanceChange?>(new(true, 1, Changed: true));
            },
            null,
            _ => false);

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call(session, new PyLong(ShipId), new PyInt(3))));

        Assert.Equal("CustomNotify", refusal.Error.ClassName);
        Assert.Equal(0, changed);
        Assert.Empty(sink.Packets);
    }

    private static ClientSession Session(RecordingSink sink)
    {
        var session = new ClientSession(sink) { UserId = 34, NodeId = 65450 };
        session.Set("charid", new PyInt(CharacterId));
        return session;
    }

    private static ServiceCall Call(ClientSession session, params PyValue[] args) => new()
    {
        Method = "SetShipStance",
        Args = args,
        Session = session,
    };

    private static string NotificationName(PyValue value)
    {
        var packet = Assert.IsType<PyObject>(value);
        var fields = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(fields[2]);
        var addressFields = Assert.IsType<PyTuple>(destination.Args).Items;
        return Assert.IsType<PyStr>(addressFields[1]).Value;
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
