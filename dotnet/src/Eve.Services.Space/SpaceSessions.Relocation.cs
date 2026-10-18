using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>How far off a pilot's ship a game master's relocation puts the game master, metres: the place is along x, facing the ship (a rule of this host).</summary>
    public const double RelocationOffsetMeters = 2_500d;

    /// <summary>
    /// A game master's relocation of the session's pilot into a solar system (EVE.js <c>jumpSessionToSolarSystem</c>, behind <c>/solar</c> and <c>/tele</c>). The
    /// active ship goes into space there, from its station hangar or from the scene it was in: the scene it left lets it go, the simulation takes it in the
    /// new one, and the pilot is told the session change and the item change as a jump or an undock tells them. With a <paramref name="position"/> the ship is
    /// put there facing <paramref name="direction"/>; without one it is put at the system's anchor. No gate, range, fuel or timer rule applies, and the session
    /// change timer starts as after a jump. Returns null when the pilot is there, else the words to tell the game master.
    /// </summary>
    public async Task<string?> RelocateAsync(
        ClientSession session,
        int solarSystemId,
        IReadOnlyList<double>? position,
        IReadOnlyList<double>? direction,
        CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId)
        {
            return "Select a character before relocating.";
        }
        if (!_sim.IsConnected)
        {
            return "Space is not available right now. Try again in a moment.";
        }
        if (_systems.Get(solarSystemId) is not { } place)
        {
            return $"Solar system not found: {solarSystemId}.";
        }
        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return "Active ship not found for this character.";
        }

        var sessionId = (ulong)session.SessionId;
        var previousStationId = session.GetInt("stationid") ?? 0;
        var previousStructureId = session.GetInt("structureid") ?? 0;
        var previousSystemId = snapshot.Character.SolarSystemId;
        var wasInSpace = _pilots.TryGetValue(sessionId, out var pilot);

        var relocation = await _ships.RelocateAsync(characterId, solarSystemId, place.ConstellationId, place.RegionId, cancellationToken);
        if (relocation.Moved is not { } moved)
        {
            return relocation.Refusal == "NO_SHIP" ? "Active ship not found for this character." : "Solar-system jump failed.";
        }

        await SyncBuffsAsync(snapshot.World, snapshot.Ship.Id, cancellationToken);
        var spec = SimShips.Of(snapshot, _statics, await SecurityStatusAsync(characterId, cancellationToken));
        if (wasInSpace)
        {
            // the scene the ship leaves: its rounds are written and its repairs end, as on a dock; the simulation lets the ship go
            EndTetherPilot(pilot!);
            await CancelRepairsAsync(pilot!, cancellationToken);
            await _charges.CloseAsync(sessionId);
            _pilots.TryRemove(sessionId, out _);
            pilot!.End();
            _warping.TryRemove(sessionId, out _);
            _sim.Leave(sessionId, LeaveWhy.Jumped);
        }

        string? refusal;
        try
        {
            refusal = await _sim.EnterAsync(
                new SimEnter(sessionId, solarSystemId, position is null ? EnterHow.Anchor : EnterHow.InSpace, 0, spec, position, direction),
                cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            refusal = error.Message;
        }
        if (refusal is not null)
        {
            _log?.Invoke($"the relocation of character {characterId} to system {solarSystemId} was refused by the simulation: {refusal}");
            // the ship is not in any scene: it is recovered to the station its character last was at, as a ship the simulation loses is
            await _ships.RecoverFromSpaceAsync(characterId, PlaceOfStation, CancellationToken.None);
            return "Space is not available right now. Try again in a moment.";
        }

        TellEnteredBuffs(sessionId, snapshot.Ship.Id, _nowUnixMs());
        _pilots[sessionId] = new Pilot(session, characterId, moved.Ship.Id, moved.Ship.TypeId, solarSystemId, FileTimeNow());
        if (!wasInSpace)
        {
            session.Closed += () => OnSessionClosed(session);
        }

        var type = _statics.Type(moved.Ship.TypeId);
        var row = InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr(wasInSpace ? $"Undocking:{previousSystemId}" : $"Undocking:{previousStationId}"));
        if (wasInSpace)
        {
            session.SendNotification("OnItemChange", "clientID", row, new PyDict([new(new PyInt(3), InventoryWire.Id(moved.PreviousLocationId))]), None);
            session.Change(
                ("solarsystemid", new PyInt(solarSystemId)),
                ("solarsystemid2", new PyInt(solarSystemId)),
                ("constellationid", new PyInt(place.ConstellationId)),
                ("regionid", new PyInt(place.RegionId)),
                ("locationid", new PyInt(solarSystemId)));
        }
        else
        {
            session.SendNotification("OnItemsChanged", "charid", new PyList([row]), ChangedFrom(moved), None);
            session.Set("stationid2", None);
            session.Change(
                ("stationid", None),
                ("structureid", None),
                ("solarsystemid", new PyInt(solarSystemId)),
                ("solarsystemid2", new PyInt(solarSystemId)),
                ("constellationid", new PyInt(place.ConstellationId)),
                ("regionid", new PyInt(place.RegionId)),
                ("locationid", new PyInt(solarSystemId)));
            DockedLocationChanged?.Invoke(session, previousStationId, previousStructureId);
        }
        // the objects bound to the old place let go, as a jump's arrival lets them go (the client binds the new ballpark)
        ReleaseBoundObjects(session);
        if (wasInSpace && !_pilots.Values.Any(other => other.SolarSystemId == previousSystemId))
        {
            ForgetNpcsOf(previousSystemId);
        }
        SystemEntered?.Invoke(solarSystemId);
        _transitions.StartSessionChange(sessionId);
        await RefreshTetherPolicyAsync(session, cancellationToken);
        return null;
    }

    /// <summary>
    /// Where a connected pilot is, exactly (<c>/tr</c>): the place of its ship in space, or only its system (a null place) when it is docked; null when it is
    /// in no system.
    /// </summary>
    public async Task<(int SolarSystemId, IReadOnlyList<double>? Position)?> PilotPlaceAsync(ClientSession target, CancellationToken cancellationToken)
    {
        if (IsInSpace(target) && ShipIdOf(target) is { } shipId && await ScenePositionOfAsync(target, shipId, cancellationToken) is { } at)
        {
            return (at.SolarSystemId, new double[] { at.X, at.Y, at.Z });
        }
        if (target.GetInt("solarsystemid2") is { } system and > 0)
        {
            return ((int)system, null);
        }
        return null;
    }

    /// <summary>
    /// Where a game master goes to be with a connected pilot (<c>/tele</c>): <see cref="RelocationOffsetMeters"/> off the pilot's ship in space, facing it; a pilot
    /// that is docked is found at the anchor of its system. Null when the pilot is in no system.
    /// </summary>
    public async Task<(int SolarSystemId, IReadOnlyList<double>? Position, IReadOnlyList<double>? Direction)?> PilotDestinationAsync(ClientSession target, CancellationToken cancellationToken)
    {
        if (IsInSpace(target) && ShipIdOf(target) is { } shipId && await ScenePositionOfAsync(target, shipId, cancellationToken) is { } at)
        {
            return (at.SolarSystemId, [at.X + RelocationOffsetMeters, at.Y, at.Z], [-1d, 0d, 0d]);
        }
        if (target.GetInt("solarsystemid2") is { } system and > 0)
        {
            // the anchor: no place given, so the simulation puts the ship at the system's anchor
            IReadOnlyList<double>? noPlace = null;
            return ((int)system, noPlace, noPlace);
        }
        return null;
    }
}
