using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The capital jump drive for player-lit cynosural fields and accessible Pharolux beacons. Player-lit fields require fleet membership; the structure path
/// applies its service ACL before sharing the same ship, range, fuel, fatigue, cooldown, spool, and simulation rules (<see cref="SpaceSessions.CynoJumpAsync"/>).
/// </summary>
public sealed class SpaceJumpDrive(
    CharacterShips ships,
    DogmaStatic statics,
    PgDeployedStore deployed,
    PgJumpFatigueStore fatigue,
    PgJumpSpoolStore spools,
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
    /// beacon by its module (0 when the client names a deployable), and <paramref name="solarSystemId"/> the system of the field the client chose.
    /// </summary>
    /// <summary>
    /// Jump to an accessible online Pharolux. The caller performs the structure service ACL and system-jammer checks;
    /// this method applies the same ship, range, fuel, capacitor, cooldown, spool and fatigue rules as a field jump,
    /// without the fleet-membership rule that belongs only to player-lit cynosural fields.
    /// </summary>
    public async Task<PyValue> JumpToStructureBeaconAsync(
        ClientSession session, long structureId, int structureTypeId, int solarSystemId,
        string? boundObjectId, CancellationToken cancellationToken)
    {
        if (structureId <= 0 || structureTypeId <= 0 || solarSystemId <= 0)
        {
            throw Notify("That cynosural beacon is no longer available.");
        }
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0)
        {
            throw Refusal("DeniedShipChanged");
        }
        var origin = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (origin <= 0 || await space.PositionOfAsync(session, shipId, cancellationToken) is null)
        {
            throw Refusal("DeniedShipChanged");
        }
        if (origin == solarSystemId)
        {
            throw Notify("You are already in that solar system.");
        }
        return await JumpToTargetAsync(session, character, shipId, origin, structureId, structureTypeId, solarSystemId, boundObjectId, cancellationToken);
    }

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
            || !SpaceContainers.IsLitField(field, now)
            || field.SolarSystemId != solarSystemId
            || (ownerId != 0 && field.OwnerId != ownerId))
        {
            throw Notify("That cynosural field is not lit.");
        }
        if (field.SolarSystemId == origin)
        {
            throw Refusal("DeniedShipChanged");
        }

        var fleet = await fleets.FleetIdForCharacterAsync(character, cancellationToken);
        if (fleet is null || fleet != await fleets.FleetIdForCharacterAsync(field.OwnerId, cancellationToken))
        {
            throw Notify("You are not in the fleet of that cynosural field.");
        }
        return await JumpToTargetAsync(session, character, shipId, origin, fieldId, field.TypeId, field.SolarSystemId, boundObjectId, cancellationToken);
    }

    private async Task<PyValue> JumpToTargetAsync(
        ClientSession session, long character, long shipId, int origin,
        long targetId, int targetTypeId, int targetSolarSystemId, string? boundObjectId,
        CancellationToken cancellationToken)
    {

        if (systems.Get(origin) is { } place && Security.IsHighSecOrHigher(Security.ClassFromLevel(place.Security)))
        {
            throw Notify("Jump drives cannot be used in high-security space.");
        }
        if (await ships.ShipTypeOfAsync(character, shipId, cancellationToken) is not { } shipType)
        {
            throw Refusal("DeniedShipChanged");
        }
        await statics.EnsureTypesAsync([shipType], cancellationToken);
        // the hull's own numbers: the hull as dogma has it with no skills and no fitting, so a role bonus on the range, the fuel or the share (SDE, a passive
        // effect of the hull) is in them, and the skills (Fuel Conservation, Calibration) and the economizers, which also act on 868, are taken below, once each
        if (statics.Type(shipType) is not { } hull || await ships.LoadShipAsync(character, shipId, cancellationToken) is not { } flown)
        {
            throw Notify("Your ship has no jump drive.");
        }
        var numbers = new DogmaWorld(statics, character, shipId);
        numbers.Add(ItemDogma.Of(flown.Ship, character, flown.Ship.LocationId, flown.Ship.Flag, ItemState.Online));
        var baseRange = numbers.Get(shipId, JumpDriveRules.RangeAttribute);
        var spool = numbers.Get(shipId, JumpDriveRules.SpoolAttribute);
        var fuelPerLightYear = numbers.Get(shipId, JumpDriveRules.FuelPerLightYearAttribute);
        var fuelType = numbers.Get(shipId, JumpDriveRules.FuelTypeAttribute);
        var hullShare = numbers.Get(shipId, JumpDriveRules.CapacitorNeedAttribute);
        if (baseRange <= 0 || spool < 0 || fuelPerLightYear <= 0 || fuelType <= 0 || hullShare <= 0)
        {
            throw Notify("Your ship has no jump drive.");
        }
        if (!JumpDriveRules.MayJumpTo(JumpDriveRules.KindOf(targetTypeId), hull.GroupId))
        {
            throw Notify("That cynosural field does not take this ship.");
        }

        if (systems.Get(targetSolarSystemId) is { } destination && !JumpDriveRules.MayJumpInto(hull.GroupId, Eve.Universe.Security.ClassFromLevel(destination.Security)))
        {
            throw Notify("Jump freighters cannot jump into high-security space.");
        }
        if (systems.Get(targetSolarSystemId) is { } entered && JumpDriveRules.EntryRefusal(entered.RegionId) is { } entry)
        {
            throw Notify(entry);
        }
        var state = await fatigue.GetAsync(character, cancellationToken);
        if (state.CooldownUntilMs > nowUnixMs())
        {
            throw Notify("Your jump drive is still cooling down.");
        }
        var lightYears = systems.LightYearsBetween(origin, targetSolarSystemId) ?? throw Refusal("DeniedShipChanged");
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
            await PerLevelAsync(JumpDriveRules.FuelConservationSkill, JumpDriveRules.FuelBonusAttribute, cancellationToken),
            freighter ? await LevelAsync(JumpDriveRules.JumpFreighterSkill, character, cancellationToken) : 0,
            JumpDriveRules.EconomizerMultiplier(await EconomizerBonusesAsync(character, shipId, hull.GroupId, cancellationToken)));
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
        var now = nowUnixMs();
        var fatigueMultiplier = numbers.Get(shipId, JumpDriveRules.FatigueMultiplierAttribute);
        var multiplier = fatigueMultiplier > 0 ? fatigueMultiplier : 1.0;
        var (fatigueMs, cooldownMs) = JumpDriveRules.AfterJump(Math.Max(0, state.FatigueUntilMs - now), lightYears * multiplier);
        var next = new JumpFatigueState(now + fatigueMs, now + cooldownMs);
        var pending = new JumpSpool(character, shipId, targetId, targetSolarSystemId, now + (long)spool, fuelId, quantity, state);

        // Reserve the fuel and fatigue before asking the simulation. A refusal rolls this same durable spool back.
        if (!await ships.DebitJumpAsync(fatigue, spools, pending, next, cancellationToken))
        {
            throw Notify("Your ship does not have enough fuel for that jump.");
        }
        try
        {
            return await space.CynoJumpAsync(session, shipId, targetId, targetSolarSystemId, (long)spool, capacitorShare, boundObjectId, cancellationToken);
        }
        catch
        {
            try
            {
                await CancelAsync(pending, "the simulation refused the jump", CancellationToken.None);
            }
            catch (Exception refundError)
            {
                log?.Invoke($"the jump spool of character {character} to system {targetSolarSystemId} could not be refunded immediately: {refundError.Message}");
            }
            throw;
        }
    }

    /// <summary>
    /// Tells every member of the fleet of a field's lighter that the field is lit or gone (<c>OnFleetJumpBeaconModuleChange</c>, which the client's fleet service keeps
    /// Nothing is sent to a lighter who is in no fleet.
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
            var session = sessionOf(member.CharacterId);
            if (session is null)
            {
                continue;
            }
            if (field.Kind == SpaceContainers.BeaconKind)
            {
                session.SendNotification(
                    "OnFleetJumpBeaconDeployableChange", "fleetid",
                    new PyLong(field.DeployableId), new PyInt(field.SolarSystemId), new PyLong(field.DeployableId), new PyLong(field.OwnerId), lit ? PyBool.True : PyBool.False);
                continue;
            }
            session.SendNotification(
                "OnFleetJumpBeaconModuleChange", "fleetid",
                new PyLong(field.OwnerId), new PyInt(field.SolarSystemId), new PyLong(field.DeployableId), new PyInt(field.TypeId), lit ? PyBool.True : PyBool.False);
        }
    }

    /// <summary>The spool is over and the ship has jumped, so there is nothing to refund.</summary>
    public Task ArrivedAsync(long characterId, CancellationToken cancellationToken = default) => spools.ForgetAsync(characterId, cancellationToken);

    /// <summary>
    /// At the start of the host: the sessions and the spools of the simulation did not survive a restart, so every spool in progress is cancelled and refunded.
    /// </summary>
    public async Task CancelInterruptedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var spool in await spools.ListAsync(cancellationToken))
        {
            await CancelAsync(spool, "the host restarted", cancellationToken);
        }
    }

    /// <summary>
    /// A spool the simulation did not complete: its time is over and the ship has not jumped <see cref="SpoolGraceMs"/> after.
    /// shortcut: a minute is the grace after a spool ends; no source read gives a rule for a spool the simulation loses (DEVIATIONS, Track A8).
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = nowUnixMs() - SpoolGraceMs;
        foreach (var spool in await spools.ListAsync(cancellationToken))
        {
            if (spool.EndsAtMs < cutoff)
            {
                await CancelAsync(spool, "the simulation did not complete it", cancellationToken);
            }
        }
    }

    /// <summary>The grace after a spool ends before the host takes it to have been lost: a minute.</summary>
    public const long SpoolGraceMs = 60_000;

    private async Task CancelAsync(JumpSpool spool, string why, CancellationToken cancellationToken)
    {
        if (await ships.CancelJumpAsync(fatigue, spools, spool.CharacterId, spool.EndsAtMs, cancellationToken))
        {
            space.CancelCynoTetherArrival(spool.CharacterId);
            log?.Invoke($"the jump spool of character {spool.CharacterId} to system {spool.DestinationSystem} was cancelled ({why}): its fuel is refunded");
        }
    }

    /// <summary>
    /// The fuel bonuses (fractions) of the economizers fitted to the ship that fit its hull: an economizer that does not fit the hull is not counted (the
    /// </summary>
    private async Task<IReadOnlyList<double>> EconomizerBonusesAsync(long characterId, long shipId, int hullGroupId, CancellationToken cancellationToken)
    {
        var bonuses = new List<double>();
        foreach (var (typeId, attributes) in await ships.FittedModulesAsync(characterId, shipId, cancellationToken))
        {
            if (statics.Type(typeId) is { GroupId: JumpDriveRules.EconomizerGroup }
                && JumpDriveRules.EconomizerFits(hullGroupId, attributes)
                && attributes.TryGetValue(JumpDriveRules.FuelBonusAttribute, out var percent))
            {
                bonuses.Add(percent / 100);
            }
        }
        return bonuses;
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
