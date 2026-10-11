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
/// A jump bridge (the EVE University wiki, "Jump drives"): a Titan's Jump Portal Generator, or a Black Ops' Covert one, opens a bridge where the ship is to a
/// cynosural field lit in another system, and the fleet ships within 2 500 metres of it jump through to the field. The numbers are the SDE's (the generator's
/// attributes 6, 713, 714, 1001, 1002, 1298 and 3318, the hull's 866 to 868 and 1972, the skills' 1775 and the range attributes); the rules are the wiki's.
/// The bridge lives in the simulation (command 30) and is kept here for its fleet and its isotopes; a restart ends it, as the simulation's does.
/// </summary>
public sealed class SpaceBridges(
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
    /// <summary>An open bridge: its ship, its fleet, the systems, the field it goes to, the fuel rules of the bridging ship, and when it ends.</summary>
    public sealed record JumpBridgeRecord(
        long BridgerCharacterId, long BridgerShipId, long FleetId, int OriginSystemId, int DestinationSystemId, long BeaconId, long FieldId,
        int PassengerAttribute, int FuelTypeId, double FuelPerLightYear, double PortalMassFactor, double AdditionalConsumption, double FatigueMultiplier,
        long ExpiresAtMs);

    private readonly ConcurrentDictionary<long, JumpBridgeRecord> _bridges = new();

    /// <summary>The bridges open now, by the ship that opened each.</summary>
    public IReadOnlyCollection<JumpBridgeRecord> Open => _bridges.Values.ToArray();

    /// <summary>
    /// The ship of the session opens a bridge to the lit cynosural field <paramref name="fieldId"/> in <paramref name="destinationSystem"/> (command
    /// <c>CmdBridgeToFleetModuleBeacon</c>, or the deployable variant by the field's id). It needs the matching portal generator fitted, the skill, a lit field of
    /// its own fleet, a Titan only to a standard field and not in high security, the range of its jump drive, and the isotopes of the activation.
    /// </summary>
    public async Task OpenAsync(ClientSession session, long fieldId, int destinationSystem, CancellationToken cancellationToken)
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
            || field.SolarSystemId != destinationSystem)
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
        if (statics.Type(hullType) is not { } hull || !hull.Attributes.TryGetValue(JumpDriveRules.RangeAttribute, out var baseRange) || baseRange <= 0
            || !hull.Attributes.TryGetValue(JumpDriveRules.FuelPerLightYearAttribute, out var fuelPerLightYear)
            || !hull.Attributes.TryGetValue(JumpDriveRules.FuelTypeAttribute, out var jumpFuel))
        {
            throw Notify("Your ship has no jump drive.");
        }
        // the generator the ship fits: a Titan's standard bridge, or a Black Ops' covert one
        var generator = (await ships.FittedModulesAsync(character, shipId, cancellationToken))
            .FirstOrDefault(module => module.TypeId is JumpDriveRules.TitanGeneratorType or JumpDriveRules.CovertGeneratorType or JumpDriveRules.IndustrialGeneratorType);
        if (generator.Attributes is null || !generator.Attributes.TryGetValue(JumpDriveRules.PortalDurationAttribute, out var lifetime) || lifetime <= 0
            || !generator.Attributes.TryGetValue(JumpDriveRules.PortalPassengerAttribute, out var passengerAttribute)
            || !generator.Attributes.TryGetValue(1298, out var fitsGroup) || (int)fitsGroup != hull.GroupId)
        {
            throw Notify("Your ship is not fitted with an online jump portal generator for that cynosural field.");
        }
        var titan = generator.TypeId == JumpDriveRules.TitanGeneratorType;
        var industrial = generator.TypeId == JumpDriveRules.IndustrialGeneratorType;
        var kind = JumpDriveRules.KindOf(field.TypeId);
        // a Titan bridge goes to a standard field or a beacon (the wiki: "standard Cynosural Fields, or Cynosural Beacons"); a covert beacon then fails the cyno rule below
        if (titan && kind != JumpDriveRules.FieldKind.Standard && field.Kind != SpaceContainers.BeaconKind)
        {
            throw Notify("A titan jump bridge goes only to a standard cynosural field or a beacon.");
        }
        if (industrial && kind != JumpDriveRules.FieldKind.Industrial)
        {
            throw Notify("An industrial jump bridge goes only to an industrial cynosural field.");
        }
        // the bridging hull must be allowed to jump to that kind of field (the SDE's cyno rules: JF, capital industrial and Black Ops to industrial)
        if (!JumpDriveRules.MayJumpTo(kind, hull.GroupId))
        {
            throw Notify("That cynosural field does not take this ship.");
        }
        if (systems.Get(destinationSystem) is { } entered && JumpDriveRules.EntryRefusal(entered.RegionId) is { } entry)
        {
            throw Notify(entry);
        }
        // the capitals operate outside high security (the wiki, "Capital ships"): a Titan and a Rorqual are capitals
        if ((titan || industrial) && systems.Get(origin) is { } place && Security.IsHighSecOrHigher(Security.ClassFromLevel(place.Security)))
        {
            throw Notify("Jump drives cannot be used in high-security space.");
        }
        if (await LevelAsync(JumpDriveRules.JumpPortalGenerationSkill, character, cancellationToken) < 1)
        {
            throw Notify("Your pilot cannot use a jump portal generator yet (Jump Portal Generation at level one).");
        }
        // the bridging ship must be within its own jump range of the field (the wiki)
        var lightYears = systems.LightYearsBetween(origin, destinationSystem) ?? throw Refusal("DeniedShipChanged");
        var calibration = await LevelAsync(JumpDriveRules.CalibrationSkill, character, cancellationToken);
        if (lightYears > JumpDriveRules.Range(baseRange, calibration, await PerLevelAsync(JumpDriveRules.CalibrationSkill, JumpDriveRules.CalibrationPerLevelAttribute, cancellationToken)))
        {
            throw Notify("That cynosural field is out of your jump drive's range.");
        }
        // the activation's isotopes (the generator's 714 of 713) less the skill a level (1775); a covert generator has none (the SDE)
        var activationFuel = generator.Attributes.TryGetValue(JumpDriveRules.PortalFuelQuantityAttribute, out var activation)
            ? (long)Math.Ceiling(Math.Round(activation * (1 + await PerLevelAsync(JumpDriveRules.JumpPortalGenerationSkill, JumpDriveRules.JumpPortalGenerationPerLevelAttribute, cancellationToken)), 6))
            : 0;
        var activationType = generator.Attributes.TryGetValue(JumpDriveRules.PortalFuelTypeAttribute, out var strontium) ? (int)strontium : 0;
        if (activationFuel > 0 && await ships.FuelHeldAsync(character, shipId, activationType, cancellationToken) < activationFuel)
        {
            throw Notify("The bridge ship does not have enough jump portal activation fuel.");
        }
        var capacitor = generator.Attributes.TryGetValue(JumpDriveRules.ModuleCapacitorAttribute, out var need) ? need : 0;
        // the Titan holds still for its bridge's minute (the wiki: it immobilises the ship holding it open); a covert bridge does not (the wiki is silent)
        var immobile = titan ? (long)lifetime : 0;
        await space.OpenBridgeAsync(session, shipId, fieldId, destinationSystem, (long)lifetime, immobile, JumpDriveRules.BridgeRangeMeters, capacitor, cancellationToken);
        if (activationFuel > 0 && !await ships.DebitFuelAsync(character, shipId, activationType, activationFuel, cancellationToken))
        {
            // shortcut: the fuel was checked a moment before and another debit may have taken it; the bridge stands (the same shortcut as the jump's)
            log?.Invoke($"the activation fuel of character {character} was taken by another debit after the check");
        }
        var conservation = await PerLevelAsync(JumpDriveRules.FuelConservationSkill, JumpDriveRules.FuelBonusAttribute, cancellationToken);
        var conservationLevel = await LevelAsync(JumpDriveRules.FuelConservationSkill, character, cancellationToken);
        var record = new JumpBridgeRecord(
            character,
            shipId,
            fleet.Value,
            origin,
            destinationSystem,
            fieldId,
            fieldId,
            (int)passengerAttribute,
            (int)jumpFuel,
            fuelPerLightYear * (1 + conservation * conservationLevel),
            generator.Attributes.TryGetValue(JumpDriveRules.PortalMassFactorAttribute, out var massFactor) ? massFactor : 0,
            generator.Attributes.TryGetValue(JumpDriveRules.PortalAdditionalConsumptionAttribute, out var additional) ? additional : 0,
            hull.Attributes.TryGetValue(JumpDriveRules.JumpThroughFatigueAttribute, out var bridgeMultiplier) ? bridgeMultiplier : 1.0,
            now + (long)lifetime);
        _bridges[shipId] = record;
        await AnnounceAsync(record, true);
        // the opener is told the anchor is activated, with no passengers yet (EveJS notifyGroupJumpAnchor after the bridge opens)
        session.SendNotification("OnGroupJumpAnchorActivated", "clientID", new PyInt(destinationSystem), new PyInt(0));
    }

    /// <summary>
    /// The passenger of the session jumps through the bridge of the ship <paramref name="bridgerShipId"/> to the field <paramref name="beaconId"/> in
    /// <paramref name="solarSystemId"/> (command <c>CmdJumpThroughFleet</c>). The passenger is in the bridge's fleet, in its system, of a kind the portal takes
    /// (the wiki: a fleetmate), not cooling down; the isotopes come from the bridging ship and the passenger's fatigue is the wiki's, with the bridge's and the
    /// passenger's hull multipliers (1972 and 1971).
    /// </summary>
    public async Task<PyValue> JumpThroughAsync(
        ClientSession session, long bridgerShipId, long beaconId, int solarSystemId, string? boundObjectId, CancellationToken cancellationToken)
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
        if (!_bridges.TryGetValue(bridgerShipId, out var bridge) || bridge.ExpiresAtMs <= now || bridge.DestinationSystemId != solarSystemId
            || bridge.BeaconId != beaconId || bridge.OriginSystemId != origin)
        {
            throw Notify("That fleet bridge is no longer available.");
        }
        if (await fleets.FleetIdForCharacterAsync(character, cancellationToken) != bridge.FleetId)
        {
            throw Notify("You are not in the fleet of that bridge.");
        }
        if (await ships.ShipTypeOfAsync(character, shipId, cancellationToken) is not { } passengerType)
        {
            throw Refusal("DeniedShipChanged");
        }
        await statics.EnsureTypesAsync([passengerType], cancellationToken);
        if (statics.Type(passengerType) is not { } hull)
        {
            throw Refusal("DeniedShipChanged");
        }
        if (!JumpDriveRules.IsPassenger(bridge.PassengerAttribute, hull.Attributes))
        {
            throw Notify("Your ship cannot pass through that jump bridge.");
        }
        var state = await fatigue.GetAsync(character, cancellationToken);
        if (state.CooldownUntilMs > now)
        {
            throw Notify("Your jump drive is still cooling down.");
        }
        if (!hull.Attributes.TryGetValue(JumpDriveRules.MassAttribute, out var mass) || mass <= 0)
        {
            throw Notify("Your ship mass could not be calculated for the jump bridge.");
        }
        var lightYears = systems.LightYearsBetween(origin, solarSystemId) ?? throw Refusal("DeniedShipChanged");
        var quantity = JumpDriveRules.BridgeFuelQuantity(lightYears, mass, bridge.PortalMassFactor, bridge.FuelPerLightYear, bridge.AdditionalConsumption);
        if (await ships.FuelHeldAsync(bridge.BridgerCharacterId, bridge.BridgerShipId, bridge.FuelTypeId, cancellationToken) < quantity)
        {
            throw Notify("The bridge ship does not have enough jump portal fuel.");
        }
        var passengerMultiplier = hull.Attributes.TryGetValue(JumpDriveRules.FatigueMultiplierAttribute, out var own) ? own : 1.0;
        var (fatigueMs, cooldownMs) = JumpDriveRules.AfterJump(
            Math.Max(0, state.FatigueUntilMs - now), lightYears * passengerMultiplier * bridge.FatigueMultiplier);
        var next = new JumpFatigueState(now + fatigueMs, now + cooldownMs);

        // the simulation is asked first: a passage it refuses takes nothing
        var answer = await space.BridgeJumpAsync(session, shipId, bridge.BridgerShipId, bridge.FieldId, solarSystemId, boundObjectId, cancellationToken);
        if (!await ships.DebitBridgeAsync(fatigue, bridge.BridgerCharacterId, bridge.BridgerShipId, bridge.FuelTypeId, quantity, character, next, cancellationToken))
        {
            // shortcut: the fuel was checked a moment before; a debit lost in that moment lets the passage stand (as the jump drive's)
            log?.Invoke($"the bridge fuel of ship {bridge.BridgerShipId} was taken by another debit after the check");
        }
        return answer;
    }

    /// <summary>The bridges whose time is up are closed: their fleet is told (OnBridgeModeChange, not active). Run by the scheduler.</summary>
    public async Task SweepAsync()
    {
        var now = nowUnixMs();
        foreach (var bridge in _bridges.Values.Where(bridge => bridge.ExpiresAtMs <= now).ToArray())
        {
            if (_bridges.TryRemove(bridge.BridgerShipId, out _))
            {
                await AnnounceAsync(bridge, false);
            }
        }
    }

    /// <summary>Tells every member of the bridge's fleet that the bridge is open or closed (the client keeps the bridge for its jump menu).</summary>
    private async Task AnnounceAsync(JumpBridgeRecord bridge, bool active)
    {
        if (sessionOf is null || await fleets.SnapshotAsync(bridge.FleetId) is not { } snapshot)
        {
            return;
        }
        foreach (var member in snapshot.Members)
        {
            sessionOf(member.CharacterId)?.SendNotification(
                "OnBridgeModeChange", "fleetid",
                new PyLong(bridge.BridgerShipId), new PyInt(bridge.DestinationSystemId), new PyLong(bridge.BeaconId), active ? PyBool.True : PyBool.False);
        }
    }

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
