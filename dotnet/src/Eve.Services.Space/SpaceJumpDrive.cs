using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The capital jump drive to a cynosural field (the reference's fleet cyno jump, <c>CmdJumpToFleetModuleBeacon</c> and <c>CmdJumpToFleetDeployableBeacon</c>, and
/// the EVE University wiki's rules for it): the field is lit, in the pilot's fleet, in another system within range and of a kind the ship may use; the drive
/// has its fuel and is not cooling down. The fuel and the new fatigue are taken when the simulation accepts the spool; the spool and the jump are the
/// simulation's (<see cref="SpaceSessions.CynoJumpAsync"/>).
/// </summary>
public sealed class SpaceJumpDrive(
    CharacterShips ships,
    DogmaStatic statics,
    PgDeployedStore deployed,
    PgJumpFatigueStore fatigue,
    SolarSystemCatalog systems,
    PgSkillStore skills,
    NpgsqlDataSource source,
    SpaceSessions space,
    FleetStore fleets,
    Func<long> nowUnixMs,
    Action<string>? log = null,
    Func<long, ClientSession?>? sessionOf = null)
{
    /// <summary>
    /// The jump to a field, as the client asks it: <paramref name="fieldId"/> is the field, <paramref name="ownerId"/> the character who lit it when the client names the
    /// beacon by its module (0 when the client names a deployable), and <paramref name="solarSystemId"/> the system of the field the client chose.
    /// </summary>
    public async Task<PyValue> JumpToFieldAsync(ClientSession session, long fieldId, long ownerId, int solarSystemId, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0)
        {
            throw Refusal("DeniedShipChanged");
        }
        var origin = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (origin <= 0 || await space.PositionOfAsync(session, shipId, cancellationToken) is null)
        {
            throw Refusal("DeniedShipChanged");
        }
        var now = nowUnixMs();
        if (await deployed.GetAsync(fieldId, cancellationToken) is not { } field
            || field.Kind != SpaceContainers.CynoFieldKind
            || field.ExpiresAtMs <= now
            || field.SolarSystemId != solarSystemId
            || (ownerId != 0 && field.OwnerId != ownerId))
        {
            throw Notify("That cynosural field is not lit.");
        }
        if (field.SolarSystemId == origin)
        {
            throw Refusal("DeniedShipChanged");
        }
        // only the members of the field's fleet may jump to it (the wiki: a field is lit for a fleet)
        var fleet = await fleets.FleetIdForCharacterAsync(character, cancellationToken);
        if (fleet is null || fleet != await fleets.FleetIdForCharacterAsync(field.OwnerId, cancellationToken))
        {
            throw Notify("You are not in the fleet of that cynosural field.");
        }
        // capitals only operate outside high-security space (the wiki, "Capital ships")
        if (systems.Get(origin) is { } place && Security.IsHighSecOrHigher(Security.ClassFromLevel(place.Security)))
        {
            throw Notify("Jump drives cannot be used in high-security space.");
        }
        if (await ships.ShipTypeOfAsync(character, shipId, cancellationToken) is not { } shipType)
        {
            throw Refusal("DeniedShipChanged");
        }
        await statics.EnsureTypesAsync([shipType], cancellationToken);
        if (statics.Type(shipType) is not { } hull || !hull.Attributes.TryGetValue(JumpDriveRules.RangeAttribute, out var baseRange) || baseRange <= 0
            || !hull.Attributes.TryGetValue(JumpDriveRules.SpoolAttribute, out var spool) || !hull.Attributes.TryGetValue(JumpDriveRules.FuelPerLightYearAttribute, out var fuelPerLightYear)
            || !hull.Attributes.TryGetValue(JumpDriveRules.FuelTypeAttribute, out var fuelType)
            || !hull.Attributes.TryGetValue(JumpDriveRules.CapacitorNeedAttribute, out var hullShare))
        {
            throw Notify("Your ship has no jump drive.");
        }
        if (!JumpDriveRules.MayJumpTo(JumpDriveRules.KindOf(field.TypeId), hull.GroupId))
        {
            throw Notify("That cynosural field does not take this ship.");
        }
        var state = await fatigue.GetAsync(character, cancellationToken);
        if (state.CooldownUntilMs > now)
        {
            throw Notify("Your jump drive is still cooling down.");
        }
        var lightYears = systems.LightYearsBetween(origin, field.SolarSystemId) ?? throw Refusal("DeniedShipChanged");
        var calibration = await LevelAsync(JumpDriveRules.CalibrationSkill, character, cancellationToken);
        var calibrationPerLevel = await PerLevelAsync(JumpDriveRules.CalibrationSkill, JumpDriveRules.CalibrationPerLevelAttribute, cancellationToken);
        if (lightYears > JumpDriveRules.Range(baseRange, calibration, calibrationPerLevel))
        {
            throw Notify("That cynosural field is out of your jump drive's range.");
        }
        var freighter = hull.GroupId == JumpDriveRules.JumpFreighterGroup;
        var quantity = JumpDriveRules.FuelQuantity(
            lightYears,
            fuelPerLightYear,
            await LevelAsync(JumpDriveRules.FuelConservationSkill, character, cancellationToken),
            await PerLevelAsync(JumpDriveRules.FuelConservationSkill, JumpDriveRules.FuelConservationPerLevelAttribute, cancellationToken),
            freighter ? await LevelAsync(JumpDriveRules.JumpFreighterSkill, character, cancellationToken) : 0);
        var fuelId = (int)fuelType;
        if (await ships.FuelHeldAsync(character, shipId, fuelId, cancellationToken) < quantity)
        {
            throw Notify("Your ship does not have enough fuel for that jump.");
        }
        // the activation's capacitor share: the hull's (SDE 898) less the pilot's Jump Drive Operation (SDE 899 of the skill, a percentage a level)
        var capacitorShare = JumpDriveRules.CapacitorNeed(
            hullShare,
            await LevelAsync(JumpDriveRules.OperationSkill, character, cancellationToken),
            await PerLevelAsync(JumpDriveRules.OperationSkill, JumpDriveRules.CapacitorNeedPerLevelAttribute, cancellationToken));
        var multiplier = hull.Attributes.TryGetValue(JumpDriveRules.FatigueMultiplierAttribute, out var fatigueMultiplier) ? fatigueMultiplier : 1.0;
        var (fatigueMs, cooldownMs) = JumpDriveRules.AfterJump(Math.Max(0, state.FatigueUntilMs - now), lightYears * multiplier);
        var next = new JumpFatigueState(now + fatigueMs, now + cooldownMs);

        // the simulation is asked first: a spool it refuses takes nothing from the ship
        var reply = await space.CynoJumpAsync(session, shipId, fieldId, field.SolarSystemId, (long)spool, capacitorShare, boundObjectId, cancellationToken);
        if (!await ships.DebitJumpAsync(fatigue, character, shipId, fuelId, quantity, next, cancellationToken))
        {
            // shortcut: the fuel was checked a moment before and the ship's module cycles are the only other debit; a debit lost in that moment lets the spool stand
            log?.Invoke($"the jump fuel of character {character} was taken by another debit after the check");
        }
        return reply;
    }

    /// <summary>
    /// Tells every member of the fleet of a field's lighter that the field is lit or gone (<c>OnFleetJumpBeaconModuleChange</c>, which the client's fleet service keeps
    /// the jump targets in). Nothing is sent to a lighter who is in no fleet.
    /// </summary>
    public async Task AnnounceFieldAsync(DeployedRecord field, bool lit)
    {
        if (fleets is null || sessionOf is null || await fleets.FleetIdForCharacterAsync(field.OwnerId) is not { } fleet
            || await fleets.SnapshotAsync(fleet) is not { } snapshot)
        {
            return;
        }
        foreach (var member in snapshot.Members)
        {
            sessionOf(member.CharacterId)?.SendNotification(
                "OnFleetJumpBeaconModuleChange", "fleetid",
                new PyLong(field.OwnerId), new PyInt(field.SolarSystemId), new PyLong(field.DeployableId), new PyInt(field.TypeId), lit ? PyBool.True : PyBool.False);
        }
    }

    /// <summary>A skill's percentage a level as a fraction, from its own SDE attribute (a missing attribute is a data fault, not a zero).</summary>
    private async Task<double> PerLevelAsync(int skillTypeId, int attribute, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([skillTypeId], cancellationToken);
        return statics.Type(skillTypeId) is { } skill && skill.Attributes.TryGetValue(attribute, out var percent)
            ? percent / 100
            : throw new InvalidOperationException($"skill {skillTypeId} has no attribute {attribute} in the SDE");
    }

    /// <summary>A character's skill level, 0 when the character lacks it.</summary>
    private async Task<int> LevelAsync(int skillTypeId, long characterId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await skills.LevelAsync(connection, null!, characterId, skillTypeId, cancellationToken);
    }

    private static MachoWrappedException Refusal(string name) => MachoErrors.ThrowUserError(name);

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
