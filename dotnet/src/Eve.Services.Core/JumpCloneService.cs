using System.Collections.Concurrent;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>An installed jump clone: where it is (a station, a structure or a ship), its name, and the implant types in it.</summary>
public sealed record JumpCloneRecord(long JumpCloneId, long LocationId, string LocationKind, string Name, IReadOnlyList<int> ImplantTypeIds);

/// <summary>What a character has installed, and the FILETIME of its last clone jump (0 when none).</summary>
public sealed record JumpCloneSnapshot(IReadOnlyList<JumpCloneRecord> Clones, long TimeLastJumpFileTime);

/// <summary>
/// The settlement of one install (EveJS <c>jumpCloneInstallSettlements</c>): the fee the character pays, and who is paid it: the owner
/// corporation of a structure (its wallet is credited), or the offering pilot of a ship (the journal's counterparty; no wallet is credited).
/// </summary>
public sealed record JumpCloneInstall(long LocationId, string LocationKind, decimal Fee, long OwnerCorporationId = 0, long OfferingCharacterId = 0);

/// <summary>A structure's clone bay as one character may use it: the owner's fee and the owner corporation.</summary>
public sealed record StructureCloneBay(long StructureId, long OwnerCorporationId, decimal Fee);

/// <summary>The active ship of a pilot and its clone bay: the hull's clone capacity and operational range, and whether a Clone Vat Bay is fitted.</summary>
public sealed record ShipCloneBay(long ShipId, bool CanReceiveCloneJumps, bool HasCloneVatBay, int Capacity, double MaxOperationalDistance);

/// <summary>
/// The outcome of a clone jump's state change. A refusal names the reason (<c>MUST_LEAVE_SHIP</c>, <c>CLONE_NOT_FOUND</c>,
/// <c>INSUFFICIENT_FUNDS</c>); otherwise the station the character is in now, with its solar system, constellation and region.
/// </summary>
public sealed record CloneJumpResult(string? Refusal, long StationId = 0, int SolarSystemId = 0, int ConstellationId = 0, int RegionId = 0);

/// <summary>The clone jump rules that are not state: the cooldown (wiki: Jump clones; EveJS <c>jumpCloneRules.js</c>).</summary>
public static class CloneJumpRules
{
    /// <summary>The Infomorph Synchronizing skill: each level takes an hour off the cooldown.</summary>
    public const int SynchronizingSkillId = 33399;

    /// <summary>A jump starts a cooldown of 24 hours, less the Infomorph Synchronizing level.</summary>
    public static int CooldownHours(int synchronizingLevel) => Math.Max(0, 24 - synchronizingLevel);

    /// <summary>True while the cooldown from the last jump (a FILETIME, 0 when none) runs at <paramref name="nowFileTime"/>.</summary>
    public static bool CooldownActive(long lastJumpFileTime, int cooldownHours, long nowFileTime) =>
        lastJumpFileTime > 0 && cooldownHours > 0 && lastJumpFileTime + cooldownHours * TimeSpan.TicksPerHour > nowFileTime;
}

/// <summary>The rows and the skill levels the jump clone service reads and writes (the host supplies them from PostgreSQL).</summary>
public interface IJumpCloneStore
{
    ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken);
    ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken);
    ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken);
    ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken);
    ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken);

    /// <summary>
    /// Installs a clone and records its settlement, with the fee moved, in one transaction. Null when the character cannot pay (nothing is
    /// installed then).
    /// </summary>
    ValueTask<JumpCloneRecord?> InstallAsync(long characterId, JumpCloneInstall install, CancellationToken cancellationToken);

    /// <summary>The character's ISK balance (for the not-enough-money answer).</summary>
    ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken);
    /// <summary>
    /// The clone jump, in one transaction: the target clone becomes the body, the body that left keeps its implants in a clone at
    /// the origin (replacing the clone there), the character is in the target's station, the cooldown starts at
    /// <paramref name="nowFileTime"/> and the fee is debited. Nothing changes on a refusal.
    /// </summary>
    ValueTask<CloneJumpResult> JumpAsync(long characterId, long targetCloneId, long originLocationId, decimal fee, long nowFileTime, CancellationToken cancellationToken);
}

/// <summary>The space side of the clone installs, supplied by the Host: the pilot's active ship, the distance of two ships, the online sessions and the structures' clone bays.</summary>
public interface IJumpCloneSpace
{
    /// <summary>The pilot's active ship and its clone bay; null when the pilot has no active ship.</summary>
    ValueTask<ShipCloneBay?> ActiveShipAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>The centre-to-centre distance in metres of two ships that are both in space in one solar system; null otherwise.</summary>
    ValueTask<double?> DistanceAsync(ClientSession first, long firstShipId, ClientSession second, long secondShipId, CancellationToken cancellationToken);

    /// <summary>The session of an online character; null when none.</summary>
    ClientSession? SessionOf(long characterId);

    /// <summary>The clone bay of a structure for the character (a medical or jump clone service it has, and the docking right); null when none.</summary>
    ValueTask<StructureCloneBay?> StructureBayAsync(long characterId, long structureId, CancellationToken cancellationToken);
}

/// <summary>The late link to the space side: the Host sets <see cref="Space"/> once the space sessions and the structures exist.</summary>
public sealed class JumpCloneSpaceLink
{
    public IJumpCloneSpace? Space { get; set; }
}

/// <summary>
/// The jump clone service (<c>jumpCloneSvc</c>, bound per location as <c>Moniker('jumpCloneSvc', (locationID, group))</c>).
/// The clone state the client reads: <c>clones</c> and <c>implants</c> rowsets and the last jump time (eve-logic
/// <c>eve/client/script/ui/services/clonejumpsvc.py</c>, <c>GetCloneState</c>). The clone limit is the sum of the three
/// Infomorph Psychology levels (wiki: Jump clone; EveJS <c>jumpCloneRules.js</c>).
/// </summary>
[Service("jumpCloneSvc")]
public sealed partial class JumpCloneService(IJumpCloneStore? store = null, JumpCloneSpaceLink? spaceLink = null)
{
    /// <summary>The skills that set the clone limit: Infomorph Psychology, Advanced, Elite (one clone per level each).</summary>
    private static readonly int[] CloneLimitSkills = [24242, 33407, 73910];
    private const int CloneNameMaxLength = 100;

    /// <summary>The install fee of a clone station or of a structure without its own fee (EveJS <c>JUMP_CLONE_INSTALL_COST</c>, the wiki's 900,000 ISK).</summary>
    private const decimal JumpCloneInstallFee = 900000m;

    /// <summary>
    /// The ship clone offers, by the pilot they were made to (EveJS <c>pendingShipCloneOffers</c>). They live in memory, as the reference keeps them:
    /// an offer does not survive a Host restart, and an installed clone does.
    /// </summary>
    private readonly ConcurrentDictionary<long, ShipCloneOffer> _offers = new();

    private sealed record ShipCloneOffer(long OfferingCharacterId, long TargetCharacterId, long ShipId);

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
        var here = snapshot.Clones.Count(clone => clone.LocationId == location);
        if (here > 0)
        {
            errors.Add(Py.Tuple(new PyStr("UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/InstalledCloneCount"),
                Py.Dict(("clone_count", new PyInt(here)))));
        }
        // a structure installs only where the character may use its clone bay (EveJS validateInstallJumpClone)
        if (!IsStationDock(call.Session) && await StructureBay(characterId, location, call.Cancellation) is null)
            errors.Add(new PyStr("UI/Medical/Errors/UnknownValidationError"));
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

    /// <summary>
    /// <c>InstallCloneInStation()</c> and <c>InstallCloneInStructure()</c>: installs a clone of the character at the place it is docked in
    /// (EveJS <c>installCloneAtCurrentLocation</c>) and charges the fee. A validation failure is refused with its label; too little ISK is
    /// the client's NotEnoughMoney.
    /// </summary>
    [Method("InstallCloneInStation")]
    public async ValueTask<PyValue> InstallCloneInStation(ServiceCall call) => await Install(call);

    [Method("InstallCloneInStructure")]
    public async ValueTask<PyValue> InstallCloneInStructure(ServiceCall call) => await Install(call);

    /// <summary>
    /// <c>GetPriceForClone()</c>: the fee of an install here: the structure's clone bay fee where the character may use it, else the install fee
    /// (EveJS <c>getCloneServiceCostForLocation</c>, which answers the install fee outside a structure).
    /// </summary>
    [Method("GetPriceForClone")]
    public async ValueTask<PyValue> GetPriceForClone(ServiceCall call)
    {
        var fee = JumpCloneInstallFee;
        var structure = DockedLocation(call.Session);
        if (call.Session.CharacterId is { } characterId && structure > 0 && !IsStationDock(call.Session)
            && await StructureBay(characterId, structure, call.Cancellation) is { } bay)
        {
            fee = bay.Fee;
        }
        return new PyReal((double)fee);
    }

    /// <summary>
    /// <c>OfferShipCloneInstallation(charID)</c>: the pilot of a ship with a Clone Vat Bay offers the target pilot a clone installed in the ship
    /// (EveJS <c>offerShipCloneInstallation</c>). The target hears <c>OnShipJumpCloneInstallationOffered</c>; the offer waits in memory.
    /// </summary>
    [Method("OfferShipCloneInstallation")]
    public async ValueTask<PyValue> OfferShipCloneInstallation(ServiceCall call)
    {
        var offering = call.Session.CharacterId ?? 0;
        var target = Number(call.Arg(0)) ?? 0;
        var space = Space();
        var ship = await space.ActiveShipAsync(call.Session, call.Cancellation);
        if (offering == 0 || target == 0 || ship is null)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Clone install target unavailable")));
        if (!ship.CanReceiveCloneJumps || !ship.HasCloneVatBay)
            throw MachoErrors.ThrowUserError("InviteClone1");
        if (space.SessionOf(target) is not { } targetSession)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Clone install target unavailable")));
        var targetShip = await space.ActiveShipAsync(targetSession, call.Cancellation);
        if (!await InCloneRange(space, call.Session, ship, targetSession, targetShip, call.Cancellation))
            throw MachoErrors.ThrowUserError("InviteClone2");

        _offers[target] = new ShipCloneOffer(offering, target, ship.ShipId);
        targetSession.SendNotification("OnShipJumpCloneInstallationOffered", "clientID", OfferPayload(offering, target, ship.ShipId));
        return PyNone.Instance;
    }

    /// <summary>
    /// <c>AcceptShipCloneInstallation()</c>: the target pilot accepts the offer. The clone is installed in the offering ship, the fee is paid,
    /// and the pilot and the offering pilot hear <c>OnShipJumpCloneInstallationDone</c> (EveJS <c>acceptShipCloneInstallation</c>).
    /// </summary>
    [Method("AcceptShipCloneInstallation")]
    public async ValueTask<PyValue> AcceptShipCloneInstallation(ServiceCall call)
    {
        var target = call.Session.CharacterId ?? 0;
        if (!_offers.TryGetValue(target, out var offer))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Clone install offer expired")));
        var space = Space();
        var offeringSession = space.SessionOf(offer.OfferingCharacterId);
        var offeringShip = offeringSession is null ? null : await space.ActiveShipAsync(offeringSession, call.Cancellation);
        var targetShip = await space.ActiveShipAsync(call.Session, call.Cancellation);
        if (offeringSession is null || offeringShip is null || offeringShip.ShipId != offer.ShipId
            || !await InCloneRange(space, offeringSession, offeringShip, call.Session, targetShip, call.Cancellation))
        {
            _offers.TryRemove(target, out _);
            throw MachoErrors.ThrowUserError("InviteClone2");
        }

        var snapshot = await Snapshot(call);
        if (snapshot.Clones.Count(clone => clone.LocationKind == "ship" && clone.LocationId == offer.ShipId) >= offeringShip.Capacity)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Clone bay is full")));
        if (snapshot.Clones.Count >= await CloneLimit(target, call.Cancellation))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("UI/Medical/JumpCloneSkillReqNotMet")));

        var install = new JumpCloneInstall(offer.ShipId, "ship", JumpCloneInstallFee, 0, offer.OfferingCharacterId);
        if (await Store().InstallAsync(target, install, call.Cancellation) is null)
            throw await NotEnoughMoney(target, call.Cancellation);
        _offers.TryRemove(target, out _);

        // the reference's order: the pilot, then the offering pilot, then the clone caches
        var payload = OfferPayload(offer.OfferingCharacterId, target, offer.ShipId);
        call.Session.SendNotification("OnShipJumpCloneInstallationDone", "clientID", payload);
        offeringSession.SendNotification("OnShipJumpCloneInstallationDone", "clientID", payload);
        Invalidate(call.Session, offer.ShipId, target);
        return PyNone.Instance;
    }

    /// <summary>
    /// <c>CancelShipCloneInstallation()</c>: either pilot cancels the offer that involves it; both hear
    /// <c>OnShipJumpCloneInstallationCanceled</c> (EveJS <c>cancelShipCloneInstallation</c>). Nothing to cancel is not an error.
    /// </summary>
    [Method("CancelShipCloneInstallation")]
    public PyValue CancelShipCloneInstallation(ServiceCall call)
    {
        var caller = call.Session.CharacterId ?? 0;
        var offer = _offers.Values.FirstOrDefault(candidate => candidate.TargetCharacterId == caller)
            ?? _offers.Values.FirstOrDefault(candidate => candidate.OfferingCharacterId == caller);
        if (offer is null)
            return PyNone.Instance;
        _offers.TryRemove(offer.TargetCharacterId, out _);
        var payload = OfferPayload(offer.OfferingCharacterId, offer.TargetCharacterId, offer.ShipId);
        call.Session.SendNotification("OnShipJumpCloneInstallationCanceled", "clientID", payload);
        spaceLink?.Space?.SessionOf(offer.TargetCharacterId)?.SendNotification("OnShipJumpCloneInstallationCanceled", "clientID", payload);
        return PyNone.Instance;
    }

    /// <summary>The activation fee of a clone jump (EveJS <c>REF_JUMP_CLONE_ACTIVATION_FEE</c>, the same 900,000 ISK).</summary>
    private const decimal JumpCloneActivationFee = 900000m;

    /// <summary>
    /// <c>CloneJump(destLocationID, cloneID, cost, confirmed)</c>: the character leaves for the place of an installed clone (EveJS
    /// <c>performCloneJump</c>, <c>buildCloneJumpState</c>). The cooldown is checked when the clone is not where the character is, a
    /// clone at the current place is replaced unless the client confirmed it, and the activation fee is charged once.
    /// </summary>
    [Method("CloneJump")]
    public async ValueTask<PyValue> CloneJump(ServiceCall call)
    {
        var current = DockedLocation(call.Session);
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 || current == 0)
            throw MachoErrors.ThrowUserError("NotAtStation");
        var destination = Number(call.Arg(0)) ?? 0;
        var cloneId = Number(call.Arg(1)) ?? 0;
        var confirmed = call.Arg(3) is PyBool { Value: true };
        var snapshot = await Snapshot(call);
        var target = snapshot.Clones.FirstOrDefault(clone => clone.JumpCloneId == cloneId && (destination == 0 || clone.LocationId == destination))
            ?? throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Jump clone not found")));
        var now = Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (target.LocationId != current)
        {
            var level = await Store().SkillLevelAsync(characterId, CloneJumpRules.SynchronizingSkillId, call.Cancellation);
            if (CloneJumpRules.CooldownActive(snapshot.TimeLastJumpFileTime, CloneJumpRules.CooldownHours(level), now))
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/JumpCooldownError")));
        }
        if (!confirmed && snapshot.Clones.Any(clone => clone.LocationId == current && clone.JumpCloneId != target.JumpCloneId))
            throw MachoErrors.ThrowUserError("JumpCheckWillLoseExistingClone", ("locationID", new PyLong(current)));

        var result = await Store().JumpAsync(characterId, target.JumpCloneId, current, JumpCloneActivationFee, now, call.Cancellation);
        switch (result.Refusal)
        {
            case null:
                break;
            case "MUST_LEAVE_SHIP":
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You must leave your ship before jumping clones")));
            case "INSUFFICIENT_FUNDS":
                var balance = await Store().IskBalanceAsync(characterId, call.Cancellation);
                throw MachoErrors.ThrowUserError("NotEnoughMoney", ("amount", new PyReal((double)JumpCloneActivationFee)), ("balance", new PyReal((double)balance)));
            default:
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Jump clone not found")));
        }
        CharacterSessionRelocation.Docked(call.Session, result.StationId, result.SolarSystemId, result.ConstellationId, result.RegionId, call.Session.GetInt("shipid"));
        Invalidate(call.Session, current, characterId);
        if (target.LocationId != current)
            Invalidate(call.Session, target.LocationId, characterId);
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> Install(ServiceCall call)
    {
        var location = DockedLocation(call.Session);
        if (call.Session.CharacterId is not { } characterId || location == 0)
            throw MachoErrors.ThrowUserError("NotAtStation");
        if (await ValidateInstallJumpClone(call) is PyList { Items.Count: > 0 } problems)
        {
            var first = problems.Items[0];
            var label = first is PyStr text ? text.Value : (first as PyTuple)?.Items[0] is PyStr tupleLabel ? tupleLabel.Value : "UI/Medical/Errors/UnknownValidationError";
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(label)));
        }

        JumpCloneInstall install;
        if (IsStationDock(call.Session))
        {
            install = new JumpCloneInstall(location, "station", JumpCloneInstallFee);
        }
        else
        {
            var bay = await StructureBay(characterId, location, call.Cancellation)
                ?? throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("UI/Medical/Errors/UnknownValidationError")));
            install = new JumpCloneInstall(location, "structure", bay.Fee, bay.OwnerCorporationId);
        }
        if (await Store().InstallAsync(characterId, install, call.Cancellation) is null)
            throw await NotEnoughMoney(characterId, call.Cancellation);
        Invalidate(call.Session, location, characterId);
        return PyNone.Instance;
    }

    /// <summary>The not-enough-money refusal with the fee and the character's balance (the client's NotEnoughMoney).</summary>
    private async ValueTask<MachoWrappedException> NotEnoughMoney(long characterId, CancellationToken cancellationToken)
    {
        var balance = await Store().IskBalanceAsync(characterId, cancellationToken);
        return MachoErrors.ThrowUserError("NotEnoughMoney", ("amount", new PyReal((double)JumpCloneInstallFee)), ("balance", new PyReal((double)balance)));
    }

    private IJumpCloneStore Store() => store ?? throw new InvalidOperationException("the jump clones are not wired on this server");

    private IJumpCloneSpace Space() => spaceLink?.Space ?? throw new InvalidOperationException("the ship and structure clones are not wired on this server");

    private async ValueTask<StructureCloneBay?> StructureBay(long characterId, long structureId, CancellationToken cancellationToken) =>
        spaceLink?.Space is { } space ? await space.StructureBayAsync(characterId, structureId, cancellationToken) : null;

    /// <summary>EveJS <c>validateCloneVatTargetRange</c>: the offering ship has an operational range and the target is within it.</summary>
    private static async ValueTask<bool> InCloneRange(IJumpCloneSpace space, ClientSession offering, ShipCloneBay ship, ClientSession target, ShipCloneBay? targetShip, CancellationToken cancellationToken)
    {
        if (ship.MaxOperationalDistance <= 0 || targetShip is null)
            return false;
        return await space.DistanceAsync(offering, ship.ShipId, target, targetShip.ShipId, cancellationToken) is { } distance
            && distance <= ship.MaxOperationalDistance;
    }

    /// <summary>The ship clone notifications carry one list: the offering pilot, the target pilot, the ship, and 0 (EveJS <c>payload</c>).</summary>
    private static PyList OfferPayload(long offeringCharacterId, long targetCharacterId, long shipId) =>
        new([new PyLong(offeringCharacterId), new PyLong(targetCharacterId), new PyLong(shipId), new PyInt(0)]);

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

    private static bool IsStationDock(ClientSession session) => (long?)session.GetInt("stationid") is > 0;

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
