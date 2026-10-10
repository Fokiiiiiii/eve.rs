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
        if (!JumpDriveRules.MayJumpTo(JumpDriveRules.KindOf(field.TypeId), hull.GroupId))
        {
            throw Notify("That cynosural field does not take this ship.");
        }
        // a jump freighter does not jump into high-security space (the wiki's Jump Freighters page); the wording is ours
        if (systems.Get(field.SolarSystemId) is { } destination && !JumpDriveRules.MayJumpInto(hull.GroupId, Eve.Universe.Security.ClassFromLevel(destination.Security)))
        {
            throw Notify("Jump freighters cannot jump into high-security space.");
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
        var fatigueMultiplier = numbers.Get(shipId, JumpDriveRules.FatigueMultiplierAttribute);
        var multiplier = fatigueMultiplier > 0 ? fatigueMultiplier : 1.0;
        var (fatigueMs, cooldownMs) = JumpDriveRules.AfterJump(Math.Max(0, state.FatigueUntilMs - now), lightYears * multiplier);
        var next = new JumpFatigueState(now + fatigueMs, now + cooldownMs);

        // the simulation is asked first: a spool it refuses takes nothing from the ship
        var reply = await space.CynoJumpAsync(session, shipId, fieldId, field.SolarSystemId, (long)spool, capacitorShare, boundObjectId, cancellationToken);
        var pending = new JumpSpool(character, shipId, fieldId, field.SolarSystemId, now + (long)spool, fuelId, quantity, state);
        if (!await ships.DebitJumpAsync(fatigue, spools, pending, next, cancellationToken))
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
            log?.Invoke($"the jump spool of character {spool.CharacterId} to system {spool.DestinationSystem} was cancelled ({why}): its fuel is refunded");
        }
    }

    /// <summary>
    /// The fuel bonuses (fractions) of the economizers fitted to the ship that fit its hull: an economizer that does not fit the hull is not counted (the
    /// wiki: only Jump Freighters and Rorquals carry them, and the SDE names the groups each fits, attributes 1298 and 1299).
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
