using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private const double PvpFilamentFleetRangeMeters = 1_000_000;

    private async ValueTask<PvpFilamentAdmissionResult> AdmitPvpFilamentQueueAsync(
        ServiceCall call, PvpFilamentEvent eventInfo, CancellationToken cancellationToken)
    {
        var errors = new List<PvpFilamentQueueError>();
        void Add(int code, params PyValue[] arguments) => errors.Add(new(code, arguments));
        PvpFilamentAdmissionResult Result() => new([], errors.AsReadOnly());

        var characterId = call.Session.CharacterId ?? call.Session.GetInt("characterID") ?? call.Session.GetInt("characterId") ?? 0;
        if (characterId <= 0 || characterSessions.Of(characterId) != call.Session)
        {
            Add(PvpFilamentJumpErrorCodes.ShipNotFound);
            return Result();
        }

        var space = spaceSessions;
        if (space is null)
        {
            Add(PvpFilamentJumpErrorCodes.ShipNotFound);
            return Result();
        }

        var shipId = space.ShipIdOf(call.Session);
        var solarSystemId = space.SolarSystemOf(call.Session);
        var ownerShipId = shipId.GetValueOrDefault();
        var ownerSolarSystemId = solarSystemId.GetValueOrDefault();

        async ValueTask<(long ShipId, int ShipTypeId, int SolarSystemId, Item? ShipItem, bool IsLive)> AddActorStateErrorsAsync(
            ClientSession session, long participantId)
        {
            if (characterSessions.Of(participantId) != session)
            {
                Add(PvpFilamentJumpErrorCodes.ShipNotFound);
                return (0, 0, 0, null, false);
            }

            var participantShipId = space.ShipIdOf(session).GetValueOrDefault();
            var participantShipTypeId = space.ShipTypeIdOf(session).GetValueOrDefault();
            var participantSolarSystemId = space.SolarSystemOf(session).GetValueOrDefault();
            var shipItem = participantShipId > 0
                ? await items.GetAsync(participantShipId, cancellationToken).ConfigureAwait(false)
                : null;
            var participantType = participantShipTypeId > 0 ? dogma.Type(participantShipTypeId) : null;
            var currentShip = participantShipId > 0 && participantType?.CategoryId == 6 &&
                session.GetInt("shipid") == participantShipId &&
                shipItem is not null && shipItem.OwnerId == participantId && shipItem.TypeId == participantShipTypeId &&
                shipItem.LocationId == (session.DockedLocationId is > 0 ? session.DockedLocationId : participantSolarSystemId);
            var live = space.IsInSpace(session) && participantSolarSystemId > 0 && currentShip &&
                shipItem!.LocationId == participantSolarSystemId;
            if (!currentShip || (session.DockedLocationId is not > 0 && !live))
                Add(PvpFilamentJumpErrorCodes.ShipNotFound);
            if (session.DockedLocationId is > 0)
                Add(PvpFilamentJumpErrorCodes.ShipIsDocked, new PyInt(participantId));
            var canCloakAttributeId = dogma.AttributeId("canCloak");
            if (canCloakAttributeId is { } canCloakId &&
                (await items.InLocationAsync(participantShipId, participantId, cancellationToken).ConfigureAwait(false))
                    .Any(module => Slots.IsSlotFlag(module.Flag) && space.IsModuleActive(session, module.Id) &&
                        dogma.Type(module.TypeId)?.Attributes.TryGetValue(canCloakId, out var canCloak) == true && canCloak > 0))
                Add(PvpFilamentJumpErrorCodes.ShipIsCloaked, new PyInt(participantId));
            if (space.IsInvulnerable(session))
                Add(PvpFilamentJumpErrorCodes.ShipIsInvulnerable, new PyInt(participantId));
            if (await space.IsSelfDestructingAsync(participantShipId, cancellationToken).ConfigureAwait(false))
                Add(PvpFilamentJumpErrorCodes.ShipIsSelfDestructing, new PyInt(participantId));
            if (space.IsTethered(session))
                Add(PvpFilamentJumpErrorCodes.ShipIsTethered, new PyInt(participantId));
            if (space.IsWarping(session))
                Add(PvpFilamentJumpErrorCodes.ShipInWarp, new PyInt(participantId));
            if (await space.IsWarpScrambledAsync(session, cancellationToken).ConfigureAwait(false))
                Add(PvpFilamentJumpErrorCodes.ShipIsWarpScrambled, new PyInt(participantId));
            if (space.IsInAbyssalRun(session))
                Add(PvpFilamentJumpErrorCodes.InAbyssSystem, new PyInt(participantId));
            if (space.IsSessionChanging(session))
                Add(PvpFilamentJumpErrorCodes.SessionChangeInProgress, new PyInt(participantId));
            if ((await crimewatchRuntime.StateOfAsync(participantId, cancellationToken).ConfigureAwait(false)).PvpUntilMs > now())
                Add(PvpFilamentJumpErrorCodes.PvpTimerActive, new PyInt(participantId));
            return (participantShipId, participantShipTypeId, participantSolarSystemId, shipItem, live);
        }

        var itemId = Eve.Macho.JsSemantics.ToPositiveInteger(call.Arg(0));
        var requestedTypeId = Eve.Macho.JsSemantics.ToPositiveInteger(call.Arg(1));
        var key = itemId > 0 ? await items.GetAsync(itemId, cancellationToken).ConfigureAwait(false) : null;
        if (key is null)
        {
            Add(PvpFilamentJumpErrorCodes.KeyNotFound);
        }
        else
        {
            var keyType = dogma.Type(key.TypeId);
            if (key.TypeId != requestedTypeId || keyType?.GroupId != 4050)
                Add(PvpFilamentJumpErrorCodes.InvalidKeyType, new PyInt(characterId));
            if (key.OwnerId != characterId)
                Add(PvpFilamentJumpErrorCodes.InvalidOwner, new PyInt(characterId));
            if (!key.Singleton && key.Quantity < 1)
                Add(PvpFilamentJumpErrorCodes.InvalidKeyAmount);
            if (key.LocationId != ownerShipId || key.Flag != ItemFlags.Cargo)
                Add(PvpFilamentJumpErrorCodes.ItemNotReachable, new PyInt(key.TypeId));
        }

        var fleetId = await fleetStore.FleetIdForCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
        var fleet = fleetId is > 0 ? await fleetStore.SnapshotAsync(fleetId.Value, cancellationToken).ConfigureAwait(false) : null;
        if (fleet is null)
        {
            Add(PvpFilamentJumpErrorCodes.ShipNotInFleet);
            await AddActorStateErrorsAsync(call.Session, characterId).ConfigureAwait(false);
            return Result();
        }
        var ownerMember = fleet.Members.SingleOrDefault(member => member.CharacterId == characterId);
        if (ownerMember is null)
            Add(PvpFilamentJumpErrorCodes.ShipNotInFleet);
        else if (!FleetStore.HasBossJob(ownerMember.Job))
            Add(PvpFilamentJumpErrorCodes.FleetRolesNeeded);
        if (fleet.Members.Count < eventInfo.FleetSize)
            Add(PvpFilamentJumpErrorCodes.TooFewInFleet, new PyInt(eventInfo.FleetSize));
        if (fleet.Members.Count > eventInfo.FleetSize)
            Add(PvpFilamentJumpErrorCodes.TooManyInFleet, new PyInt(eventInfo.FleetSize));

        var typeListIds = new[]
        {
            eventInfo.ShipRestrictions,
            eventInfo.BannedModulesTypeListId,
            eventInfo.LimitedAmountOfModulesTypeListId,
        }.Where(id => id is > 0).Select(id => (long)id!.Value).Distinct().ToArray();
        var typeLists = (await sde.ReadAsync("typeLists", typeListIds, cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => checked((int)row.Key), row => row.Doc);
        var missingTypeLists = new HashSet<int>();
        bool TypeListNames(int listId, int typeId)
        {
            if (!typeLists.TryGetValue(listId, out var list) || dogma.Type(typeId) is not { } type)
            {
                if (missingTypeLists.Add(listId))
                    Add(PvpFilamentJumpErrorCodes.UnknownError);
                return false;
            }

            static bool Contains(System.Text.Json.JsonElement row, string property, int id) =>
                id > 0 && row.TryGetProperty(property, out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Array &&
                values.EnumerateArray().Any(value => value.TryGetInt32(out var candidate) && candidate == id);

            var included = Contains(list, "includedTypeIDs", typeId) || Contains(list, "includedGroupIDs", type.GroupId) ||
                Contains(list, "includedCategoryIDs", type.CategoryId);
            var excluded = Contains(list, "excludedTypeIDs", typeId) || Contains(list, "excludedGroupIDs", type.GroupId) ||
                Contains(list, "excludedCategoryIDs", type.CategoryId);
            return included && !excluded;
        }

        var participants = new List<PvpFilamentQueueParticipant>(fleet.Members.Count);
        var pilots = new List<(long CharacterId, ClientSession Session, long ShipId, int ShipTypeId, int GroupId)>(fleet.Members.Count);
        foreach (var member in fleet.Members.OrderBy(member => member.CharacterId))
        {
            var session = characterSessions.Of(member.CharacterId);
            if (session is null)
            {
                Add(PvpFilamentJumpErrorCodes.ShipNotFound);
                continue;
            }
            var actorState = await AddActorStateErrorsAsync(session, member.CharacterId).ConfigureAwait(false);
            if (!actorState.IsLive || actorState.ShipTypeId > int.MaxValue || actorState.ShipItem is null)
                continue;

            participants.Add(new PvpFilamentQueueParticipant(member.CharacterId, session));

            var memberShipType = actorState.ShipTypeId;
            var shipType = dogma.Type(memberShipType);
            if (shipType is null)
            {
                Add(PvpFilamentJumpErrorCodes.ShipNotFound, new PyInt(member.CharacterId));
                continue;
            }
            pilots.Add((member.CharacterId, session, actorState.ShipId, memberShipType, shipType.GroupId));
            if (!TypeListNames(eventInfo.ShipRestrictions, memberShipType))
                Add(PvpFilamentJumpErrorCodes.InvalidShipType, new PyInt(member.CharacterId));
        }

        if (fleet.Members.Count > 1)
        {
            var ownerPosition = await space.PositionOfAsync(call.Session, ownerShipId, cancellationToken).ConfigureAwait(false);
            foreach (var member in fleet.Members.Where(member => member.CharacterId != characterId))
            {
                var pilot = pilots.SingleOrDefault(pilot => pilot.CharacterId == member.CharacterId);
                if (pilot.Session is null)
                {
                    Add(PvpFilamentJumpErrorCodes.FleetMemberNotInRange,
                        new PyInt(member.CharacterId), new PyInt((long)PvpFilamentFleetRangeMeters));
                    continue;
                }
                var position = await space.PositionOfAsync(call.Session, pilot.ShipId, cancellationToken).ConfigureAwait(false);
                if (position is not { } memberPosition || ownerPosition is not { } ownerCoordinates ||
                    memberPosition.SolarSystemId != ownerCoordinates.SolarSystemId ||
                    space.SolarSystemOf(pilot.Session) != ownerSolarSystemId)
                {
                    Add(PvpFilamentJumpErrorCodes.FleetMemberNotInRange, new PyInt(pilot.CharacterId), new PyInt((long)PvpFilamentFleetRangeMeters));
                    continue;
                }
                var dx = memberPosition.X - ownerCoordinates.X;
                var dy = memberPosition.Y - ownerCoordinates.Y;
                var dz = memberPosition.Z - ownerCoordinates.Z;
                if (dx * dx + dy * dy + dz * dz > PvpFilamentFleetRangeMeters * PvpFilamentFleetRangeMeters)
                    Add(PvpFilamentJumpErrorCodes.FleetMemberNotInRange, new PyInt(pilot.CharacterId), new PyInt((long)PvpFilamentFleetRangeMeters));
            }
        }

        void CheckFleetLimit(int limit, int code, Func<(long CharacterId, ClientSession Session, long ShipId, int ShipTypeId, int GroupId), int> selectId)
        {
            if (limit <= 0) return;
            var repeated = pilots.GroupBy(selectId).FirstOrDefault(group => group.Count() > limit);
            if (repeated is not null)
                Add(code, new PyInt(limit), new PyInt(repeated.Key));
        }

        CheckFleetLimit(eventInfo.MaxOfShipTypeRestriction, PvpFilamentJumpErrorCodes.MaxOfShipTypeRestrictions,
            pilot => pilot.ShipTypeId);
        CheckFleetLimit(eventInfo.MaxOfShipGroupRestriction, PvpFilamentJumpErrorCodes.MaxOfShipGroupRestrictions,
            pilot => pilot.GroupId);

        var metaLevelId = dogma.AttributeId("metaLevel");
        var oldMetaLevelId = dogma.AttributeId("metaLevelOld");
        foreach (var pilot in pilots)
        {
            var fitted = (await items.InLocationAsync(pilot.ShipId, pilot.CharacterId, cancellationToken).ConfigureAwait(false))
                .Where(item => Slots.IsSlotFlag(item.Flag)).ToArray();
            var implants = (await items.InLocationAsync(pilot.CharacterId, pilot.CharacterId, cancellationToken).ConfigureAwait(false))
                .Where(item => item.Flag == PilotAugmentations.ImplantFlag && PilotAugmentations.IsImplantType(dogma.Type(item.TypeId)))
                .ToArray();

            if (eventInfo.BannedModulesTypeListId is { } bannedList)
            {
                foreach (var module in fitted)
                    if (TypeListNames(bannedList, module.TypeId))
                    {
                        Add(PvpFilamentJumpErrorCodes.BannedModulesRestrictions, new PyInt(module.TypeId), new PyInt(pilot.CharacterId));
                        break;
                    }
            }

            if (eventInfo.LimitedAmountOfModulesTypeListId is { } limitedList)
            {
                var limitedModules = new List<Item>();
                foreach (var module in fitted)
                    if (TypeListNames(limitedList, module.TypeId))
                        limitedModules.Add(module);
                if (limitedModules.Count > eventInfo.NumberOfLimitedModules)
                    Add(PvpFilamentJumpErrorCodes.LimitedNumberOfModulesRestriction,
                        new PyInt(limitedModules[0].TypeId), new PyInt(pilot.CharacterId), new PyInt(eventInfo.NumberOfLimitedModules));
            }

            if (eventInfo.ModuleAndImplantMetaLevelRestrictions > 0)
            {
                var hasRestrictedMetaLevel = false;
                foreach (var item in fitted.Concat(implants))
                {
                    var type = dogma.Type(item.TypeId);
                    var metaLevel = type is null
                        ? double.PositiveInfinity
                        : metaLevelId is { } metaId && type.Attributes.TryGetValue(metaId, out var currentMeta)
                            ? currentMeta
                            : oldMetaLevelId is { } oldMetaId ? type.Attributes.GetValueOrDefault(oldMetaId) : 0;
                    if (metaLevel > eventInfo.ModuleAndImplantMetaLevelRestrictions)
                    {
                        hasRestrictedMetaLevel = true;
                        break;
                    }
                }
                if (hasRestrictedMetaLevel)
                    Add(PvpFilamentJumpErrorCodes.ModuleAndImplantRestrictions);
            }
        }

        return errors.Count != 0
            ? Result()
            : new PvpFilamentAdmissionResult(participants.AsReadOnly(), []);
    }
}
