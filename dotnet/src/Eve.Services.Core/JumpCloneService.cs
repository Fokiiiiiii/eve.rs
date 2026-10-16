using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>An installed jump clone: where it is (a station, a structure or a ship), its name, and the implant types in it.</summary>
public sealed record JumpCloneRecord(long JumpCloneId, long LocationId, string LocationKind, string Name, IReadOnlyList<int> ImplantTypeIds);

/// <summary>What a character has installed, and the FILETIME of its last clone jump (0 when none).</summary>
public sealed record JumpCloneSnapshot(IReadOnlyList<JumpCloneRecord> Clones, long TimeLastJumpFileTime);

/// <summary>The clone rows and the skill levels the jump clone service reads and writes (the host supplies them from PostgreSQL).</summary>
public interface IJumpCloneStore
{
    ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken);
    ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken);
    ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken);
    ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken);
    ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken);

    /// <summary>
    /// Installs a clone at the location and charges the fee in one transaction. Null when the character cannot pay (nothing is
    /// installed then).
    /// </summary>
    ValueTask<JumpCloneRecord?> InstallAsync(long characterId, long locationId, string locationKind, decimal fee, CancellationToken cancellationToken);

    /// <summary>The character's ISK balance (for the not-enough-money answer).</summary>
    ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken);
}

/// <summary>
/// The jump clone service (<c>jumpCloneSvc</c>, bound per location as <c>Moniker('jumpCloneSvc', (locationID, group))</c>).
/// The clone state the client reads: <c>clones</c> and <c>implants</c> rowsets and the last jump time (eve-logic
/// <c>eve/client/script/ui/services/clonejumpsvc.py</c>, <c>GetCloneState</c>). The clone limit is the sum of the three
/// Infomorph Psychology levels (wiki: Jump clone; EveJS <c>jumpCloneRules.js</c>).
/// </summary>
[Service("jumpCloneSvc")]
public sealed partial class JumpCloneService(IJumpCloneStore? store = null)
{
    /// <summary>The skills that set the clone limit: Infomorph Psychology, Advanced, Elite (one clone per level each).</summary>
    private static readonly int[] CloneLimitSkills = [24242, 33407, 73910];
    private const int CloneNameMaxLength = 100;

    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall _) => new PyInt(MachoMessage.ProxyNodeId);

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(
            new PyStr(oid),
            new PyLong(Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 ? nested.Items[1] switch { PyTuple tuple => tuple.Items, PyList list => list.Items, _ => [] } : [],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                BoundObjectParameters = call.Arg(0),
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    [Method("GetCloneState")]
    public async ValueTask<PyValue> GetCloneState(ServiceCall call)
    {
        var snapshot = await Snapshot(call);
        return Py.KeyVal(
            ("clones", CloneRows(snapshot.Clones)),
            ("implants", ImplantRows(snapshot.Clones)),
            ("timeLastJump", new PyLong(snapshot.TimeLastJumpFileTime)));
    }

    /// <summary>The clones installed at the station or structure the character is docked in.</summary>
    [Method("GetStationCloneState")]
    public async ValueTask<PyValue> GetStationCloneState(ServiceCall call)
    {
        var snapshot = await Snapshot(call);
        var location = DockedLocation(call.Session);
        return CloneRows(snapshot.Clones.Where(clone => clone.LocationId == location).ToArray());
    }

    /// <summary>The clones installed in the active ship.</summary>
    [Method("GetShipCloneState")]
    public async ValueTask<PyValue> GetShipCloneState(ServiceCall call)
    {
        var snapshot = await Snapshot(call);
        var shipId = (long?)call.Session.GetInt("shipid");
        var rows = snapshot.Clones
            .Where(clone => clone.LocationKind == "ship" && (shipId is null || clone.LocationId == shipId))
            .Select(clone => (PyValue)new PyList([new PyLong(clone.JumpCloneId), new PyLong(call.Session.CharacterId ?? 0), new PyLong(clone.LocationId)]))
            .ToArray();
        return Py.Rowset(["jumpCloneID", "ownerID", "locationID"], rows);
    }

    /// <summary>The clones in the structure (or the ship) the character is in; none outside one.</summary>
    [Method("GetNumClonesInPilotsStructure")]
    public async ValueTask<PyValue> GetNumClonesInPilotsStructure(ServiceCall call)
    {
        var snapshot = await Snapshot(call);
        var location = (long?)call.Session.GetInt("structureid") ?? (long?)call.Session.GetInt("shipid");
        if (location is not > 0)
            return new PyInt(0);
        return new PyInt(snapshot.Clones.Count(clone => clone.LocationId == location));
    }

    /// <summary>
    /// <c>ValidateInstallJumpClone()</c>: the reasons a clone cannot be installed here, as UI labels, or a label with its
    /// arguments. An empty list means the install may go ahead.
    /// </summary>
    [Method("ValidateInstallJumpClone")]
    public async ValueTask<PyValue> ValidateInstallJumpClone(ServiceCall call)
    {
        var errors = new List<PyValue>();
        var location = DockedLocation(call.Session);
        if (call.Session.CharacterId is not { } characterId || location == 0)
        {
            errors.Add(new PyStr("UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/JumpNotDockedError"));
            return new PyList([.. errors]);
        }

        var snapshot = await Snapshot(call);
        var limit = await CloneLimit(characterId, call.Cancellation);
        if (limit <= 0)
            errors.Add(new PyStr("UI/Medical/JumpCloneSkillReqNotMet"));
        if (snapshot.Clones.Count >= limit)
        {
            errors.Add(Py.Tuple(new PyStr("UI/Medical/JumpCloneUsageAndCapacity"),
                Py.Dict(("count", new PyInt(snapshot.Clones.Count)), ("limit", new PyInt(limit)))));
        }
        // shortcut: the structure clone-service rights check (EveJS validateInstallJumpClone) comes with the install package CH-15.
        var here = snapshot.Clones.Count(clone => clone.LocationId == location);
        if (here > 0)
        {
            errors.Add(Py.Tuple(new PyStr("UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/InstalledCloneCount"),
                Py.Dict(("clone_count", new PyInt(here)))));
        }
        return new PyList([.. errors]);
    }

    [Method("SetJumpCloneName")]
    public async ValueTask<PyValue> SetJumpCloneName(ServiceCall call)
    {
        var (characterId, clone) = (call.Session.CharacterId ?? 0, Number(call.Arg(0)) ?? 0);
        var name = (JsSemantics.StringValue(call.Arg(1)) ?? string.Empty).Trim();
        if (name.Length > CloneNameMaxLength)
            name = name[..CloneNameMaxLength];
        if (characterId == 0 || clone == 0 || !await Store().RenameAsync(characterId, clone, name, call.Cancellation))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Jump clone not found")));
        Invalidate(call.Session, await LocationOf(characterId, clone, call), characterId);
        return PyNone.Instance;
    }

    [Method("DestroyInstalledClone")]
    public async ValueTask<PyValue> DestroyInstalledClone(ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var clone = Number(call.Arg(0)) ?? 0;
        if (characterId == 0 || clone == 0 || await Store().DestroyAsync(characterId, clone, call.Cancellation) is not { } removed)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Jump clone not found")));
        Invalidate(call.Session, removed.LocationId, characterId);
        return PyNone.Instance;
    }

    [Method("ResetLastCloneJumpTime")]
    public async ValueTask<PyValue> ResetLastCloneJumpTime(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId)
            return PyNone.Instance;
        await Store().ResetLastJumpAsync(characterId, call.Cancellation);
        Invalidate(call.Session, DockedLocation(call.Session), characterId);
        return PyNone.Instance;
    }

    /// <summary>The install fee of a clone station (EveJS <c>JUMP_CLONE_INSTALL_COST</c>, the wiki's 900,000 ISK).</summary>
    private const decimal JumpCloneInstallFee = 900000m;

    /// <summary>
    /// <c>InstallCloneInStation()</c>: installs a clone of the character at the station it is docked in and charges the install fee
    /// (EveJS <c>installCloneAtCurrentLocation</c>). A validation failure is refused with its label; too little ISK is the client's
    /// NotEnoughMoney.
    /// </summary>
    [Method("InstallCloneInStation")]
    public async ValueTask<PyValue> InstallCloneInStation(ServiceCall call) => await Install(call, station: true);

    /// <summary>
    /// <c>InstallCloneInStructure()</c>. shortcut: the structure's clone-service fee is set by its owner and needs the rights check
    /// (EveJS <c>getCloneServiceCostForLocation</c>); until that exists a structure install is refused.
    /// </summary>
    [Method("InstallCloneInStructure")]
    public async ValueTask<PyValue> InstallCloneInStructure(ServiceCall call) => await Install(call, station: false);

    /// <summary>The fee of the install at the place the character is docked (the station fee; 0 where the fee is not modelled yet).</summary>
    [Method("GetPriceForClone")]
    public PyValue GetPriceForClone(ServiceCall call) =>
        (long?)call.Session.GetInt("stationid") is > 0 ? new PyReal((double)JumpCloneInstallFee) : new PyInt(0);

    private async ValueTask<PyValue> Install(ServiceCall call, bool station)
    {
        var location = DockedLocation(call.Session);
        if (call.Session.CharacterId is not { } characterId || location == 0)
            throw MachoErrors.ThrowUserError("NotAtStation");
        if (!station)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Jump clone installation in a structure is not available on this server.")));
        if (await ValidateInstallJumpClone(call) is PyList { Items.Count: > 0 } problems)
        {
            var first = problems.Items[0];
            var label = first is PyStr text ? text.Value : (first as PyTuple)?.Items[0] is PyStr tupleLabel ? tupleLabel.Value : "UI/Medical/Errors/UnknownValidationError";
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(label)));
        }
        var installed = await Store().InstallAsync(characterId, location, "station", JumpCloneInstallFee, call.Cancellation);
        if (installed is null)
        {
            var balance = await Store().IskBalanceAsync(characterId, call.Cancellation);
            throw MachoErrors.ThrowUserError("NotEnoughMoney", ("amount", new PyReal((double)JumpCloneInstallFee)), ("balance", new PyReal((double)balance)));
        }
        Invalidate(call.Session, location, characterId);
        return PyNone.Instance;
    }

    private IJumpCloneStore Store() => store ?? throw new InvalidOperationException("the jump clones are not wired on this server");

    private async ValueTask<JumpCloneSnapshot> Snapshot(ServiceCall call) =>
        call.Session.CharacterId is { } characterId
            ? await Store().ReadAsync(characterId, call.Cancellation)
            : new JumpCloneSnapshot([], 0);

    private async ValueTask<int> CloneLimit(long characterId, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var skill in CloneLimitSkills)
            total += await Store().SkillLevelAsync(characterId, skill, cancellationToken);
        return total;
    }

    private async ValueTask<long> LocationOf(long characterId, long cloneId, ServiceCall call) =>
        (await Store().ReadAsync(characterId, call.Cancellation)).Clones.FirstOrDefault(clone => clone.JumpCloneId == cloneId)?.LocationId
        ?? DockedLocation(call.Session);

    /// <summary>The station or structure the session is docked in (the ship is not a dock); 0 in space.</summary>
    private static long DockedLocation(ClientSession session)
    {
        var station = (long?)session.GetInt("stationid");
        if (station is > 0)
            return station.Value;
        var structure = (long?)session.GetInt("structureid");
        return structure is > 0 && structure != (long?)session.GetInt("shipid") ? structure.Value : 0;
    }

    private static PyValue CloneRows(IReadOnlyList<JumpCloneRecord> clones) => Py.Rowset(
        ["jumpCloneID", "locationID", "cloneName"],
        clones.Select(clone => (PyValue)new PyList([new PyLong(clone.JumpCloneId), new PyLong(clone.LocationId), new PyWString(clone.Name)])).ToArray());

    private static PyValue ImplantRows(IReadOnlyList<JumpCloneRecord> clones) => Py.Rowset(
        ["jumpCloneID", "typeID"],
        clones.SelectMany(clone => clone.ImplantTypeIds.Select(typeId =>
            (PyValue)new PyList([new PyLong(clone.JumpCloneId), new PyInt(typeId)]))).ToArray());

    /// <summary>The clone-change notifications the client's clone caches listen for (EveJS <c>emitCloneInvalidations</c>).</summary>
    private static void Invalidate(ClientSession session, long locationId, long ownerId)
    {
        session.SendNotification("OnJumpCloneCacheInvalidated", "clientID");
        if (locationId == 0)
            return;
        session.SendNotification("OnStationJumpCloneCacheInvalidated", "clientID", new PyLong(locationId), new PyLong(ownerId));
        session.SendNotification("OnShipJumpCloneCacheInvalidated", "clientID", new PyLong(locationId), new PyLong(ownerId));
    }

    private static long? Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => (long)r.Value, _ => null };
}
