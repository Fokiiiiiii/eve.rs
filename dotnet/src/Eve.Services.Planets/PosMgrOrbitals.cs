using System.Text.Json;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Planets;

public sealed partial class PlanetMgrService
{
    private const string OrbitalTransitionJobKind = "planet.orbital.transition";
    private const int OrbitalCategoryId = 46;
    private const int OrbitalConstructionPlatformGroupId = 1106;
    private const int CustomsOfficeGantryTypeId = 3962;
    private const int CustomsOfficeTypeId = 2233;
    private const int OrbitalAnchoring = -6;
    private const int OrbitalOnlining = -5;
    private const int OrbitalAnchored = -4;
    private const int OrbitalUnanchoring = -3;
    private const int OrbitalUnanchored = -2;
    private const int OrbitalIdle = 0;
    private const long Junkyard = 6;

    private sealed record OrbitalTransitionDue(long ItemId, int State, long EndsAtMs);
    private sealed record OrbitalUpgradeMaterial(int TypeId, long Quantity);

    private static readonly string[] OrbitalTransitionAttributes = ["anchoringDelay", "onliningDelay", "unanchoringDelay"];

    private void RegisterOrbitalJobs(DurableScheduler scheduler) =>
        scheduler.Register(OrbitalTransitionJobKind, CompleteOrbitalTransitionAsync);

    /// <summary>Applies the V24 posMgr orbital state calls to the durable inventory item and scheduled transition.</summary>
    public async ValueTask<PyValue> PosMgrOrbitalOperationAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var itemId = Integral(call.Arg(0)) ?? 0;
        var characterId = call.Session.CharacterId ?? 0;
        var corporationId = call.Session.GetInt("corpid") ?? call.Session.GetInt("corporationid") ?? 0;
        var systemId = checked((int)(call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0));
        if (itemId <= 0 || characterId <= 0 || corporationId <= 0 || systemId <= 0 || dogma is null || _scheduler is null)
            return PyNone.Instance;

        var committed = false;
        OrbitalStateRecord? next = null;
        var consumedMaterials = new List<ItemChanged>();
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false))
        {
            var item = await items.LockAndGetAsync(connection, transaction, itemId, call.Cancellation).ConfigureAwait(false);
            if (item is null || item.OwnerId != corporationId || item.LocationId != systemId || item.Flag != ItemFlags.InSpace)
            {
                await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
                return PyNone.Instance;
            }

            await dogma.EnsureTypesAsync([item.TypeId], call.Cancellation).ConfigureAwait(false);
            var type = dogma.Type(item.TypeId);
            if (type is not { CategoryId: OrbitalCategoryId })
            {
                await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
                return PyNone.Instance;
            }

            var previous = await store.LockOrbitalStateAsync(connection, transaction, itemId, call.Cancellation).ConfigureAwait(false)
                ?? new OrbitalStateRecord(itemId, systemId,
                    type.GroupId == OrbitalConstructionPlatformGroupId ? OrbitalUnanchored : OrbitalIdle, null, null);
            if (previous.SolarSystemId != systemId)
            {
                await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
                return PyNone.Instance;
            }

            var nextState = call.Method switch
            {
                "AnchorOrbital" when previous.State == OrbitalUnanchored => OrbitalAnchoring,
                "OnlineOrbital" when previous.State is OrbitalAnchored or OrbitalUnanchored => OrbitalOnlining,
                "UnanchorOrbital" when previous.State is not (OrbitalUnanchored or OrbitalUnanchoring) => OrbitalUnanchoring,
                _ => 0,
            };
            if (nextState == 0)
            {
                await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
                return PyNone.Instance;
            }

            var attributeName = nextState switch
            {
                OrbitalAnchoring => OrbitalTransitionAttributes[0],
                OrbitalOnlining => OrbitalTransitionAttributes[1],
                _ => OrbitalTransitionAttributes[2],
            };
            var delay = TypeAttributeValue(type, attributeName);
            if (delay is null && nextState == OrbitalUnanchoring)
                delay = TypeAttributeValue(type, "anchoringDelay");
            if (delay is not { } transitionDelay || !double.IsFinite(transitionDelay) || transitionDelay < 0)
                throw OrbitalConfigurationUnavailable($"The pinned SDE has no valid {attributeName} for orbital type {type.TypeId}.");
            var endsAtMs = checked(nowMs() + Math.Max(1_000, (long)Math.Ceiling(transitionDelay)));
            next = new OrbitalStateRecord(itemId, systemId, nextState, nowMs(), endsAtMs);

            if (nextState == OrbitalOnlining && type.GroupId == OrbitalConstructionPlatformGroupId)
            {
                var requirements = await ReadOrbitalUpgradeMaterialsAsync(connection, transaction, type, call.Cancellation).ConfigureAwait(false);
                var materials = (await items.LockContainerAsync(connection, transaction, itemId, call.Cancellation).ConfigureAwait(false))
                    .Where(material => material.Flag == ItemFlags.SpecializedMaterialBay)
                    .OrderBy(material => material.Id)
                    .ToArray();
                var requiredByType = requirements.ToDictionary(requirement => requirement.TypeId, requirement => requirement.Quantity);
                var availableByType = new Dictionary<int, long>();
                foreach (var material in materials)
                {
                    if (!requiredByType.ContainsKey(material.TypeId)) continue;
                    var quantity = material.Singleton ? 1 : material.Quantity;
                    if (quantity <= 0) continue;
                    availableByType[material.TypeId] = checked(availableByType.GetValueOrDefault(material.TypeId) + quantity);
                }
                if (requirements.Any(requirement => availableByType.GetValueOrDefault(requirement.TypeId) < requirement.Quantity))
                    throw OrbitalUpgradeMaterialsMissing();

                var remainingByType = requiredByType.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var material in materials)
                {
                    if (!remainingByType.TryGetValue(material.TypeId, out var remaining) || remaining == 0) continue;
                    var available = material.Singleton ? 1 : material.Quantity;
                    if (available <= 0) continue;
                    var consumed = Math.Min(available, remaining);
                    if (material.Singleton && consumed != 1)
                        throw OrbitalUpgradeMaterialsMissing();

                    if (consumed == available)
                    {
                        await items.DeleteAsync(connection, transaction, material.Id, call.Cancellation).ConfigureAwait(false);
                        consumedMaterials.Add(new ItemChanged(material with { LocationId = Junkyard },
                            material.LocationId, material.Flag, material.Quantity));
                    }
                    else
                    {
                        var after = material with { Quantity = material.Quantity - consumed };
                        await items.SetQuantityAsync(connection, transaction, material.Id, after.Quantity, call.Cancellation).ConfigureAwait(false);
                        consumedMaterials.Add(new ItemChanged(after, material.LocationId, material.Flag, material.Quantity));
                    }
                    remainingByType[material.TypeId] = remaining - consumed;
                }
                if (remainingByType.Values.Any(quantity => quantity != 0))
                    throw OrbitalUpgradeMaterialsMissing();
            }

            await store.SaveOrbitalStateAsync(connection, transaction, next, call.Cancellation).ConfigureAwait(false);

            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(OrbitalTransitionJobKind, endsAtMs)
            {
                Key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Payload = new OrbitalTransitionDue(itemId, nextState, endsAtMs),
            }, call.Cancellation).ConfigureAwait(false);
            await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
            committed = true;
        }

        if (committed && next is not null)
        {
            _scheduler.Wake();
            PresentOrbitalState(next);
            await NotifyConsumedMaterialsAsync(call.Session, consumedMaterials, call.Cancellation).ConfigureAwait(false);
        }
        return PyNone.Instance;
    }

    private async Task<IReadOnlyList<OrbitalUpgradeMaterial>> ReadOrbitalUpgradeMaterialsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TypeDef platformType, CancellationToken cancellationToken)
    {
        if (dogma?.AttributeId("constructionType") is not { } constructionTypeAttribute ||
            !platformType.Attributes.TryGetValue(constructionTypeAttribute, out var constructionTypeValue) ||
            !double.IsFinite(constructionTypeValue) || constructionTypeValue <= 0 || constructionTypeValue != Math.Truncate(constructionTypeValue))
            throw OrbitalConfigurationUnavailable($"The pinned SDE has no valid constructionType for orbital type {platformType.TypeId}.");

        var constructionTypeId = checked((int)constructionTypeValue);
        await using var command = new NpgsqlCommand(
            "SELECT doc::text FROM sde.\"typeMaterials\" WHERE key = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = constructionTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (raw is null)
            throw OrbitalConfigurationUnavailable($"The pinned SDE has no construction materials for orbital type {constructionTypeId}.");

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
            throw OrbitalConfigurationUnavailable($"The pinned SDE construction materials for orbital type {constructionTypeId} are invalid.");
        var quantities = new SortedDictionary<int, long>();
        foreach (var material in materials.EnumerateArray())
        {
            var typeId = material.GetProperty("materialTypeID").GetInt32();
            var quantity = material.GetProperty("quantity").GetInt64();
            if (typeId <= 0 || quantity <= 0)
                throw OrbitalConfigurationUnavailable($"The pinned SDE construction materials for orbital type {constructionTypeId} are invalid.");
            quantities[typeId] = checked(quantities.GetValueOrDefault(typeId) + quantity);
        }
        if (quantities.Count == 0)
            throw OrbitalConfigurationUnavailable($"The pinned SDE has no construction materials for orbital type {constructionTypeId}.");
        await dogma!.EnsureTypesAsync([.. quantities.Keys], cancellationToken).ConfigureAwait(false);
        return [.. quantities.Select(pair => new OrbitalUpgradeMaterial(pair.Key, pair.Value))];
    }

    private double? TypeAttributeValue(TypeDef type, string name)
    {
        if (dogma?.AttributeId(name) is not { } attributeId || dogma.Attribute(attributeId) is not { } attribute)
            return null;
        return type.Attributes.TryGetValue(attributeId, out var value) ? value : attribute.DefaultValue;
    }

    private async Task NotifyConsumedMaterialsAsync(ClientSession session, IReadOnlyList<ItemChanged> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0 || dogma is null) return;
        await dogma.EnsureTypesAsync(changes.Select(change => change.After.TypeId).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (var change in changes)
        {
            var type = dogma.Type(change.After.TypeId);
            ShipNotifications.SendItemChange(session, change, item => new ItemRow(
                item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0));
        }
    }

    private async Task CompleteOrbitalTransitionAsync(JobContext context, CancellationToken cancellationToken)
    {
        var due = context.Payload<OrbitalTransitionDue>();
        if (due is null) return;
        var current = await store.LockOrbitalStateAsync(context.Connection, context.Transaction, due.ItemId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.State != due.State || current.EndsAtMs != due.EndsAtMs || context.NowMs < due.EndsAtMs)
            return;

        var stableState = due.State switch
        {
            OrbitalAnchoring => OrbitalAnchored,
            OrbitalOnlining => OrbitalIdle,
            OrbitalUnanchoring => OrbitalUnanchored,
            _ => current.State,
        };
        var item = await items.LockAndGetAsync(context.Connection, context.Transaction, due.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is null) return;
        var upgraded = due.State == OrbitalOnlining && item.TypeId == CustomsOfficeGantryTypeId;
        if (upgraded)
            await items.SetTypeAsync(context.Connection, context.Transaction, item.Id, CustomsOfficeTypeId, cancellationToken).ConfigureAwait(false);

        var completed = new OrbitalStateRecord(due.ItemId, current.SolarSystemId, stableState, null, null);
        await store.SaveOrbitalStateAsync(context.Connection, context.Transaction, completed, cancellationToken).ConfigureAwait(false);
        context.AfterCommit(_ =>
        {
            if (upgraded) PresentOrbitalType(item.Id, CustomsOfficeTypeId);
            PresentOrbitalState(completed);
            return Task.CompletedTask;
        });
    }

    private void PresentOrbitalType(long itemId, int typeId)
    {
        if (simulation is null) return;
        var type = dogma?.Type(typeId);
        if (type is null) return;
        try
        {
            simulation.SetSlimField(itemId, "typeID", new SimSlimInt(typeId));
            simulation.SetSlimField(itemId, "groupID", new SimSlimInt(type.GroupId));
        }
        catch (SimUnavailableException error)
        {
            Console.Error.WriteLine($"eve-host: orbital {itemId} type committed but its slim refresh could not be sent: {error.Message}");
        }
    }

    private void PresentOrbitalState(OrbitalStateRecord state)
    {
        if (simulation is null) return;
        try
        {
            simulation.SetSlimField(state.ItemId, "orbitalState", new SimSlimInt(state.State));
            simulation.SetSlimField(state.ItemId, "orbitalTimestamp", state.EndsAtMs is { } endsAt
                ? new SimSlimInt(Py.FileTimeFromUnixMs(endsAt))
                : new SimSlimNone());
        }
        catch (SimUnavailableException error)
        {
            Console.Error.WriteLine($"eve-host: orbital {state.ItemId} committed but its slim refresh could not be sent: {error.Message}");
        }
    }

    private static MachoWrappedException OrbitalUpgradeMaterialsMissing() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("The orbital upgrade hold does not contain the required construction materials.")));

    private static MachoWrappedException OrbitalConfigurationUnavailable(string reason) => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr(reason)));
}
