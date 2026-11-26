using Eve.Inventory;
using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;
using Eve.Services.Ships;
using Eve.Structures;
using Eve.Ships;
using System.Text.Json;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    private const string MobileDepotKind = "mobile_depot";
    private const int MobileDepotHoldFlag = 176;

    /// <summary><c>ship.Scoop(itemID, password)</c> takes the target into cargo.</summary>
    public Task ScoopAsync(ClientSession session, long itemId, string? password, CancellationToken cancellationToken) =>
        ScoopToHoldAsync(session, itemId, CargoFlag, password, cancellationToken);

    /// <summary>
    /// Takes a scoopable item, its contents, and the space/deployed records into the selected ship hold. Every inventory mutation is one PostgreSQL transaction.
    /// </summary>
    public async Task ScoopToHoldAsync(ClientSession session, long itemId, int destinationFlag, string? password, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId)
            return;
        // A pilot who is not in space is told the scoop failed, not left with a silent success.
        if (!_space.IsInSpace(session) || _space.SolarSystemOf(session) is not { } systemId)
            throw UnableToScoop();
        if (itemId <= 0 || !CharacterShips.IsShipHoldFlag(destinationFlag))
            throw UnableToScoop();

        var item = await _items.GetAsync(itemId, cancellationToken);
        if (item is null)
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = _statics.Type(item.TypeId)
            ?? throw new InvalidOperationException($"SDE type {item.TypeId} for scoop target {itemId} is missing.");
        var spaceItem = await _spaceItems.GetAsync(itemId, cancellationToken);
        if (spaceItem is null || spaceItem.SolarSystemId != systemId || item.LocationId != systemId || item.Flag != CharacterShips.InSpaceFlag)
            throw MachoErrors.ThrowUserError("FakeItemNotFound");

        var mobileDepot = _statics.GroupName(type.GroupId).Equals("Mobile Depot", StringComparison.OrdinalIgnoreCase);
        if (mobileDepot)
        {
            await ScoopMobileDepotAsync(session, item, spaceItem, destinationFlag, systemId, cancellationToken);
            return;
        }

        var isFittings = StructureUnanchorDrop.IsFittingsContainer(item.TypeId, item.CustomInfo);
        var isHull = StructureUnanchorDrop.IsHullArtifact(item.CustomInfo) && _statics.CategoryName(type.CategoryId) is "Structure" or "Starbase";
        var isStarbaseStructure = type.CategoryId == StarbaseCategoryId && type.GroupId != ControlTowerGroup
            && type.EffectIds.Contains(AnchorDropForStructuresEffectId);
        var isCargoContainer = IsScoopableCargoContainer(type);
        var isCorpse = _statics.GroupName(type.GroupId).Equals("Biomass", StringComparison.OrdinalIgnoreCase);
        if (!isFittings && !isHull && !isStarbaseStructure && !isCargoContainer && !isCorpse)
            throw UnableToScoop();

        if (isFittings || isHull || isStarbaseStructure)
        {
            var starbaseHull = !isFittings && _statics.CategoryName(type.CategoryId) == "Starbase";
            var pilot = await _ships.CharacterAsync(characterId, cancellationToken);
            if (pilot is null || (starbaseHull && item.OwnerId != characterId &&
                (item.OwnerId != pilot.CorporationId || spaceItem.CorporationId != pilot.CorporationId)))
                throw MachoErrors.ThrowUserError("FakeItemNotFound");
            if (isFittings && !await MayTakeFromAsync(characterId, item, cancellationToken, spaceItem))
                throw MachoErrors.ThrowUserError("SafetyActivated");
            await MeasureForScoopAsync(session, itemId, item.TypeId, cancellationToken);
            var artifact = await _ships.ScoopSpaceArtifactAsync(
                characterId, pilot.CorporationId, isFittings, shipId, systemId, itemId, item.CustomInfo!, _spaceItems,
                cancellationToken, destinationFlag);
            await CompleteScoopAsync(session, characterId, artifact, itemId, removeWreck: true, preserveArtifactNotifications: true);
            return;
        }

        if (IsSecureContainer(type))
        {
            var secureState = SecureContainerStateOf(item.CustomInfo);
            if (secureState.Anchored && secureState.Password.Length > 0 &&
                !StringComparer.Ordinal.Equals(secureState.Password, password ?? string.Empty))
                throw MachoErrors.ThrowUserError("ShpScoopSecureCC");
        }

        await MeasureForScoopAsync(session, itemId, item.TypeId, cancellationToken);
        // The client opens scoopable cans and wrecks at the same 2,500 m range. The common opener also proves the item is in the active scene.
        if (await OpenAsync(session, itemId, cancellationToken) is null)
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        if (isCargoContainer && !await MayTakeFromAsync(characterId, item, cancellationToken, spaceItem))
            throw MachoErrors.ThrowUserError("SafetyActivated");

        var result = await _ships.ScoopSpaceItemToShipHoldAsync(
            characterId, shipId, systemId, itemId, destinationFlag, _spaceItems,
            removeFromScene: _ =>
            {
                _sim.RemoveWreck(itemId, 0);
                return Task.FromResult(true);
            }, cancellationToken: cancellationToken);
        await CompleteScoopAsync(session, characterId, result, itemId);
    }

    private async Task ScoopMobileDepotAsync(ClientSession session, Item item, SpaceItem spaceItem, int destinationFlag,
        int systemId, CancellationToken cancellationToken)
    {
        // The retail checker can offer this deployable to an infrastructure hold as well as the
        // dedicated Mobile Depot hold when the active hull supports that hold.
        if (destinationFlag is not CharacterShips.CargoFlag and not MobileDepotHoldFlag and not 185)
            throw UnableToScoop();
        var characterId = session.CharacterId!.Value;
        var shipId = session.GetInt("shipid")!.Value;
        var now = _nowUnixMs();
        var record = _deployed is null ? null : await _deployed.GetAsync(item.Id, cancellationToken);
        if (record is null || record.Kind != MobileDepotKind || record.SolarSystemId != systemId || record.ExpiresAtMs <= now)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Unable to scoop this item.")));
        if (record.ActivateAtMs > now)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Mobile Depot is still activating.")));
        if (record.OwnerId != characterId || item.OwnerId != characterId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Only the owner can scoop this Mobile Depot.")));

        var target = await _ships.LoadAsync(characterId, cancellationToken);
        if (target is null || target.Ship.Id != shipId)
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        var mobileDepotCapacityAttribute = _statics.AttributeId("specialMobileDepotHoldCapacity");
        if (destinationFlag == MobileDepotHoldFlag && (mobileDepotCapacityAttribute is not { } depotCapacity
            || target.World.Get(shipId, depotCapacity) <= 0))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("This ship does not have a Mobile Depot Hold.")));
        var cargoCapacityAttribute = _statics.AttributeId("capacity");
        if (destinationFlag == CharacterShips.CargoFlag && (cargoCapacityAttribute is not { } cargoCapacity
            || target.World.Get(shipId, cargoCapacity) <= 0))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("This ship does not have a Cargo Hold.")));

        await _statics.EnsureTypesAsync([CharacterShips.CargoContainerTypeId], cancellationToken);
        var cargoContainerType = _statics.Type(CharacterShips.CargoContainerTypeId)
            ?? throw new InvalidOperationException("The SDE Cargo Container type is missing.");
        var radiusAttribute = _statics.AttributeId("radius");
        var ejectedContainer = new ScoopEjectedContainer(
            cargoContainerType.TypeId,
            cargoContainerType.GroupId,
            radiusAttribute is { } radiusId ? cargoContainerType.Attributes.GetValueOrDefault(radiusId, 1) : 1,
            now,
            now + (long)CanLifetime.TotalMilliseconds,
            record.OwnerId,
            record.CorporationId,
            record.AllianceId,
            spaceItem.WarFactionId);

        await MeasureForScoopAsync(session, item.Id, item.TypeId, cancellationToken);
        var result = await _ships.ScoopSpaceItemToShipHoldAsync(
            characterId, shipId, systemId, item.Id, destinationFlag, _spaceItems, _deployed, MobileDepotKind,
            async token => await _sim.RemoveDeployedAsync(item.Id, token), cancellationToken,
            ejectedContainer,
            (container, containerSpace, _) =>
            {
                if (_placed.TryAdd(container.Id, 0))
                {
                    _sim.PlaceWreck(new SimPlaceWreck(
                        SimSceneKey.SolarSystem(containerSpace.SolarSystemId), container.Id, containerSpace.GroupId,
                        new SimWreckKind(container.TypeId, cargoContainerType.Name ?? "Cargo Container", cargoContainerType.GraphicId),
                        containerSpace.Position, containerSpace.Direction, containerSpace.Radius, containerSpace.LauncherId,
                        new SimCredit(containerSpace.CharacterId, containerSpace.CorporationId, containerSpace.AllianceId, containerSpace.WarFactionId),
                        IsEmpty: false));
                }
                return Task.FromResult(true);
            },
            (containerId, token) =>
            {
                _placed.TryRemove(containerId, out _);
                _sim.RemoveWreck(containerId, 0);
                return Task.CompletedTask;
            },
            _ =>
            {
                _sim.RestoreDeployed(new SimDeployed(
                    record.SolarSystemId, record.DeployableId, record.Name, record.TypeId, record.GroupId, record.Position,
                    record.Radius, record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId,
                    Math.Max(1, record.ExpiresAtMs - _nowUnixMs())));
                return Task.CompletedTask;
            });
        // The depot hold is checked against its own capacity; the refusal names that hold, not the cargo hold.
        if (result is ShipRefused { Reason: ShipRefusal.NotEnoughCargoSpace } && destinationFlag == MobileDepotHoldFlag)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("There is not enough room in the Mobile Depot Hold.")));
        await CompleteScoopAsync(session, characterId, result, item.Id, itemsChanged: true);
    }

    private async Task MeasureForScoopAsync(ClientSession session, long itemId, int typeId, CancellationToken cancellationToken)
    {
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [itemId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"measuring scoop target {itemId} for session {session.SessionId}: {error.Message}");
            throw Unavailable();
        }
        if (!reply.Accepted || reply.Distance is not { } distance)
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        if (distance > OpenRange)
            throw MachoErrors.ThrowUserError("CantUseTooDistantWithType",
                ("desired", new PyInt((long)OpenRange)),
                ("actual", new PyInt((long)Math.Floor(distance + 0.5))),
                ("typeID", new PyInt(typeId)));
    }

    private async Task CompleteScoopAsync(ClientSession session, long characterId, ShipOutcome outcome, long itemId,
        bool removeWreck = false, bool itemsChanged = false, bool preserveArtifactNotifications = false)
    {
        switch (outcome)
        {
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            case ShipChanged changed:
                if (removeWreck)
                    _sim.RemoveWreck(itemId, 0);
                _placed.TryRemove(itemId, out _);
                if (preserveArtifactNotifications)
                    ShipNotifications.Send(session, characterId, changed, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
                else
                    ShipNotifications.SendScoop(session, changed, RowOf, itemsChanged);
                return;
            default:
                throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }
    }

    private static bool IsSecureContainer(TypeDef type) => type.GroupId is 340 or 448;

    private static (bool Anchored, string Password) SecureContainerStateOf(string? customInfo)
    {
        if (string.IsNullOrWhiteSpace(customInfo))
            return (false, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(customInfo);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (false, string.Empty);
            var root = document.RootElement;
            var stored = Property(root, "secureContainerState");
            var secure = Property(root, "secureContainer");
            var cargo = Property(root, "cargoContainer");
            var state = Property(root, "state");
            var anchored = Boolean(Property(stored, "anchored")) ??
                IsAnchorState(Property(stored, "anchorState")) ??
                Boolean(Property(root, "anchored")) ?? Boolean(Property(root, "isAnchored")) ??
                Boolean(Property(state, "anchored")) ?? Boolean(Property(secure, "anchored")) ??
                Boolean(Property(cargo, "anchored")) ?? IsAnchorState(Property(root, "anchorState")) ??
                IsAnchorState(Property(secure, "anchorState")) ?? IsAnchorState(Property(cargo, "anchorState")) ?? false;
            var password = Text(Property(stored, "generalPassword")) ??
                Text(Property(root, "secureContainerPassword")) ?? Text(Property(root, "containerPassword")) ??
                Text(Property(root, "scoopPassword")) ?? Text(Property(root, "generalPassword")) ??
                Text(Property(root, "password")) ?? Text(Property(secure, "password")) ??
                Text(Property(secure, "generalPassword")) ?? Text(Property(cargo, "password")) ??
                Text(Property(cargo, "generalPassword")) ?? Text(Property(state, "password")) ??
                Text(Property(state, "generalPassword")) ?? string.Empty;
            return (anchored, password.Length <= 50 ? password : password[..50]);
        }
        catch (JsonException)
        {
            return (false, string.Empty);
        }
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (StringComparer.OrdinalIgnoreCase.Equals(property.Name, name))
                    return property.Value;
        return default;
    }

    private static bool? Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
        JsonValueKind.String when value.GetString() is { } text => text is "1" or "true" or "True",
        _ => null,
    };

    private static bool? IsAnchorState(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()?.ToLowerInvariant() switch
        {
            "anchored" or "anchoring" or "unanchoring" => true,
            "unanchored" => false,
            _ => null,
        }
        : null;

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private bool IsScoopableCargoContainer(TypeDef type) => type.GroupId is CanGroup or WreckGroup or 340 or 448 or 649
        || _statics.GroupName(type.GroupId).Equals("Freight Container", StringComparison.OrdinalIgnoreCase);

    private static MachoWrappedException UnableToScoop() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Unable to scoop this item.")));
}
