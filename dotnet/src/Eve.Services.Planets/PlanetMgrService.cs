using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Dogma;
using Eve.Planets;
using Eve.Services;
using Eve.Ships;
using Eve.Skills;
using Eve.Space;
using Eve.Structures;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Planets;

/// <summary>
/// The client's <c>planetMgr</c>: the planetary interaction of a character. Unbound calls list the colonies and launches; a
/// planet's bound object (<c>eveMoniker.GetPlanet</c>) reads and changes one planet. A change (<c>UserUpdateNetwork</c> and the
/// other user calls) runs in one transaction: the colony document, the pin items, the launch records and the ISK it costs.
/// Notifications go out after the commit.
/// </summary>
[Service("planetMgr")]
public sealed partial class PlanetMgrService(NpgsqlDataSource source, PlanetStore store, PlanetCatalog catalog, IResourceField field,
    PgItemStore items, WalletTransactions wallets, PgSkillStore skills, Func<long> nowMs, Func<long, PyValue?, Task> notify, Func<long, Task> notifyLaunches, string inventorySchema,
    ISimulation? simulation = null, PgSpaceItemStore? spaceItems = null, DogmaStatic? dogma = null,
    Func<long, long, CancellationToken, Task<bool>>? canManageCustomsOffice = null)
    : Eve.Services.Inventory.ICustomsOffices
{
    private const long UnboundPlanetKey = -1;
    private readonly Dictionary<string, long> _boundPlanets = [];
    private readonly Dictionary<long, PlanetBody> _bodies = [];
    private readonly object _rawResourceTypeIdsGate = new();
    private Task<int[]>? _rawResourceTypeIds;

    /// <summary><c>MachoBindObject(planetID, (method, args, kwargs))</c>: binds the planet, then makes the nested call.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var planetId = Integral(call.Arg(0)) ?? 0;
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        lock (_boundPlanets) _boundPlanets[oid] = planetId;
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. ArgsOf(nested.Items.Count > 1 ? nested.Items[1] : PyNone.Instance)],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>GetPlanetsForChar: the planets the character has a colony on (planetSvc.py:65).</summary>
    [Method("GetPlanetsForChar")]
    public async ValueTask<PyValue> GetPlanetsForChar(ServiceCall call)
    {
        var owner = call.Session.CharacterId ?? 0;
        var colonies = owner == 0 ? [] : await store.ColoniesOfAsync(owner, call.Cancellation);
        var rows = new List<PyValue>();
        foreach (var colony in colonies)
        {
            var body = await BodyAsync(colony.PlanetId, call.Cancellation);
            rows.Add(new PyPackedRow(PlanetRowDescriptor, [new PyInt(body.SolarSystemId), new PyInt(colony.PlanetId), new PyInt(colony.PlanetTypeId),
                new PyInt(colony.Pins.Count), new PyInt(body.CelestialIndex)]));
        }
        return CRowset(PlanetRowDescriptor, rows);
    }

    /// <summary>GetMyLaunchesDetails: the launches still in orbit (planetUISvc.py:172).</summary>
    [Method("GetMyLaunchesDetails")]
    public async ValueTask<PyValue> GetMyLaunchesDetails(ServiceCall call)
    {
        var owner = call.Session.CharacterId ?? 0;
        if (owner != 0) await store.ExpireLaunchesAsync(owner, Ticks(), call.Cancellation);
        var launches = owner == 0 ? [] : await store.LaunchesOfAsync(owner, Ticks(), call.Cancellation);
        return CRowset(LaunchRowDescriptor, [.. launches.Select(l => (PyValue)new PyPackedRow(LaunchRowDescriptor,
            [new PyInt(l.LaunchId), new PyInt(l.SolarSystemId), new PyLong(l.ItemId), new PyInt(l.OwnerId), new PyInt(l.PlanetId),
             PyValue.Of(l.Status), new PyLong(l.LaunchTime), new PyReal(l.X), new PyReal(l.Y), new PyReal(l.Z)]))]);
    }

    [Method("DeleteLaunch")]
    public async ValueTask<PyValue> DeleteLaunch(ServiceCall call)
    {
        var owner = call.Session.CharacterId ?? 0;
        var launchId = Integral(call.Arg(0)) ?? 0;
        return PyValue.Of(owner != 0 && launchId > 0 && await store.DeleteLaunchAsync(owner, launchId, call.Cancellation));
    }

    /// <summary>GetPlanetInfo: the planet's body, and the colony of the calling character when it has one (clientPlanet.PreparePlanet).</summary>
    [Method("GetPlanetInfo")]
    public async ValueTask<PyValue> GetPlanetInfo(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var body = await BodyAsync(planetId, call.Cancellation);
        var fields = new List<(string, PyValue)>
        {
            ("solarSystemID", new PyInt(body.SolarSystemId)),
            ("planetTypeID", new PyInt(body.TypeId)),
            ("radius", new PyReal(body.Radius)),
            ("celestialIndex", new PyInt(body.CelestialIndex)),
        };
        var owner = call.Session.CharacterId ?? 0;
        var colony = owner == 0 ? null : await SnapshotAsync(owner, planetId, call.Cancellation);
        if (colony is not null) fields.AddRange(colony.Value.Fields);
        return Py.KeyVal([.. fields]);
    }

    [Method("GetFullNetworkForOwner")]
    public async ValueTask<PyValue> GetFullNetworkForOwner(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var owner = Integral(call.Arg(1)) ?? 0;
        var colony = owner == 0 ? null : await SnapshotAsync(owner, planetId, call.Cancellation);
        return colony is null ? PyNone.Instance : Py.KeyVal([.. colony.Value.Fields]);
    }

    /// <summary>GetCommandPinsForPlanet(planetID): the command centers on the planet (planetSvc.py:116).</summary>
    [Method("GetCommandPinsForPlanet")]
    public async ValueTask<PyValue> GetCommandPinsForPlanet(ServiceCall call) => await PinsOfKindAsync(call, PinKind.CommandCenter);

    /// <summary>GetExtractorsForPlanet(planetID): the extraction control units on the planet (planetSvc.py:126).</summary>
    [Method("GetExtractorsForPlanet")]
    public async ValueTask<PyValue> GetExtractorsForPlanet(ServiceCall call) => await PinsOfKindAsync(call, PinKind.Ecu);

    /// <summary>GetProgramResultInfo(pinID, typeID, heads, headRadius): what a program would distribute (clientPlanet.InstallProgram).</summary>
    [Method("GetProgramResultInfo")]
    public async ValueTask<PyValue> GetProgramResultInfo(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var owner = call.Session.CharacterId ?? 0;
        await using var connection = await source.OpenConnectionAsync(call.Cancellation);
        var doc = await store.ReadAsync(owner, planetId, call.Cancellation)
            ?? throw MachoErrors.ThrowUserError("CannotManagePlanetWithoutCommandCenter");
        var colony = doc.ToColony(catalog, field);
        colony.RunTo(Ticks());
        var heads = ReadHeads(call.Arg(2));
        var result = colony.ProgramResult(Integral(call.Arg(0)) ?? 0, (int)(Integral(call.Arg(1)) ?? 0), heads, Number(call.Arg(3)));
        return Py.Tuple(new PyInt(result.MaxValue), new PyLong(result.CycleTime), new PyInt(result.NumCycles));
    }

    /// <summary>
    /// GetResourceData: the planet's resource harmonic is CCP's native data, which the server does not have (PI_DESIGN.md D1), so
    /// the client gets no new band and keeps what it has.
    /// </summary>
    [Method("GetResourceData")]
    public PyValue GetResourceData(ServiceCall call) => Py.KeyVal(("data", PyNone.Instance), ("numBands", new PyInt(0)), ("proximity", PyNone.Instance));

    /// <summary>
    /// GetPlanetResourceInfo: the planet's resources and their quality on the client's display scale (resourceController lists them). The set is the
    /// project's rule (<see cref="PlanetSurvey"/>, D9); the amounts are the field's.
    /// </summary>
    [Method("GetPlanetResourceInfo")]
    public async ValueTask<PyValue> GetPlanetResourceInfo(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        if (dogma is null) return new PyDict([]);
        Task<int[]> rawResourceTypeIds;
        lock (_rawResourceTypeIdsGate)
            rawResourceTypeIds = _rawResourceTypeIds ??= LoadRawResourceTypeIdsAsync();
        var raw = await rawResourceTypeIds.WaitAsync(call.Cancellation);
        var survey = PlanetSurvey.ResourcesOf(planetId, raw)
            .Select(type => new KeyValuePair<PyValue, PyValue>(new PyInt(type), new PyReal(PlanetSurvey.QualityOf(field, planetId, type))));
        return new PyDict([.. survey]);
    }

    private async Task<int[]> LoadRawResourceTypeIdsAsync() =>
        (await dogma!.TypesInGroupsAsync(RawResourceGroups)).Select(type => type.TypeId).ToArray();

    /// <summary>The raw planetary resource groups (SDE 1032, 1033, 1035: solid, liquid-gas, organic).</summary>
    private static readonly string[] RawResourceGroups = ["Planet Solid - Raw Resource", "Planet Liquid-Gas - Raw Resource", "Planet Organic - Raw Resource"];

    /// <summary>UserUpdateNetwork(changes): the batch of the client's command stream, applied in one transaction (clientPlanet.SubmitChanges).</summary>
    [Method("UserUpdateNetwork")]
    public async ValueTask<PyValue> UserUpdateNetwork(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        var changes = Changes(call.Arg(0));
        var snapshot = await ChangeAsync(owner, planetId, async (colony, context) =>
        {
            var commands = new List<ColonyCommand>();
            foreach (var change in changes) commands.Add(await DecodeAsync(change, colony, context, call.Cancellation));
            return commands;
        }, call.Cancellation);
        return Py.KeyVal([.. snapshot.Snapshot]);
    }

    /// <summary>
    /// GMForceInstallProgram(pinID, typeID, cycleTime, lifetimeHours, qtyPerCycle, headRadius): the game master's program on an extractor
    /// control unit of the caller's colony. Served to ROLE_GML only (the client shows the deposit designer to that role alone).
    /// </summary>
    [Method("GMForceInstallProgram")]
    public async ValueTask<PyValue> GMForceInstallProgram(ServiceCall call)
    {
        RequireGameMaster(call);
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        var pinId = Integral(call.Arg(0)) ?? 0;
        var typeId = (int)(Integral(call.Arg(1)) ?? 0);
        var cycleTime = Integral(call.Arg(2)) ?? 0;
        var hours = Math.Min(Integral(call.Arg(3)) ?? 0, long.MaxValue / PlanetRules.Hour);
        var qtyPerCycle = Integral(call.Arg(4)) ?? 0;
        var headRadius = Number(call.Arg(5));
        await ChangeAsync(owner, planetId, (_, _) => Task.FromResult<List<ColonyCommand>>(
            [new GmForceInstallProgramCommand(pinId, typeId, cycleTime, hours * PlanetRules.Hour, qtyPerCycle, headRadius)]), call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>GMAddCommodity(pinID, typeID, quantity): the game master's commodity on a pin of the caller's colony (ROLE_GML only).</summary>
    [Method("GMAddCommodity")]
    public async ValueTask<PyValue> GMAddCommodity(ServiceCall call)
    {
        RequireGameMaster(call);
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        var pinId = Integral(call.Arg(0)) ?? 0;
        var typeId = (int)(Integral(call.Arg(1)) ?? 0);
        var quantity = Integral(call.Arg(2)) ?? 0;
        await ChangeAsync(owner, planetId, (_, _) => Task.FromResult<List<ColonyCommand>>([new GmAddCommodityCommand(pinId, typeId, quantity)]), call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>GMConvertCommandCenter(pinID): the command center takes the type of its planet (ROLE_GML only).</summary>
    [Method("GMConvertCommandCenter")]
    public async ValueTask<PyValue> GMConvertCommandCenter(ServiceCall call)
    {
        RequireGameMaster(call);
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        var pinId = Integral(call.Arg(0)) ?? 0;
        await ChangeAsync(owner, planetId, (_, _) => Task.FromResult<List<ColonyCommand>>([new GmConvertCommandCenterCommand(pinId)]), call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GMGetSynchedServerState")]
    public async ValueTask<PyValue> GMGetSynchedServerState(ServiceCall call)
    {
        RequireGameMaster(call);
        var planetId = PlanetOf(call);
        var owner = Integral(call.Arg(0)) ?? call.Session.CharacterId ?? 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var colony = owner == 0 ? null : await SnapshotAsync(owner, planetId, call.Cancellation);
        var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks;
        var fields = colony?.Fields ?? [("level", new PyInt(0)), ("currentSimTime", new PyLong(0)), ("pins", new PyList([]))];
        return Py.Tuple(new PyLong(duration), Py.KeyVal([.. fields]));
    }

    private static void RequireGameMaster(ServiceCall call)
    {
        if (!GameMasterRoles.Has(call.Session, GameMasterRoles.Gml))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("ROLE_GML is required")));
    }

    /// <summary>UserAbandonPlanet: the colony goes; its pins' items go with it.</summary>
    [Method("UserAbandonPlanet")]
    public async ValueTask<PyValue> UserAbandonPlanet(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        await ChangeAsync(owner, planetId, (colony, _) => Task.FromResult<List<ColonyCommand>>([new RemovePinCommand(CommandCenterOf(colony))]), call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>UserLaunchCommodities(commandPinID, {typeID: qty}): the commodities go into orbit; the launch time comes back.</summary>
    [Method("UserLaunchCommodities")]
    public async ValueTask<PyValue> UserLaunchCommodities(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        var pinId = Integral(call.Arg(0)) ?? 0;
        var commodities = Commodities(call.Arg(1));
        var body = await BodyAsync(planetId, call.Cancellation);
        long launchTime = 0;
        long canId = 0;
        await ChangeAsync(owner, planetId, (colony, _) =>
        {
            launchTime = colony.Launch(pinId, commodities);
            return Task.FromResult<List<ColonyCommand>>([]);
        }, call.Cancellation, extra: async (connection, transaction) =>
        {

            var container = await items.CreateAsync(connection, transaction, new NewItem(LaunchContainerType, owner, body.SolarSystemId, ContainerFlag, 1, Singleton: true), call.Cancellation);
            canId = container;
            foreach (var (type, quantity) in commodities)
                await items.CreateAsync(connection, transaction, new NewItem(type, owner, container, ContainerFlag, quantity), call.Cancellation);
            // near the planet: two radii out along x from its centre (the launch's place in the system)
            await store.InsertLaunchAsync(connection, transaction, new LaunchRecord(0, owner, planetId, body.SolarSystemId, container, true,
                launchTime, launchTime + PlanetRules.LaunchLifetime, body.X + 2 * body.Radius, body.Y, body.Z,
                System.Text.Json.JsonSerializer.Serialize(commodities.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value))),
                call.Cancellation);
        });
        await PlaceLaunchCanAsync(body, canId, owner, call.Cancellation);

        await notifyLaunches(owner);
        return new PyLong(launchTime);
    }

    /// <summary>
    /// Puts a launch's can in space two radii out from its planet, where the launch record says: the simulation's ball of a can, and its space row
    /// </summary>
    private async Task PlaceLaunchCanAsync(PlanetBody body, long canId, long owner, CancellationToken cancellationToken)
    {
        if (simulation is null || spaceItems is null || dogma is null) return;
        await dogma.EnsureTypesAsync([LaunchContainerType], cancellationToken);
        var type = dogma.Type(LaunchContainerType);
        var radius = type?.Attributes.GetValueOrDefault(162, LaunchCanRadius) ?? LaunchCanRadius;
        double[] position = [body.X + 2 * body.Radius, body.Y, body.Z];
        double[] direction = [1, 0, 0];
        var expiresMs = nowMs() + PlanetRules.LaunchLifetime / 10_000;
        await spaceItems.SaveAsync(new SpaceItem(canId, (int)body.SolarSystemId, position, direction, radius, LaunchCanGroup, 0, owner, 0, 0, 0, expiresMs),
            cancellationToken);
        simulation.PlaceWreck(new SimPlaceWreck(SimSceneKey.SolarSystem((int)body.SolarSystemId), canId, LaunchCanGroup,
            new SimWreckKind(LaunchContainerType, type?.Name ?? "Cargo Container", type?.GraphicId ?? 0), position, direction, radius, 0,
            new SimCredit(owner, 0, 0, 0), false));
    }

    /// <summary>UserTransferCommodities(path, commodities): an expedited transfer between two of the colony's pins.</summary>
    [Method("UserTransferCommodities")]
    public async ValueTask<PyValue> UserTransferCommodities(ServiceCall call)
    {
        var planetId = PlanetOf(call);
        var owner = RequireOwner(call);
        var path = Ids(call.Arg(0));
        var commodities = Commodities(call.Arg(1));
        var times = (0L, 0L);
        await ChangeAsync(owner, planetId, (colony, _) =>
        {
            times = colony.ExpeditedTransfer(path, commodities);
            return Task.FromResult<List<ColonyCommand>>([]);
        }, call.Cancellation);
        return Py.Tuple(new PyLong(times.Item1), new PyLong(times.Item2));
    }

    private sealed class ChangeContext(NpgsqlConnection connection, NpgsqlTransaction transaction, long owner, long planetId)
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;
        public long Owner { get; } = owner;
        public long PlanetId { get; } = planetId;
        public Dictionary<(long Kind, long Number), long> PinIds { get; } = [];
        public Dictionary<(long Kind, long Number), long> RouteIds { get; } = [];
    }

    private sealed record ChangeResult(long Owner, long PlanetId, IReadOnlyList<(string, PyValue)> Snapshot);

    public Task<IReadOnlyList<ColonyDocument>> GmColoniesAsync(long owner, CancellationToken cancellationToken) => store.ColoniesOfAsync(owner, cancellationToken);

    public async Task GmChangeAsync(long owner, long planetId, ColonyCommand command, CancellationToken cancellationToken) =>
        await ChangeAsync(owner, planetId, (_, _) => Task.FromResult<List<ColonyCommand>>([command]), cancellationToken);

    /// <summary>
    /// One change of a colony: locks it, runs the colony to now, lets <paramref name="build"/> make the commands (and the direct
    /// </summary>
    private async Task<ChangeResult> ChangeAsync(long owner, long planetId, Func<Colony, ChangeContext, Task<List<ColonyCommand>>> build,
        CancellationToken cancellationToken, Func<NpgsqlConnection, NpgsqlTransaction, Task>? extra = null)
    {
        try
        {
            return await ChangeCoreAsync(owner, planetId, build, cancellationToken, extra);
        }
        catch (PlanetRefusal refusal)
        {
            throw MachoErrors.ThrowUserError(refusal.Key);
        }
    }

    private async Task<ChangeResult> ChangeCoreAsync(long owner, long planetId, Func<Colony, ChangeContext, Task<List<ColonyCommand>>> build,
        CancellationToken cancellationToken, Func<NpgsqlConnection, NpgsqlTransaction, Task>? extra = null)
    {
        var now = Ticks();
        var body = await BodyAsync(planetId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var context = new ChangeContext(connection, transaction, owner, planetId);
        var doc = await store.LockAsync(connection, transaction, owner, planetId, cancellationToken);
        var colony = doc?.ToColony(catalog, field) ?? await NewColonyAsync(owner, planetId, now, cancellationToken);
        colony.RunTo(now);
        var before = colony.Pins.Keys.ToHashSet();
        var commands = await build(colony, context);
        var ctx = new ColonyContext(await OtherColoniesAsync(owner, planetId, cancellationToken),
            await SkillLevelAsync(connection, transaction, owner, TypeInterplanetaryConsolidation, cancellationToken),
            await SkillLevelAsync(connection, transaction, owner, TypeCommandCenterUpgrade, cancellationToken), long.MaxValue);
        var outcome = colony.ApplyCommands(commands, ctx);
        if (extra is not null) await extra(connection, transaction);
        foreach (var removed in before.Where(id => !colony.Pins.ContainsKey(id)))
            if (await items.GetAsync(removed, cancellationToken) is { } item && item.OwnerId == owner && item.LocationId == planetId)
                await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
        if (outcome.IskCost > 0)
        {
            var balance = await wallets.AdjustAsync(connection, transaction, owner, -outcome.IskCost, 0, PlanetaryConstructionRef, "planetary interaction",
                now, $"pi:{owner}:{planetId}:{now}:{Guid.NewGuid():N}", cancellationToken);
            if (balance < 0) throw MachoErrors.ThrowUserError("CannotBuildNotEnoughCash");
        }
        var snapshot = outcome.Abandoned ? [] : Snapshot(colony);
        await store.SaveAsync(connection, transaction, owner, planetId, outcome.Abandoned ? null : ColonyDocument.From(colony), now, cancellationToken);
        // a planet with pins has its customs office (the client's launchpad reaches it through the office's ball in space)
        var office = !outcome.Abandoned && colony.Pins.Count > 0;
        if (office) await EnsureOfficeAsync(connection, transaction, planetId, body.SolarSystemId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (office) PlaceOffice(planetId, body);
        if (before.Count > 0 || colony.Pins.Count > 0) await notify(owner, new PyInt(planetId));
        return new ChangeResult(owner, planetId, snapshot);
    }

    /// <summary>The colony of a planet the character has not used yet: empty, on the planet's type and radius.</summary>
    private async Task<Colony> NewColonyAsync(long owner, long planetId, long now, CancellationToken cancellationToken)
    {
        var body = await BodyAsync(planetId, cancellationToken);
        return new Colony(catalog, field, planetId, body.TypeId, body.Radius, owner) { CurrentSimTime = now };
    }

    private async Task<int> OtherColoniesAsync(long owner, long planetId, CancellationToken cancellationToken) =>
        (await store.ColoniesOfAsync(owner, cancellationToken)).Count(c => c.PlanetId != planetId);

    private async Task<int> SkillLevelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long owner, int typeId, CancellationToken cancellationToken) =>
        await skills.LevelAsync(connection, transaction, owner, typeId, cancellationToken);

    // ----- the client's command stream -----

    /// <summary>One change as the client sends it: the command id and its argument tuple.</summary>
    private static List<(int Id, PyTuple Args)> Changes(PyValue value)
    {
        var list = new List<(int, PyTuple)>();
        var items = value switch { PyList l => l.Items, PyTuple t => t.Items, _ => [] };
        foreach (var entry in items)
            if (entry is PyTuple { Items.Count: 2 } pair && pair.Items[0] is PyInt id && pair.Items[1] is PyTuple args)
                list.Add(((int)id.Value, args));
            else if (entry is PyTuple { Items.Count: >= 2 } fallback && Integral(fallback.Items[0]) is { } fid)
                list.Add(((int)fid, new PyTuple([.. fallback.Items.Skip(1)])));
        return list;
    }

    private static (long Kind, long Number)? TemporaryId(PyValue value) =>
        value is PyTuple { Items.Count: 2 } pair && Integral(pair.Items[0]) is { } kind && Integral(pair.Items[1]) is { } number ? (kind, number) : null;

    private static long Real(ChangeContext c, PyValue value, Dictionary<(long, long), long> map) =>
        TemporaryId(value) is { } temp ? map.TryGetValue(temp, out var real) ? real : throw MachoErrors.ThrowUserError("PinDoesNotExist") : Integral(value) ?? 0;

    /// <summary>Decodes one command of the stream, creating the pins' items and mapping the client's temporary ids.</summary>
    private async Task<ColonyCommand> DecodeAsync((int Id, PyTuple Args) change, Colony colony, ChangeContext c, CancellationToken cancellationToken)
    {
        var a = change.Args.Items;
        PyValue At(int i) => i < a.Count ? a[i] : PyNone.Instance;
        switch (change.Id)
        {
            case 1:
            {
                var typeId = (int)(Integral(At(1)) ?? 0);
                var kind = colony.Catalog.KindOf(typeId) ?? throw MachoErrors.ThrowUserError("InvalidPinType");
                long pinId;
                if (TemporaryId(At(0)) is { } temp)
                {
                    pinId = await items.CreateAsync(c.Connection, c.Transaction, new NewItem(typeId, c.Owner, c.PlanetId, 0), cancellationToken);
                    c.PinIds[temp] = pinId;
                }
                else
                {
                    pinId = Integral(At(0)) ?? 0;
                    if (kind == PinKind.CommandCenter)
                    {
                        var hold = await items.LockAndGetAsync(c.Connection, c.Transaction, pinId, cancellationToken)
                            ?? throw MachoErrors.ThrowUserError("CannotBuildCommandPin");
                        if (hold.OwnerId != c.Owner || hold.TypeId != typeId) throw MachoErrors.ThrowUserError("CannotBuildCommandPin");
                        await items.DeleteAsync(c.Connection, c.Transaction, pinId, cancellationToken);
                    }
                }
                return new CreatePinCommand(pinId, typeId, Number(At(2)), Number(At(3)));
            }
            case 2:
                return new RemovePinCommand(Real(c, At(0), c.PinIds));
            case 3:
                return new CreateLinkCommand(Real(c, At(0), c.PinIds), Real(c, At(1), c.PinIds), colony.Catalog.LinkTypeId, (int)(Integral(At(2)) ?? 0));
            case 4:
                return new RemoveLinkCommand(Real(c, At(0), c.PinIds), Real(c, At(1), c.PinIds));
            case 5:
                return new SetLinkLevelCommand(Real(c, At(0), c.PinIds), Real(c, At(1), c.PinIds), (int)(Integral(At(2)) ?? 0));
            case 6:
            {
                long routeId;
                if (TemporaryId(At(0)) is { } temp)
                {
                    routeId = colony.NewRouteId();
                    c.RouteIds[temp] = routeId;
                }
                else routeId = Integral(At(0)) ?? 0;
                var path = (At(1) switch { PyList l => l.Items, PyTuple t => t.Items, _ => [] }).Select(v => Real(c, v, c.PinIds)).ToList();
                return new CreateRouteCommand(routeId, path, (int)(Integral(At(2)) ?? 0), Integral(At(3)) ?? 0);
            }
            case 7:
                return new RemoveRouteCommand(Real(c, At(0), c.RouteIds));
            case 8:
                return new SetSchematicCommand(Real(c, At(0), c.PinIds), (int)(Integral(At(1)) ?? 0));
            case 9:
                return new UpgradeCommandCenterCommand(Real(c, At(0), c.PinIds), (int)(Integral(At(1)) ?? 0));
            case 10:
                return new AddExtractorHeadCommand(Real(c, At(0), c.PinIds), (int)(Integral(At(1)) ?? 0), Number(At(2)), Number(At(3)));
            case 11:
                return new KillExtractorHeadCommand(Real(c, At(0), c.PinIds), (int)(Integral(At(1)) ?? 0));
            case 12:
                return new MoveExtractorHeadCommand(Real(c, At(0), c.PinIds), (int)(Integral(At(1)) ?? 0), Number(At(2)), Number(At(3)));
            case 13: // InstallProgram(pinID, typeID or None, headRadius)
                return new InstallProgramCommand(Real(c, At(0), c.PinIds), Integral(At(1)) is { } type ? (int)type : null, Number(At(2)));
            default:
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr($"Unknown planetary change {change.Id}")));
        }
    }

    private async ValueTask<PyValue> PinsOfKindAsync(ServiceCall call, PinKind kind)
    {
        var planetId = PlanetOf(call);
        var owner = call.Session.CharacterId ?? 0;
        var colony = owner == 0 ? null : await SnapshotAsync(owner, planetId, call.Cancellation);
        var rows = colony is null ? new List<PyValue>() : PinRows(colony.Value.Colony, kind);
        return new PyList([.. rows]);
    }

    /// <summary>The colony as the client reads it (clientPlanet._PrimeColony), run to now, not saved.</summary>
    private async Task<(Colony Colony, List<(string, PyValue)> Fields)?> SnapshotAsync(long owner, long planetId, CancellationToken cancellationToken)
    {
        var doc = await store.ReadAsync(owner, planetId, cancellationToken);
        if (doc is null) return null;
        var colony = doc.ToColony(catalog, field);
        colony.RunTo(Ticks());
        return (colony, Snapshot(colony));
    }

    private List<(string, PyValue)> Snapshot(Colony colony) =>
    [
        ("level", new PyInt(colony.Level)),
        ("currentSimTime", new PyLong(colony.CurrentSimTime)),
        ("pins", new PyList([.. PinRows(colony, null)])),
        ("links", new PyList([.. colony.Links.Values.OrderBy(l => l.End1).ThenBy(l => l.End2).Select(l => (PyValue)Py.KeyVal(
            ("typeID", new PyInt(l.TypeId)), ("endpoint1", new PyInt(l.End1)), ("endpoint2", new PyInt(l.End2)), ("level", new PyInt(l.Level))))])),
        ("routes", new PyList([.. colony.Routes.Values.OrderBy(r => r.Id).Select(r => (PyValue)Py.KeyVal(
            ("routeID", new PyLong(r.Id)), ("charID", new PyInt(r.OwnerId)), ("path", new PyList([.. r.Path.Select(p => (PyValue)new PyLong(p))])),
            ("commodityTypeID", new PyInt(r.TypeId)), ("commodityQuantity", new PyLong(r.Quantity))))])),
    ];

    private static List<PyValue> PinRows(Colony colony, PinKind? kind) =>
        [.. colony.Pins.Values.OrderBy(p => p.Id).Where(p => kind is null || p.Kind == kind).Select(PinRow)];

    /// <summary>One pin as the client's <c>RestorePinFromRow</c> reads it: the common fields, and the ones of its kind.</summary>
    private static PyValue PinRow(Pin p)
    {
        var fields = new List<(string, PyValue)>
        {
            ("id", new PyLong(p.Id)),
            ("typeID", new PyInt(p.TypeId)),
            ("ownerID", new PyInt(p.OwnerId)),
            ("latitude", new PyReal(p.Latitude)),
            ("longitude", new PyReal(p.Longitude)),
            ("lastRunTime", p.LastRunTime is { } run ? new PyLong(run) : PyNone.Instance),
            ("state", new PyInt(p.State)),
            ("contents", new PyDict([.. p.Contents.Select(kv => new KeyValuePair<PyValue, PyValue>(new PyInt(kv.Key), new PyInt(kv.Value)))])),
        };
        switch (p.Kind)
        {
            case PinKind.CommandCenter:
            case PinKind.Launchpad:
                fields.Add(("lastLaunchTime", p.LastLaunchTime is { } last ? new PyLong(last) : PyNone.Instance));
                break;
            case PinKind.Processor:
                fields.Add(("schematicID", p.SchematicId is { } schematic ? new PyInt(schematic) : PyNone.Instance));
                fields.Add(("hasReceivedInputs", PyValue.Of(p.HasReceivedInputs)));
                fields.Add(("receivedInputsLastCycle", PyValue.Of(p.ReceivedInputsLastCycle)));
                break;
            case PinKind.Ecu:
                fields.Add(("heads", new PyList([.. p.Heads.Select(h => (PyValue)Py.Tuple(new PyInt(h.Id), new PyReal(h.Latitude), new PyReal(h.Longitude)))])));
                fields.Add(("programType", p.ProgramType is { } type ? new PyInt(type) : PyNone.Instance));
                fields.Add(("cycleTime", p.ProgramType is null ? PyNone.Instance : new PyLong(p.CycleTime)));
                fields.Add(("expiryTime", p.ExpiryTime is { } expiry ? new PyLong(expiry) : PyNone.Instance));
                fields.Add(("installTime", p.InstallTime is { } install ? new PyLong(install) : PyNone.Instance));
                fields.Add(("qtyPerCycle", new PyLong(p.QtyPerCycle)));
                fields.Add(("headRadius", new PyReal(p.HeadRadius)));
                break;
        }
        return Py.KeyVal([.. fields]);
    }

    private async Task<PlanetBody> BodyAsync(long planetId, CancellationToken cancellationToken)
    {
        if (_bodies.TryGetValue(planetId, out var known)) return known;
        var body = await PlanetCatalogLoader.PlanetAsync(source, planetId, cancellationToken)
            ?? throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr($"Unknown planet {planetId}")));
        _bodies[planetId] = body;
        return body;
    }

    private long PlanetOf(ServiceCall call)
    {
        if (call.BoundObjectId is { } oid)
            lock (_boundPlanets) if (_boundPlanets.TryGetValue(oid, out var bound)) return bound;
        return Integral(call.Arg(0)) ?? UnboundPlanetKey;
    }

    private static long RequireOwner(ServiceCall call) =>
        call.Session.CharacterId is { } owner ? owner : throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("No character selected")));

    private static long CommandCenterOf(Colony colony) =>
        colony.CommandCenter?.Id ?? throw MachoErrors.ThrowUserError("CannotManagePlanetWithoutCommandCenter");

    private long Ticks() => Py.FileTimeFromUnixMs(nowMs());

    private static long? Integral(PyValue? value) => value switch
    {
        PyInt i => i.Value,
        PyLong l => l.Value,
        PyReal r => (long)r.Value,
        _ => null,
    };

    private static double Number(PyValue? value) => value switch
    {
        PyReal r => r.Value,
        PyInt i => i.Value,
        PyLong l => l.Value,
        _ => 0,
    };

    private static List<long> Ids(PyValue value) =>
        (value switch { PyList l => l.Items, PyTuple t => t.Items, _ => [] }).Select(v => Integral(v) ?? 0).ToList();

    private static Dictionary<int, long> Commodities(PyValue value)
    {
        var result = new Dictionary<int, long>();
        if (value is PyDict dict)
            foreach (var (key, quantity) in dict.Entries)
                result[(int)(Integral(key) ?? 0)] = Integral(quantity) ?? 0;
        return result;
    }

    private static List<Head> ReadHeads(PyValue value) =>
        (value switch { PyList l => l.Items, PyTuple t => t.Items, _ => [] })
        .Select(h => h is PyTuple { Items.Count: >= 3 } head
            ? new Head((int)(Integral(head.Items[0]) ?? 0), Number(head.Items[1]), Number(head.Items[2]))
            : new Head(0, 0, 0)).ToList();

    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyTuple t => t.Items,
        PyList l => l.Items,
        _ => [],
    };

    // ----- the rowset descriptors (CRowset) of the planetMgr lists -----

    private static readonly Descriptor PlanetRowDescriptor = MakeDescriptor(["solarSystemID", "planetID", "typeID", "numberOfPins", "celestialIndex"], [3, 3, 3, 3, 3]);
    private static readonly Descriptor LaunchRowDescriptor = MakeDescriptor(
        ["launchID", "solarSystemID", "itemID", "ownerID", "planetID", "status", "launchTime", "x", "y", "z"], [3, 3, 20, 3, 3, 17, 64, 5, 5, 5]);

    private static PyValue CRowset(Descriptor descriptor, IReadOnlyList<PyValue> rows) => new PyObjectEx(ObjectExKind.Ex2,
        Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")), Py.Dict(("header", descriptor.Header))), rows, []);

    private static Descriptor MakeDescriptor(string[] columns, long[] types) => Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple([.. columns.Select((name, index) =>
            (PyValue)Py.Tuple(new PyStr(name), new PyInt(types[index])))]))), [], []));

    /// <summary>The wallet reference of a planetary construction charge (appConst refPlanetaryConstruction).</summary>
    private const int PlanetaryConstructionRef = 98;
    private const int TypeInterplanetaryConsolidation = 2495;
    /// <summary>The can a launch is put in (Cargo Container, the type of the cans of CharacterShips), and its flag in space (0).</summary>
    private const int LaunchContainerType = 23;
    /// <summary>The group of a can (Cargo Container, SDE group 12) and its radius when the type has no radius attribute (162).</summary>
    private const int LaunchCanGroup = 12;
    private const double LaunchCanRadius = 14;
    private const int ContainerFlag = 0;
    private const int TypeCommandCenterUpgrade = 2505;
}
