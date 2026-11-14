using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using System.Reflection;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Beyonce_unresolved_warp_subjects_are_refused_through_the_registered_host_wire()
    {
        const long characterId = 90_000_010;
        const long systemId = 31_000_004;
        const long shipId = 80_000_010;
        var simulation = DispatchProxy.Create<ISimulation, MovementSimulation>();
        var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]),
            new SolarSystemCatalog([new SolarSystemInfo((int)systemId, "Test", 20_000_001, 10_000_001, 0.5)]), () => FixedNowMs);
        Start(
            configure: registry => registry.Register(new BeyonceService(space, () => FixedNowMs)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
                space.EnterPilotForTest(session, characterId, shipId, 587, (int)systemId);
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 1;

        async Task<string> WarpFailureCode(string subject)
        {
            var nested = Py.Tuple(new PyStr("CmdWarpToStuff"), Py.Tuple(new PyStr(subject), new PyLong(77_000_001)), new PyDict([]));
            var bindArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, "beyonce", "MachoBindObject", bindArgs, kwargs))));

            var packet = MachoPacket.Decode(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            Assert.NotNull(packet);
            Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
            var state = Assert.IsType<PyTuple>(packet.Payload).Items;
            Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(state[1]).Value);
            var wrapped = Assert.IsType<PyTuple>(state[2]).Items;
            var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
            var header = Assert.IsType<PyTuple>(userError.Header).Items;
            Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
            var args = Assert.IsType<PyTuple>(header[1]).Items;
            return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
        }

        foreach (var subject in new[] { "moonMiningPoint", "operationDungeon", "externalDungeon", "epinstance", "mercenaryDen" })
            Assert.Equal("CustomNotify", await WarpFailureCode(subject));
    }

    [PgFact]
    public async Task Beyonce_moon_mining_point_warp_resolves_the_client_moon_id_through_the_registered_host_wire()
    {
        const long moonId = 40_491_234;
        const int systemId = 30_100_032;
        var simulation = DispatchProxy.Create<ISimulation, WarpCommandSimulation>();
        var recorder = (WarpCommandSimulation)(object)simulation;
        await using var rig = await AbyssalHostFlowTests.Rig.Create(simulation);
        var run = await rig.CreateRun(enter: false);
        var character = await rig.Characters.GetAsync(run.Data.CreatorCharacterId) ?? throw new InvalidOperationException();
        var shipId = character.ShipId ?? throw new InvalidOperationException();
        var mapSystem = (await rig.Sde.ReadAsync("mapSolarSystems", [systemId])).Single().Doc;
        await rig.Transaction(async (connection, transaction) =>
        {
            await rig.Characters.SetLocationAsync(connection, transaction, character.Id, systemId,
                mapSystem.GetProperty("constellationID").GetInt32(), mapSystem.GetProperty("regionID").GetInt32());
            await rig.Items.MoveAsync(connection, transaction, shipId, systemId, Eve.Ships.CharacterShips.InSpaceFlag);
        });
        var ship = await rig.Ships.LoadAsync(character.Id) ?? throw new InvalidOperationException();
        var point = MoonMiningPointCatalog.Resolve(moonId, systemId) ?? throw new InvalidOperationException();
        var service = new BeyonceService(rig.Space, () => rig.Now)
        {
            MoonMiningPointForSystem = async (requestedMoonId, requestedSystemId, token) =>
            {
                Assert.Equal(moonId, requestedMoonId);
                Assert.Equal(systemId, requestedSystemId);
                return await Eve.Host.CharacterServicesComposition.ResolveMoonMiningPointForSystemAsync(
                    rig.Sde, requestedMoonId, requestedSystemId, token);
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = character.Id;
                session.Set("charid", new PyLong(character.Id));
                session.Set("solarsystemid", new PyInt(systemId));
                session.Set("solarsystemid2", new PyInt(systemId));
                session.Set("locationid", new PyInt(systemId));
                rig.Space.EnterPilotForTest(session, character.Id, ship.Ship.Id, ship.Ship.TypeId, systemId);
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var warpKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("minRange"), new PyInt(35_000)),
        ]);
        var nested = Py.Tuple(new PyStr("CmdWarpToStuff"), Py.Tuple(new PyStr("moonMiningPoint"), new PyLong(moonId)), warpKwargs);
        var bindArgs = Py.Tuple(Py.Tuple(new PyInt(systemId), new PyInt(6)), nested);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", bindArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Same(PyNone.Instance, response.Items[1]);
        var command = Assert.Single(recorder.Commands, item => item.Command == SimCommands.WarpToPoint);
        Assert.Equal(new[] { point.X, point.Y, point.Z, 35_000d }, command.Args);
    }

    [Fact]
    public async Task Beyonce_refused_bind_returns_the_bound_pair_expected_by_the_client_moniker()
    {
        const long sessionSystemId = 31_000_006;
        const long requestedSystemId = 31_000_007;
        var service = new BeyonceService(null!, () => FixedNowMs);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("solarsystemid", new PyLong(sessionSystemId)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var nested = Py.Tuple(new PyStr("CmdWarpToStuff"), Py.Tuple(), new PyDict([]));
        var bindArgs = Py.Tuple(Py.Tuple(new PyLong(requestedSystemId), new PyInt(6)), nested);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", bindArgs, kwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(2, response.Items.Count);
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
    }

    private class WarpCommandSimulation : DispatchProxy
    {
        public List<(ulong SessionId, byte Command, double[] Args)> Commands { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name == "CommandAsync" && args is [ulong sessionId, byte command, double[] values, CancellationToken])
            {
                Commands.Add((sessionId, command, values));
                return Task.FromResult(new CommandReply(true, 1, null));
            }
            if (method.Name == "BindAsync") return Task.FromResult(new CommandReply(true, 1, null));
            if (method.Name == "get_IsConnected") return true;
            if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            if (method.ReturnType == typeof(void)) return null;
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(Task<string>)) return Task.FromResult<string?>(null);
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private class InertSimulation : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType == typeof(Task<CommandReply>)) return Task.FromResult(new CommandReply(true, 1, null));
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private class MovementSimulation : DispatchProxy
    {
        public List<(ulong SessionId, byte Command, double[] Args)> Commands { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Command" && args is [ulong sessionId, byte command, double[] values])
                Commands.Add((sessionId, command, values));
            if (method?.Name == "BindAsync") return Task.FromResult(new CommandReply(true, 1, null));
            return method?.ReturnType == typeof(bool) ? true : null;
        }
    }

    [Fact]
    public async Task Beyonce_align_to_resolves_and_issues_the_client_bookmark_command_through_bound_host_wire()
    {
        const long characterId = 90_000_011;
        const long systemId = 31_000_005;
        const long bookmarkId = 41_000_001;
        const long targetItemId = 50_000_122;
        const long shipId = 80_000_011;
        long? resolvedBookmark = null;
        var simulation = DispatchProxy.Create<ISimulation, MovementSimulation>();
        var recorder = (MovementSimulation)(object)simulation;
        var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]),
            new SolarSystemCatalog([new SolarSystemInfo((int)systemId, "Test", 20_000_001, 10_000_001, 0.5)]), () => FixedNowMs);
        var service = new BeyonceService(space, () => FixedNowMs)
        {
            BookmarkTargetForCharacter = (call, requestedId, _) =>
            {
                Assert.Equal(characterId, call.Session.CharacterId);
                Assert.Equal("CmdAlignTo", call.Method);
                resolvedBookmark = requestedId;
                return Task.FromResult<BookmarkWarpTarget?>(new((int)systemId, targetItemId, null, null, null));
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
                session.Set("locationid", new PyInt(systemId));
                space.EnterPilotForTest(session, characterId, shipId, 587, (int)systemId);
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var alignKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("dstID"), PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyStr("bookmarkID"), new PyLong(bookmarkId)),
        ]);
        var nested = Py.Tuple(new PyStr("CmdAlignTo"), Py.Tuple(), alignKwargs);
        var boundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", boundArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Same(PyNone.Instance, response.Items[1]);
        Assert.Equal(bookmarkId, resolvedBookmark);
        var command = Assert.Single(recorder.Commands);
        Assert.Equal(SimCommands.AlignTo, command.Command);
        Assert.Equal((double)targetItemId, Assert.Single(command.Args));
    }

    [Fact]
    public async Task Beyonce_warp_to_bookmark_without_a_space_pilot_returns_bound_pair_through_host_wire()
    {
        const long characterId = 90_000_012;
        const long systemId = 31_000_006;
        const long bookmarkId = 41_000_002;
        long? resolvedBookmark = null;
        var simulation = DispatchProxy.Create<ISimulation, InertSimulation>();
        var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]), new SolarSystemCatalog([]), () => FixedNowMs);
        var service = new BeyonceService(space, () => FixedNowMs)
        {
            BookmarkTargetForCharacter = (call, requestedId, _) =>
            {
                Assert.Equal(characterId, call.Session.CharacterId);
                Assert.Equal("CmdWarpToStuff", call.Method);
                resolvedBookmark = requestedId;
                return Task.FromResult<BookmarkWarpTarget?>(null);
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var warpKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("minRange"), new PyInt(0)),
        ]);
        var nested = Py.Tuple(
            new PyStr("CmdWarpToStuff"),
            Py.Tuple(new PyStr("bookmark"), new PyLong(bookmarkId)),
            warpKwargs);
        var boundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", boundArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
        Assert.Null(resolvedBookmark);
    }

}
