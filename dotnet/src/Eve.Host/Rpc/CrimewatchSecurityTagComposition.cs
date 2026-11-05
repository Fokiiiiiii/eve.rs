using Eve.Crimewatch;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Relations;
using Eve.Services;
using Eve.Services.Crimewatch;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Wallet;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private static readonly IReadOnlyDictionary<int, string> SecurityTagNames = new Dictionary<int, string>
    {
        [33138] = "Clone Soldier Trainer Tag",
        [33139] = "Clone Soldier Recruiter Tag",
        [33140] = "Clone Soldier Transporter Tag",
        [33141] = "Clone Soldier Negotiator Tag",
    };

    private async ValueTask ApplySecurityTagsAsync(
        ServiceCall call, double requestedStatus, double clientStatus, CancellationToken cancellationToken)
    {
        if (call.Session.CharacterId is not { } characterId ||
            call.Session.GetInt("stationid") is not { } stationId || stationId <= 0)
            throw SecurityTagError("Security tags can only be exchanged by a docked character.");
        if (!await IsSecurityOfficeStationAsync(stationId, cancellationToken))
            throw SecurityTagError("The Security Office is available at CONCORD and DED stations in low security space.");
        if (requestedStatus == clientStatus)
            return;

        var requirements = SecurityTagRequirements.Calculate(clientStatus, requestedStatus);
        var typeIds = requirements.Select(entry => entry.TypeId).Distinct().ToArray();
        await dogma.EnsureTypesAsync(typeIds, cancellationToken);
        var types = typeIds.ToDictionary(typeId => typeId, typeId =>
        {
            var type = dogma.Type(typeId)
                ?? throw new InvalidOperationException($"Security tag type {typeId} is missing from the pinned SDE.");
            if (!SecurityTagNames.TryGetValue(typeId, out var expectedName) ||
                !string.Equals(type.Name, expectedName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Security tag type {typeId} does not match the pinned SDE name.");
            return type;
        });
        var feeAttributeId = dogma.AttributeId("securityProcessingFee")
            ?? throw new InvalidOperationException("The SDE securityProcessingFee dogma attribute is missing.");
        var fees = requirements.Sum(requirement =>
        {
            var type = types[requirement.TypeId];
            var amount = type.Attributes.GetValueOrDefault(feeAttributeId, dogma.Attribute(feeAttributeId)?.DefaultValue ?? 0d);
            if (!double.IsFinite(amount) || amount < 0)
                throw new InvalidOperationException("The SDE security tag processing fee is invalid.");
            return checked((decimal)amount * requirement.Quantity);
        });
        if (decimal.Round(fees, 2) != fees)
            throw new InvalidOperationException("The SDE security tag processing fee is not an ISK amount.");

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var characterLock = new Npgsql.NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('crimewatch:security-tags:' || $1::text, 0))",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        })
        {
            await characterLock.ExecuteScalarAsync(cancellationToken);
        }
        var statusRelation = await foundations.Relations.GetAsync(
            "security_status", characterId, RelationStore.World, connection, transaction, cancellationToken, forUpdate: true);
        var currentStatus = statusRelation?.Value ?? 0d;
        if (Math.Abs(currentStatus - clientStatus) > 0.000001)
            throw SecurityTagError("Your security status changed. Refresh the Security Office and try again.");

        var authoritativeRequirements = SecurityTagRequirements.Calculate(currentStatus, requestedStatus);
        var requiredByType = authoritativeRequirements.ToDictionary(entry => entry.TypeId, entry => entry.Quantity);
        var available = await items.LockInLocationAsync(
            connection, transaction, stationId, characterId, ItemFlags.Hangar, typeIds, cancellationToken);
        foreach (var (typeId, quantity) in requiredByType)
        {
            var owned = available.Where(item => item.TypeId == typeId).Aggregate(0L, (sum, item) => checked(sum + item.Quantity));
            if (owned < quantity)
                throw SecurityTagError("The required security tags are no longer in your station hangar.");
        }

        var itemChanges = new List<ItemChanged>();
        foreach (var (typeId, quantity) in requiredByType)
        {
            var remaining = (long)quantity;
            foreach (var item in available.Where(item => item.TypeId == typeId).OrderBy(item => item.Id))
            {
                if (remaining == 0)
                    break;
                var consumed = Math.Min(item.Quantity, remaining);
                var newQuantity = item.Quantity - consumed;
                Eve.Inventory.Item after;
                if (newQuantity == 0)
                {
                    await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                    after = item with { LocationId = CharacterShips.JunkLocation, Quantity = 0 };
                }
                else
                {
                    await items.SetQuantityAsync(connection, transaction, item.Id, newQuantity, cancellationToken);
                    after = item with { Quantity = newQuantity };
                }
                itemChanges.Add(new ItemChanged(after, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity));
                remaining -= consumed;
            }
        }

        var statusAfterTags = currentStatus + authoritativeRequirements.Sum(entry => entry.Quantity) * SecurityTagRequirements.GainPerTag;
        var nowUnixMs = now();
        var feeBalance = fees > 0
            ? await economyWallets.AdjustAsync(connection, transaction, characterId, -fees, 0, 117,
                "Security tag processing fee", nowUnixMs,
                $"rpc:{call.Session.SessionId}:{call.CallId}:{characterId}:ApplyTagsForSecStatus", cancellationToken)
            : (decimal?)null;
        await foundations.Relations.SetAsync(
            connection, transaction, "security_status", characterId, RelationStore.World, statusAfterTags,
            "security_tag_processing",
            new
            {
                eventTypeID = 385,
                locationID = stationId,
                modification = statusAfterTags - currentStatus,
                newValue = statusAfterTags,
            },
            untilMs: null,
            cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var itemChange in itemChanges)
            ShipNotifications.SendItemChange(call.Session, itemChange, changed =>
            {
                var type = types[changed.TypeId];
                return new ItemRow(changed.Id, changed.TypeId, changed.OwnerId, changed.LocationId, changed.Flag,
                    changed.Singleton ? -1 : changed.Quantity, type.GroupId, type.CategoryId);
            });
        if (feeBalance is { } balance)
            economyWallets.AnnounceCommitted(new WalletBalanceChange(characterId, 1000, balance));

        var when = new PyLong(Py.FileTimeFromUnixMs(nowUnixMs));
        if (call.Session.GetInt("shipid") is { } shipId and > 0)
        {
            call.Session.SendNotification("OnModuleAttributeChanges", "clientID", new PyList([Py.Tuple(
                new PyStr("OnModuleAttributeChange"), InventoryWire.Id(characterId), InventoryWire.Id(shipId),
                new PyInt(SecurityStatusPenalties.PilotSecurityStatus), when,
                Py.Number(statusAfterTags), Py.Number(currentStatus), when)]));
        }
        call.Session.SendNotification("OnSecurityStatusUpdate", "charid", Py.Number(statusAfterTags));
    }

    private async Task<bool> IsSecurityOfficeStationAsync(long stationId, CancellationToken cancellationToken)
    {
        var stationRows = await sde.ReadAsync("staStations", [stationId], cancellationToken);
        if (stationRows.Count != 1)
            return false;
        var station = stationRows[0].Doc;
        var ownerId = station.GetProperty("corporationID").GetInt64();
        if (ownerId is not (1000125 or 1000137))
            return false;
        var systemId = station.GetProperty("solarSystemID").GetInt64();
        var systems = await sde.ReadAsync("mapSolarSystems", [systemId], cancellationToken);
        if (systems.Count != 1)
            return false;
        var security = systems[0].Doc.GetProperty("security").GetDouble();
        return security is > 0 and < 0.5;
    }

    private static Exception SecurityTagError(string message) => MachoErrors.ThrowUserError(
        "CustomNotify", ("notify", new PyStr(message)));
}
