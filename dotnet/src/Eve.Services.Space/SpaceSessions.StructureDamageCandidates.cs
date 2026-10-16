using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Dogma;
using Eve.Services;
using Eve.Space;
using Eve.Ships;
using Eve.Universe;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    private const int MaxStructureDamageCandidates = 4096;

    // Effect publication is deliberately not consulted here. The SDE publishes several Standup
    // module types whose dogma effects are marked unpublished, including pointDefense and burst families.
    private static readonly HashSet<string> StructureDamageEffectNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pointDefense",
        "doomsdayHOG",
        "doomsdayAOEECM",
        "doomsdayAOEDamp",
        "doomsdayAOEWeb",
        "doomsdayAOEPaint",
        "doomsdayAOEBubble",
        "doomsdayAOETrack",
        "doomsdayAOENeut",
        "lightningWeapon",
    };

private sealed record SimDamageableEntityRegistration(
        int SolarSystemId, int TypeId, long OwnerId, long CorporationId, long AllianceId, long WarFactionId);

    private readonly ConcurrentDictionary<long, SimDamageableEntityRegistration> _simDamageableEntities = new();
    private readonly object _simDamageableEntitiesSync = new();

    public Func<long, (long CharacterId, int TypeId, int SolarSystemId, long CorporationId, long AllianceId, long WarFactionId)?>? StructureDamageDroneOwnerOf { get; set; }

    public Func<long, (long CharacterId, int TypeId, int SolarSystemId, long CorporationId, long AllianceId, long WarFactionId)?>? StructureDamageFighterOwnerOf { get; set; }

    private void UpsertSimDamageableEntity(SimDamageableEntityUpserted update)
    {
        var keyIsValid = update.SolarSystemId > 0
            && update.EntityId > 0
            && update.TypeId > 0
            && update.OwnerId >= 0
            && update.CorporationId >= 0
            && update.AllianceId >= 0
            && update.WarFactionId >= 0;

        lock (_simDamageableEntitiesSync)
        {
            if (!keyIsValid)
            {
                _simDamageableEntities.TryRemove(update.EntityId, out _);
                _log?.Invoke($"the simulation sent invalid identity metadata for entity {update.EntityId} in system {update.SolarSystemId}");
                return;
            }

            // Kind 88 is emitted only after successful insertion into the Sim's damageable scene set.
            // SDE health and owner sources are checked before this identity can authorize a target.
            _simDamageableEntities[update.EntityId] = new SimDamageableEntityRegistration(
                update.SolarSystemId, update.TypeId, update.OwnerId, update.CorporationId, update.AllianceId, update.WarFactionId);
        }
    }

    private void RemoveSimDamageableEntity(int solarSystemId, long entityId)
    {
        if (solarSystemId <= 0 || entityId <= 0)
            return;
        lock (_simDamageableEntitiesSync)
        {
            if (_simDamageableEntities.TryGetValue(entityId, out var current)
                && current.SolarSystemId == solarSystemId)
                _simDamageableEntities.TryRemove(entityId, out _);
        }
    }

    private void RemoveSimDamageableEntity(long entityId)
    {
        if (entityId <= 0)
            return;
        lock (_simDamageableEntitiesSync)
            _simDamageableEntities.TryRemove(entityId, out _);
    }

    private void ClearSimDamageableEntities()
    {
        lock (_simDamageableEntitiesSync)
            _simDamageableEntities.Clear();
    }

    private bool MatchesKnownNpcCreation(SimDamageableEntityRegistration entity)
    {
        if (entity.CorporationId <= 0
            || (entity.OwnerId != 0 && entity.OwnerId != entity.CorporationId))
            return false;

        var profileMatch = Npcs?.Authority.Profiles.Any(profile =>
            profile.ShipTypeId == entity.TypeId
            && profile.CorporationId == entity.CorporationId
            && profile.AllianceId == entity.AllianceId
            && profile.FactionId == entity.WarFactionId) == true;
        if (profileMatch && Eve.Npc.NpcHull.HasHitPoints(_statics.Type(entity.TypeId)))
            return true;

        var pochvenCorporation = entity.CorporationId is
            Eve.Services.Space.Pochven.PochvenRules.DrifterCorporationId
            or Eve.Services.Space.Pochven.PochvenRules.RogueDroneCorporationId
            or Eve.Services.Space.Pochven.PochvenRules.TriglavianCorporationId
            or Eve.Services.Space.Pochven.PochvenRules.EdencomCorporationId;
        return pochvenCorporation
            && entity.AllianceId == 0
            && entity.WarFactionId == 0
            && Eve.Npc.NpcHull.HasHitPoints(_statics.Type(entity.TypeId));
    }

    private bool IsKnownNpcCorporation(SimDamageableEntityRegistration entity) =>
        MatchesKnownNpcCreation(entity)
        || Npcs?.Authority.Profiles.Any(profile => profile.CorporationId == entity.CorporationId) == true
        || entity.CorporationId is
            Eve.Services.Space.Pochven.PochvenRules.DrifterCorporationId
            or Eve.Services.Space.Pochven.PochvenRules.RogueDroneCorporationId
            or Eve.Services.Space.Pochven.PochvenRules.TriglavianCorporationId
            or Eve.Services.Space.Pochven.PochvenRules.EdencomCorporationId;
    private enum StructureDamageCandidateKind : byte
    {
        Unknown,
        Player,
        Npc,
        PlayerCorporation,
    }

    private readonly record struct StructureDamageCandidateOwner(
        StructureDamageCandidateKind Kind,
        long OwnerId,
        bool IsCapsule = false);

    /// <summary>Reads the current persistent controller lease for a controlled structure session.</summary>
    public Func<ClientSession, long, CancellationToken, Task<long?>>? StructureControlLeaseGenerationOf { get; set; }

    /// <summary>Returns the owner of a currently launched or abandoned drone entity.</summary>


    /// <summary>Returns current, non-destroyed Upwell structure owners for candidate IDs in one solar system.</summary>
    public Func<IReadOnlyList<long>, int, CancellationToken, Task<IReadOnlyDictionary<long, (long OwnerCorporationId, int TypeId, long AllianceId)>>>? StructureDamageTargetCorporationsOf { get; set; }

    /// <summary>Evaluates a structure-owner corporation against a player structure's owner corporation.</summary>
    public Func<long, long, CancellationToken, Task<WeaponTargetContext>>? StructureDamageTargetCorporationContextOf { get; set; }

    internal static bool IsStructureDamageModuleType(DogmaStatic statics, TypeDef type) =>
        type.CategoryId == statics.CategoryId("Structure Module");

    internal static bool IsCapitalFieldTarget(TypeDef type) => type.Attributes.GetValueOrDefault(1785) > 0;

    private sealed record StructureDamageAuthorization(IReadOnlyList<long> AllowedIds, IReadOnlyList<long> MovedIds, IReadOnlyList<long> HeldIds);

    private async Task ReplyToStructureDamageCandidatesAsync(SimStructureDamageCandidatesRequest request)
    {
        StructureDamageAuthorization allowed = new([], [], []);
        try
        {
            allowed = await AuthorizeStructureDamageCandidatesAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log?.Invoke($"structure damage candidates for session {request.SessionId}, structure {request.StructureId}, module {request.ModuleId} failed closed: {error.Message}");
        }

        try
        {
            // Echo the complete correlation tuple exactly. The simulation owns pending-request matching;
            // this handler keeps no cached grant that a later pulse, hop, nonce, or lease could reuse.
            _sim.StructureDamageCandidatesResult(new SimStructureDamageCandidatesResult(
                request.SessionId,
                request.StructureId,
                request.ModuleId,
                request.EffectId,
                request.ActivationNonce,
                request.ControlLeaseGeneration,
                request.CycleId,
                request.PulseHopOrdinal,
                allowed.AllowedIds)
            {
                FieldMovedTargetIds = allowed.MovedIds,
                FieldHeldTargetIds = allowed.HeldIds,
            });
        }
        catch (Exception error)
        {
            _log?.Invoke($"structure damage authorization reply for session {request.SessionId} could not be sent: {error.Message}");
        }
    }

    private async Task<StructureDamageAuthorization> AuthorizeStructureDamageCandidatesAsync(
        SimStructureDamageCandidatesRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionId == 0
            || request.StructureId <= 0
            || request.ModuleId <= 0
            || request.EffectId <= 0
            || request.ActivationNonce == 0
            || request.CycleId == 0
            || request.ControlLeaseGeneration <= 0
            || request.CandidateIds is null
            || request.CandidateIds.Count is < 1 or > MaxStructureDamageCandidates)
            return new([], [], []);

        var candidateIds = request.CandidateIds.ToArray();
        var unique = new HashSet<long>();
        foreach (var candidateId in candidateIds)
            if (candidateId <= 0 || !unique.Add(candidateId))
                return new([], [], []);

        if (!_structureObservers.TryGetValue(request.SessionId, out var session)
            || !IsStructureObserver(session)
            || session.SessionId <= 0
            || (ulong)session.SessionId != request.SessionId
            || session.CharacterId is not { } characterId
            || session.GetInt("shipid") != request.StructureId
            || session.GetInt("structureid") != request.StructureId
            || session.GetInt("structureControlLeaseGeneration") != request.ControlLeaseGeneration
            || !_pilots.TryGetValue(request.SessionId, out var controller)
            || !ReferenceEquals(controller.Session, session)
            || controller.CharacterId != characterId
            || controller.SolarSystemId <= 0
            || StructureControlLeaseGenerationOf is not { } readLease)
            return new([], [], []);

        if (await readLease(session, request.StructureId, cancellationToken).ConfigureAwait(false) != request.ControlLeaseGeneration)
            return new([], [], []);

        if (await ModuleShipAsync(controller, cancellationToken).ConfigureAwait(false) is not { } structure
            || structure.Character.Id != characterId
            || structure.Ship.Id != request.StructureId
            || structure.Ship.OwnerId <= 0
            || !MatchesActiveStructureDamageModule(structure, controller, request, out _))
            return new([], [], []);

        IReadOnlyDictionary<long, (long OwnerCorporationId, int TypeId, long AllianceId)> structureOwners =
            new Dictionary<long, (long OwnerCorporationId, int TypeId, long AllianceId)>();
        if (StructureDamageTargetCorporationsOf is { } readStructures)
        {
            try
            {
                structureOwners = await readStructures(candidateIds, controller.SolarSystemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"structure damage candidate structure owners for {request.StructureId} could not be read: {error.Message}");
            }
        }

        var registrationTypes = candidateIds
            .Select(id => _simDamageableEntities.TryGetValue(id, out var entity) && entity.SolarSystemId == controller.SolarSystemId ? entity.TypeId : 0)
            .Where(typeId => typeId > 0)
            .Distinct();
        await _statics.EnsureTypesAsync(registrationTypes, cancellationToken).ConfigureAwait(false);

        var system = _systems.Get(controller.SolarSystemId);
        SecurityClass? securityClass = system is null ? null : Security.ClassFromLevel(system.Security);

        var abandoned = new Dictionary<long, EjectedShip>();
        foreach (var candidateId in candidateIds)
            if (_ejectedShips.TryGetValue(candidateId, out var hull)
                && hull.SolarSystemId == controller.SolarSystemId)
                abandoned[candidateId] = hull;

        var pilotsByShip = _pilots.Values
            .Where(target => target.CharacterId > 0
                && target.SolarSystemId == controller.SolarSystemId
                && !IsStructureObserver(target.Session)
                && IsInSpace(target.Session))
            .GroupBy(target => target.ShipId)
            .ToDictionary(group => group.Key, group => group.First());

        var droneOwners = new Dictionary<long, (long CharacterId, int TypeId, int SolarSystemId, long CorporationId, long AllianceId, long WarFactionId)>();
        if (StructureDamageDroneOwnerOf is { } droneOwnerOf)
            foreach (var candidateId in candidateIds)
                if (droneOwnerOf(candidateId) is { } droneOwner
                    && droneOwner.CharacterId > 0
                    && droneOwner.SolarSystemId == controller.SolarSystemId)
                    droneOwners[candidateId] = droneOwner;

        var fighterOwners = new Dictionary<long, (long CharacterId, int TypeId, int SolarSystemId, long CorporationId, long AllianceId, long WarFactionId)>();
        if (StructureDamageFighterOwnerOf is { } fighterOwnerOf)
            foreach (var candidateId in candidateIds)
                if (fighterOwnerOf(candidateId) is { } fighterOwner
                    && fighterOwner.CharacterId > 0
                    && fighterOwner.SolarSystemId == controller.SolarSystemId)
                    fighterOwners[candidateId] = fighterOwner;

        var candidateTypes = new Dictionary<long, int>(candidateIds.Length);
        var owners = new StructureDamageCandidateOwner[candidateIds.Length];
        for (var index = 0; index < candidateIds.Length; index++)
        {
            var candidateId = candidateIds[index];
            var hasRegistration = _simDamageableEntities.TryGetValue(candidateId, out var identity);
            if (hasRegistration && (identity!.SolarSystemId != controller.SolarSystemId
                || !Eve.Npc.NpcHull.HasHitPoints(_statics.Type(identity.TypeId))))
            {
                owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
                continue;
            }

            bool RegistrationMatches(long ownerId, long corporationId, long allianceId, long warFactionId, int typeId)
            {
                var valid = Eve.Npc.NpcHull.HasHitPoints(_statics.Type(typeId))
                    && (!hasRegistration
                        || identity is not null
                        && identity.SolarSystemId == controller.SolarSystemId
                        && identity.TypeId == typeId
                        && identity.OwnerId == ownerId
                        && identity.CorporationId == corporationId
                        && identity.AllianceId == allianceId
                        && identity.WarFactionId == warFactionId);
                if (valid) candidateTypes[candidateId] = typeId;
                return valid;
            }

            if (candidateId == structure.Ship.Id)
            {
                owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (abandoned.TryGetValue(candidateId, out var hull))
            {
                if (RegistrationMatches(hull.OwnerCharacterId, hull.OwnerCorporationId, hull.OwnerAllianceId, hull.OwnerWarFactionId, hull.ShipTypeId))
                    owners[index] = new(StructureDamageCandidateKind.Player, hull.OwnerCharacterId, hull.ShipTypeId == CharacterShips.CapsuleTypeId);
                else
                    owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (pilotsByShip.TryGetValue(candidateId, out var target))
            {
                var targetCorporationId = target.Session.GetInt("corpid") ?? target.Session.GetInt("corporationID") ?? 0;
                var targetAllianceId = target.Session.GetInt("allianceid") ?? target.Session.GetInt("allianceID") ?? 0;
                var targetWarFactionId = target.Session.GetInt("warfactionid") ?? target.Session.GetInt("warFactionID") ?? 0;
                if (RegistrationMatches(target.CharacterId, targetCorporationId, targetAllianceId, targetWarFactionId, target.TypeId))
                    owners[index] = new(StructureDamageCandidateKind.Player, target.CharacterId, target.TypeId == CharacterShips.CapsuleTypeId);
                else
                    owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (droneOwners.TryGetValue(candidateId, out var drone))
            {
                if (RegistrationMatches(drone.CharacterId, drone.CorporationId, drone.AllianceId, drone.WarFactionId, drone.TypeId))
                    owners[index] = new(StructureDamageCandidateKind.Player, drone.CharacterId);
                else
                    owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (fighterOwners.TryGetValue(candidateId, out var fighter))
            {
                if (RegistrationMatches(fighter.CharacterId, fighter.CorporationId, fighter.AllianceId, fighter.WarFactionId, fighter.TypeId))
                    owners[index] = new(StructureDamageCandidateKind.Player, fighter.CharacterId);
                else
                    owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (_npcShips.TryGetValue(candidateId, out var npc)
                && npc.SolarSystemId == controller.SolarSystemId
                && npc.Profile is { } profile)
            {
                if (RegistrationMatches(profile.CorporationId, profile.CorporationId, profile.AllianceId, profile.FactionId, npc.TypeId))
                    owners[index] = new(StructureDamageCandidateKind.Npc, 0);
                else
                    owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (structureOwners.TryGetValue(candidateId, out var targetStructure) && targetStructure.OwnerCorporationId > 0)
            {
                if (RegistrationMatches(
                    targetStructure.OwnerCorporationId,
                    targetStructure.OwnerCorporationId,
                    targetStructure.AllianceId,
                    0,
                    targetStructure.TypeId))
                    owners[index] = new(StructureDamageCandidateKind.PlayerCorporation, targetStructure.OwnerCorporationId);
                else
                    owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            else if (hasRegistration && MatchesKnownNpcCreation(identity!))
            {
                owners[index] = new(StructureDamageCandidateKind.Npc, 0);
            }
            else if (hasRegistration
                && identity!.OwnerId > 0
                && identity.OwnerId == identity.CorporationId
                && identity.CorporationId > 0
                && IsKnownNpcCorporation(identity))
            {
                owners[index] = new(StructureDamageCandidateKind.Npc, 0);
            }
            else if (hasRegistration && identity!.OwnerId > 0 && identity.OwnerId != identity.CorporationId)
            {
                owners[index] = new(StructureDamageCandidateKind.Player, identity.OwnerId);
            }
            else if (hasRegistration
                && identity!.OwnerId > 0
                && identity.OwnerId == identity.CorporationId
                && identity.CorporationId > 0)
            {
                owners[index] = new(StructureDamageCandidateKind.PlayerCorporation, identity.CorporationId);
            }
            else
            {
                // A zero owner is not an NPC classification. It is authorized only when the
                // current Host source or a validated NPC creation profile above identifies it.
                owners[index] = new(StructureDamageCandidateKind.Unknown, 0);
            }
            if (owners[index].Kind != StructureDamageCandidateKind.Unknown && hasRegistration)
                candidateTypes[candidateId] = identity!.TypeId;
        }

        var playerTargets = owners
            .Where(owner => owner.Kind == StructureDamageCandidateKind.Player)
            .Select(owner => (owner.OwnerId, owner.IsCapsule))
            .Distinct()
            .ToArray();
        var playerCorporations = owners
            .Where(owner => owner.Kind == StructureDamageCandidateKind.PlayerCorporation)
            .Select(owner => owner.OwnerId)
            .Distinct()
            .ToArray();
        var playerGrants = new ConcurrentDictionary<(long CharacterId, bool IsCapsule), bool>();
        var corporationGrants = new ConcurrentDictionary<long, bool>();

        if (securityClass is { } currentSecurity)
        {
            await Parallel.ForEachAsync(playerTargets, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                async (target, token) =>
                {
                    playerGrants[target] = await StructureAllowsDamageCharacterAsync(
                        structure, target.OwnerId, target.IsCapsule, currentSecurity, token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            await Parallel.ForEachAsync(playerCorporations, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                async (targetCorporationId, token) =>
                {
                    corporationGrants[targetCorporationId] = await StructureAllowsDamageCorporationAsync(
                        structure, targetCorporationId, currentSecurity, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        var grants = new List<long>(candidateIds.Length);
        for (var index = 0; index < candidateIds.Length; index++)
        {
            var owner = owners[index];
            var allowed = owner.Kind switch
            {
                StructureDamageCandidateKind.Npc => true,
                StructureDamageCandidateKind.Player => playerGrants.TryGetValue((owner.OwnerId, owner.IsCapsule), out var playerGrant) && playerGrant,
                StructureDamageCandidateKind.PlayerCorporation => corporationGrants.TryGetValue(owner.OwnerId, out var corporationGrant) && corporationGrant,
                _ => false,
            };
            if (allowed)
                grants.Add(candidateIds[index]);
        }

        // Re-read both the lease and fitted/online module after the per-target policy reads.
        // Any release/reacquire or module change during this request turns the entire reply into no-grant.
        if (await readLease(session, request.StructureId, cancellationToken).ConfigureAwait(false) != request.ControlLeaseGeneration
            || session.GetInt("structureControlLeaseGeneration") != request.ControlLeaseGeneration
            || await ModuleShipAsync(controller, cancellationToken).ConfigureAwait(false) is not { } currentStructure
            || currentStructure.Ship.Id != request.StructureId
            || currentStructure.Ship.OwnerId != structure.Ship.OwnerId
            || !MatchesActiveStructureDamageModule(currentStructure, controller, request, out _))
            return new([], [], []);

        if (!_statics.Effect(request.EffectId)!.Name.Equals("doomsdayHOG", StringComparison.OrdinalIgnoreCase))
            return new(grants, [], []);

        var moved = new List<long>(grants.Count);
        var held = new List<long>(grants.Count);
        foreach (var targetId in grants)
        {
            if (!candidateTypes.TryGetValue(targetId, out var typeId) || _statics.Type(typeId) is not { } type)
                return new([], [], []);
            (IsCapitalFieldTarget(type) ? held : moved).Add(targetId);
        }
        return new(grants, moved, held);
    }

    private bool MatchesActiveStructureDamageModule(
        ShipSnapshot structure,
        Pilot controller,
        SimStructureDamageCandidatesRequest request,
        out EffectDef effect)
    {
        effect = null!;
        var structureCategory = _statics.CategoryId("Structure");
        if (structureCategory is null
            || _statics.Type(structure.Ship.TypeId)?.CategoryId != structureCategory
            || structure.Fitted.FirstOrDefault(module => module.Id == request.ModuleId) is not { } module
            || _statics.Type(module.TypeId) is not { Published: true } type
            || !IsStructureDamageModuleType(_statics, type)
            || !type.EffectIds.Contains(request.EffectId)
            || _statics.Effect(request.EffectId) is not { } foundEffect
            || foundEffect.Category is not (EffectDef.Activation or EffectDef.Target)
            || !StructureDamageEffectNames.Contains(foundEffect.Name)
            || structure.World.Item(request.ModuleId) is not { State: >= ItemState.Online }
            || !controller.ActiveModules.TryGetValue(request.ModuleId, out var activeState)
            || activeState < ItemState.Active
            || !controller.RunningModules.TryGetValue(request.ModuleId, out var running)
            || !string.Equals(running.EffectName, foundEffect.Name, StringComparison.OrdinalIgnoreCase)
            || !FitsStructureDamageModule(structure, type, controller, running))
            return false;

        effect = foundEffect;
        return true;
    }

    private bool FitsStructureDamageModule(
        ShipSnapshot structure, TypeDef moduleType, Pilot controller, RunningModule running)
    {
        var hasStructureTypeRestriction = false;
        var fitsStructureType = false;
        for (var index = 1; index <= 11; index++)
        {
            if (_statics.AttributeId($"canFitShipType{index}") is not { } attributeId
                || !moduleType.Attributes.TryGetValue(attributeId, out var allowedType)
                || allowedType <= 0)
                continue;
            hasStructureTypeRestriction = true;
            fitsStructureType |= (int)allowedType == structure.Ship.TypeId;
        }
        if (hasStructureTypeRestriction && !fitsStructureType)
            return false;

        var maxTypeFitted = StaticAttribute(moduleType, "maxTypeFitted");
        if (maxTypeFitted > 0
            && structure.Fitted.Count(module => module.TypeId == moduleType.TypeId) > maxTypeFitted)
            return false;

        var maxGroupFitted = StaticAttribute(moduleType, "maxGroupFitted");
        if (maxGroupFitted > 0
            && structure.Fitted.Count(module => _statics.Type(module.TypeId)?.GroupId == moduleType.GroupId) > maxGroupFitted)
            return false;

        var maxGroupActive = StaticAttribute(moduleType, "maxGroupActive");
        if (maxGroupActive > 0)
        {
            var activeInGroup = structure.Fitted.Count(module =>
                _statics.Type(module.TypeId)?.GroupId == moduleType.GroupId
                && controller.ActiveModules.TryGetValue(module.Id, out var state)
                && state >= ItemState.Active);
            if (activeInGroup > maxGroupActive)
                return false;
        }

        return StaticAttribute(moduleType, "disallowRepeatingActivation") != 1 || running.Repeat <= 1;
    }

    private double StaticAttribute(TypeDef type, string name) =>
        _statics.AttributeId(name) is { } attributeId
        && type.Attributes.TryGetValue(attributeId, out var value)
            ? value
            : 0;

    private async Task<bool> StructureAllowsDamageCharacterAsync(
        ShipSnapshot structure, long targetCharacterId, bool targetIsCapsule, SecurityClass securityClass,
        CancellationToken cancellationToken)
    {
        if (structure.Ship.OwnerId <= 0 || targetCharacterId <= 0
            || StructureWeaponTargetContextOf is not { } read)
            return false;
        try
        {
            var facts = await read(structure.Ship.OwnerId, targetCharacterId, cancellationToken).ConfigureAwait(false);
            if (!facts.AttackerIsPlayer || !facts.TargetIsPlayerProperty)
                return false;
            facts = facts with { TargetIsCapsule = targetIsCapsule, SamePilot = false };
            return !WeaponSafety.StopsFire(securityClass, SafetyLevel.Full, facts);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"structure weapon legality for owner {structure.Ship.OwnerId} -> {targetCharacterId} failed: {error.Message}");
            return false;
        }
    }

    private async Task<bool> StructureAllowsDamageCorporationAsync(
        ShipSnapshot structure, long targetCorporationId, SecurityClass securityClass,
        CancellationToken cancellationToken)
    {
        if (structure.Ship.OwnerId <= 0 || targetCorporationId <= 0
            || StructureDamageTargetCorporationContextOf is not { } read)
            return false;
        try
        {
            var facts = await read(structure.Ship.OwnerId, targetCorporationId, cancellationToken).ConfigureAwait(false);
            return facts.AttackerIsPlayer
                && facts.TargetIsPlayerProperty
                && !WeaponSafety.StopsFire(securityClass, SafetyLevel.Full, facts with { SamePilot = false });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"structure weapon legality for owner corporation {structure.Ship.OwnerId} -> target corporation {targetCorporationId} failed: {error.Message}");
            return false;
        }
    }
}
