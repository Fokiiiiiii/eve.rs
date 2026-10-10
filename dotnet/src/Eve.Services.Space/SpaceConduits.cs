using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// A Black Ops conduit jump (the EVE University wiki, "Jump drives": a Black Ops with its Covert Jump Portal Generator fitted takes up to 30 ships in a 10 km
/// radius through to a cynosural field of its fleet; a conduit jump costs isotopes a light year, not by mass). The numbers are the SDE's (the hull's 3131, 3133 and
/// 866 to 868, the generator's 3126, 1298 and 3318 family, the passenger flag 3322); the reach is the wiki's and EveJS's (10 km). The anchor and the passengers
/// jump at once (EveJS conduitJumpRuntime.js executeConduitPassengerJumps; no spool). A passenger is a fleetmate of the anchor, in its system and its 10 km,
/// who is eligible (3322, its SDE default 0) and not cooling down; the others are not taken, as EveJS takes only those in range.
/// </summary>
public sealed class SpaceConduits(
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
    /// The conduit jump of the session's Black Ops to the lit cynosural field <paramref name="fieldId"/> in <paramref name="destinationSystem"/> (command
    /// <c>CmdGroupJumpToFleetModuleBeacon</c>, or the deployable variant by the field's id).
    /// </summary>
    public async Task<PyValue> GroupJumpAsync(ClientSession session, long fieldId, int destinationSystem, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0)
        {
            throw Refusal("DeniedShipChanged");
        }
        var origin = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (origin <= 0 || await space.PositionOfAsync(session, shipId, cancellationToken) is not { } anchorAt)
        {
            throw Refusal("DeniedShipChanged");
        }
        var now = nowUnixMs();
        if (await deployed.GetAsync(fieldId, cancellationToken) is not { } field
            || !SpaceContainers.IsLitField(field, now)
            || field.SolarSystemId != destinationSystem
            || field.SolarSystemId == origin)
        {
            throw Notify("That cynosural field is not lit.");
        }
        var fleet = await fleets.FleetIdForCharacterAsync(character, cancellationToken);
        if (fleet is null || fleet != await fleets.FleetIdForCharacterAsync(field.OwnerId, cancellationToken))
        {
            throw Notify("You are not in the fleet of that cynosural field.");
        }
        if (await ships.ShipTypeOfAsync(character, shipId, cancellationToken) is not { } hullType)
        {
            throw Refusal("DeniedShipChanged");
        }
        await statics.EnsureTypesAsync([hullType], cancellationToken);
        if (statics.Type(hullType) is not { } hull || hull.GroupId != JumpDriveRules.BlackOpsGroup)
        {
            throw Notify("Only a Black Ops battleship makes a conduit jump.");
        }
        if (!hull.Attributes.TryGetValue(JumpDriveRules.RangeAttribute, out var baseRange) || baseRange <= 0
            || !hull.Attributes.TryGetValue(JumpDriveRules.FuelTypeAttribute, out var fuelType)
            || !hull.Attributes.TryGetValue(JumpDriveRules.ConduitFuelAttribute, out var fuelPerLightYear))
        {
            throw Notify("Your ship has no conduit.");
        }
        // the conduit is the covert generator's: it fits the hull the SDE names (1298) and enables the conduit (3126)
        var generator = (await ships.FittedModulesAsync(character, shipId, cancellationToken))
            .FirstOrDefault(module => module.TypeId == JumpDriveRules.CovertGeneratorType);
        if (generator.Attributes is null
            || !generator.Attributes.TryGetValue(JumpDriveRules.ConduitEnableAttribute, out var enabled) || enabled <= 0
            || !generator.Attributes.TryGetValue(1298, out var fitsGroup) || (int)fitsGroup != hull.GroupId)
        {
            throw Notify("Your ship is not fitted with an online covert jump portal generator for a conduit jump.");
        }
        if (await LevelAsync(JumpDriveRules.JumpPortalGenerationSkill, character, cancellationToken) < 1)
        {
            throw Notify("Your pilot cannot use a jump portal generator yet (Jump Portal Generation at level one).");
        }
        if (!JumpDriveRules.MayJumpTo(JumpDriveRules.KindOf(field.TypeId), hull.GroupId))
        {
            throw Notify("That cynosural field does not take this ship.");
        }
        var lightYears = systems.LightYearsBetween(origin, destinationSystem) ?? throw Refusal("DeniedShipChanged");
        var calibration = await LevelAsync(JumpDriveRules.CalibrationSkill, character, cancellationToken);
        if (lightYears > JumpDriveRules.Range(baseRange, calibration, await PerLevelAsync(JumpDriveRules.CalibrationSkill, JumpDriveRules.CalibrationPerLevelAttribute, cancellationToken)))
        {
            throw Notify("That cynosural field is out of your jump drive's range.");
        }
        var conservationLevel = await LevelAsync(JumpDriveRules.FuelConservationSkill, character, cancellationToken);
        var quantity = JumpDriveRules.FuelQuantity(
            lightYears, fuelPerLightYear, conservationLevel,
            await PerLevelAsync(JumpDriveRules.FuelConservationSkill, JumpDriveRules.FuelBonusAttribute, cancellationToken), 0, 1.0);
        if (await ships.FuelHeldAsync(character, shipId, (int)fuelType, cancellationToken) < quantity)
        {
            throw Notify("Your ship does not have enough jump fuel for the conduit.");
        }

        // the passengers: the fleet's ships in this system within the conduit's reach, eligible and not cooling down, up to the hull's count (3133)
        var now2 = now;
        var limit = hull.Attributes.TryGetValue(JumpDriveRules.ConduitPassengerCountAttribute, out var count) ? (int)count : 0;
        var passengers = new List<(long Character, long Ship, JumpFatigueState Next)>();
        var snapshot = await fleets.SnapshotAsync(fleet.Value, cancellationToken);
        foreach (var member in snapshot?.Members ?? [])
        {
            if (member.CharacterId == character || passengers.Count >= limit)
            {
                continue;
            }
            if (sessionOf?.Invoke(member.CharacterId) is not { } memberSession || memberSession.GetInt("shipid") is not { } memberShip || memberShip <= 0)
            {
                continue;
            }
            if (await space.PositionOfAsync(memberSession, memberShip, cancellationToken) is not { } memberAt
                || memberAt.SolarSystemId != origin
                || Distance(anchorAt, memberAt) > JumpDriveRules.ConduitRangeMeters)
            {
                continue;
            }
            if (await ships.ShipTypeOfAsync(member.CharacterId, memberShip, cancellationToken) is not { } memberType)
            {
                continue;
            }
            await statics.EnsureTypesAsync([memberType], cancellationToken);
            if (statics.Type(memberType) is not { } memberHull
                || !JumpDriveRules.IsPassenger(JumpDriveRules.ConduitPassengerAttribute, memberHull.Attributes))
            {
                continue;
            }
            var memberState = await fatigue.GetAsync(member.CharacterId, cancellationToken);
            if (memberState.CooldownUntilMs > now2)
            {
                continue;
            }
            var memberMultiplier = memberHull.Attributes.TryGetValue(JumpDriveRules.FatigueMultiplierAttribute, out var own) ? own : 1.0;
            var (memberFatigue, memberCooldown) = JumpDriveRules.AfterJump(Math.Max(0, memberState.FatigueUntilMs - now2), lightYears * memberMultiplier);
            passengers.Add((member.CharacterId, memberShip, new JumpFatigueState(now2 + memberFatigue, now2 + memberCooldown)));
        }

        // the anchor's fatigue is the jump drive's own (its hull's 1971); the passengers' the same rule with theirs
        var anchorState = await fatigue.GetAsync(character, cancellationToken);
        var anchorMultiplier = hull.Attributes.TryGetValue(JumpDriveRules.FatigueMultiplierAttribute, out var anchorOwn) ? anchorOwn : 1.0;
        var (anchorFatigue, anchorCooldown) = JumpDriveRules.AfterJump(Math.Max(0, anchorState.FatigueUntilMs - now2), lightYears * anchorMultiplier);
        var states = new Dictionary<long, JumpFatigueState> { [character] = new JumpFatigueState(now2 + anchorFatigue, now2 + anchorCooldown) };
        foreach (var passenger in passengers)
        {
            states[passenger.Character] = passenger.Next;
        }

        // the simulation is asked first: a group it refuses takes nothing
        var answer = await space.ConduitJumpAsync(session, shipId, fieldId, destinationSystem, passengers.Select(p => p.Ship).ToArray(), boundObjectId, cancellationToken);
        if (!await ships.DebitGroupJumpAsync(fatigue, character, shipId, (int)fuelType, quantity, states, cancellationToken))
        {
            // shortcut: the fuel was checked a moment before; a debit lost in that moment lets the group stand (as the jump drive's does)
            log?.Invoke($"the conduit fuel of ship {shipId} was taken by another debit after the check");
        }
        session.SendNotification("OnGroupJumpAnchorActivated", "clientID", new PyInt(destinationSystem), new PyInt(passengers.Count));
        foreach (var passenger in passengers)
        {
            sessionOf?.Invoke(passenger.Character)?.SendNotification(
                "OnGroupJumpedPassenger", "clientID", new PyLong(character), new PyInt(destinationSystem));
        }
        return answer;
    }

    private static double Distance(WarpCoordinates a, WarpCoordinates b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>A skill's level of a character, 0 when it lacks the skill.</summary>
    private async Task<int> LevelAsync(int skillTypeId, long characterId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await skills.LevelAsync(connection, null!, characterId, skillTypeId, cancellationToken);
    }

    /// <summary>A skill's percentage a level as a fraction, from its own SDE attribute.</summary>
    private async Task<double> PerLevelAsync(int skillTypeId, int attribute, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([skillTypeId], cancellationToken);
        return statics.Type(skillTypeId) is { } skill && skill.Attributes.TryGetValue(attribute, out var percent)
            ? percent / 100
            : throw new InvalidOperationException($"skill {skillTypeId} has no attribute {attribute} in the SDE");
    }

    private static MachoWrappedException Refusal(string name) => MachoErrors.ThrowUserError(name);

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
