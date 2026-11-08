using System.Security.Cryptography;
using Eve.Data;
using Eve.Dogma;
using Npgsql;

namespace Eve.Inventory.Mutation;

/// <summary>Why a mutation is refused. Every refusal is decided before the first write, so a refused request changes nothing.</summary>
public enum ItemMutationRefusal
{
    /// <summary>The mutaplasmid item does not exist.</summary>
    MutatorMissing,

    /// <summary>The source item does not exist.</summary>
    SourceMissing,

    /// <summary>The mutaplasmid and the source are the same item.</summary>
    SameItem,

    /// <summary>One of the two items is not the caller's.</summary>
    NotOwner,

    /// <summary>One of the two items is not in the location the caller is in (its station or its ship).</summary>
    NotInReach,

    /// <summary>One of the two items is fitted, or is not loose in a hangar or a cargo hold.</summary>
    NotStowed,

    /// <summary>The source is a stack; a mutation takes one assembled module.</summary>
    NotSingleton,

    /// <summary>One of the two items is blueprint-locked.</summary>
    ItemLocked,

    /// <summary>The mutator is not a published mutaplasmid of the static data.</summary>
    NotAMutaplasmid,

    /// <summary>The source is not a published input of that mutaplasmid (not applicable, unpublished, or already an output).</summary>
    InputNotMappable,

    /// <summary>The source type has no base value for an attribute the mutaplasmid moves.</summary>
    AttributeMissingInSource,
}

/// <summary>
/// What the caller asks: the mutaplasmid and source items, with <see cref="LocationId"/> naming the mutaplasmid's inventory (where the
/// result is placed). The client can select the inputs from separate inventories it can reach in its current location.
/// </summary>
public sealed record ItemMutationRequest(long CharacterId, long LocationId, long MutatorItemId, long SourceItemId)
{
    /// <summary>The source's location observed by the Host before the authority locks both inputs.</summary>
    public long? SourceLocationId { get; init; }

    /// <summary>The input flags observed by the Host before the authority locks both inputs.</summary>
    public int? MutatorFlag { get; init; }
    public int? SourceFlag { get; init; }

    /// <summary>The server-managed position of the calling pilot.</summary>
    public ItemMutationPosition? Position { get; init; }
}

/// <summary>The session position against which mutation input inventories are checked.</summary>
public sealed record ItemMutationPosition(long? DockedLocationId, long? ShipId, long? SolarSystemId);

/// <summary>
/// What became of one item in a mutation: its row after (a consumed item has quantity 0), and the place, flag and stack size it had before
/// (for the item made, the place and flag and quantity are 0: it came from nothing).
/// </summary>
public sealed record ItemMutationChange(Item After, long PreviousLocationId, int PreviousFlag, long PreviousQuantity);

/// <summary>
/// The mutated item that was created, with the provenance that was recorded for it, and the changes the mutation made to the inventory in the
/// order they are announced: the mutaplasmid, the source, then the item made.
/// </summary>
public sealed record ItemMutationReceipt(
    long ItemId,
    int TypeId,
    long LocationId,
    int Flag,
    long MutatorItemId,
    int MutatorTypeId,
    long SourceItemId,
    int SourceTypeId,
    IReadOnlyDictionary<int, double> Rolls,
    IReadOnlyDictionary<int, double> MutatedValues,
    IReadOnlyList<ItemMutationChange> Changes);

/// <summary>The outcome of a mutation request: the item that was made, or the typed reason it was refused.</summary>
public abstract record ItemMutationOutcome;

public sealed record ItemMutationAccepted(ItemMutationReceipt Receipt) : ItemMutationOutcome;

public sealed record ItemMutationRefused(ItemMutationRefusal Reason) : ItemMutationOutcome;

/// <summary>
/// The roll of one mutated attribute: a uniform draw in [0, 1) for the attribute id. It is drawn once per attribute, when the
/// item is made; the value is stored with the item and is never drawn again.
/// </summary>
public delegate double MutationRoll(int attributeId);

/// <summary>
/// The announcement of a mutation, written in the mutation's own transaction (the outbox row exists exactly when the change does, FOUNDATIONS rule 1)
/// and woken after its commit. The Event Bus is a shared foundation the domain does not reference: the composition root supplies this port.
/// </summary>
public interface IItemMutationEvents
{
    Task PublishMutatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ItemMutationService.ItemMutated payload, string key, CancellationToken cancellationToken);

    /// <summary>Asks for the announcement to be delivered now (after the commit), instead of at the next poll.</summary>
    void Wake();
}

/// <summary>
/// The authority of an abyssal mutation (ADR 0016). One PostgreSQL transaction consumes one unit of the mutaplasmid and the source
/// module, creates the mutated module in the mutaplasmid's place, records its provenance, and publishes
/// <c>item.mutated</c> through the Event Bus in that transaction. The bus is woken after the commit. Any failure before the commit
/// leaves both inputs, no record and no event.
/// </summary>
public sealed class ItemMutationService
{
    /// <summary>The event that announces a mutated item (a topic name is stored: never rename it).</summary>
    public const string MutatedTopic = "item.mutated";

    private readonly NpgsqlDataSource _source;
    private readonly PgItemStore _items;
    private readonly ItemMutationStore _mutations;
    private readonly MutatorCatalog _catalog;
    private readonly DogmaStatic _statics;
    private readonly IItemMutationEvents _events;
    private readonly Func<long> _nowUnixMs;
    private readonly MutationRoll _roll;

    public ItemMutationService(
        NpgsqlDataSource source, PgItemStore items, ItemMutationStore mutations, MutatorCatalog catalog, DogmaStatic statics,
        IItemMutationEvents events, Func<long> nowUnixMs, MutationRoll? roll = null)
    {
        _source = source;
        _items = items;
        _mutations = mutations;
        _catalog = catalog;
        _statics = statics;
        _events = events;
        _nowUnixMs = nowUnixMs;
        _roll = roll ?? CryptoRoll;
    }

    /// <summary>The production roll: a cryptographically random uniform draw in [0, 1) with 53 bits.</summary>
    public static double CryptoRoll(int attributeId)
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return (BitConverter.ToUInt64(bytes) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>The modifier fractions sent by V24.01; old records recover the fraction from their stored multiplier when possible.</summary>
    public IReadOnlyDictionary<int, double> ModifierFractions(ItemMutationRecord record)
    {
        if (record.RollFractions is { } fractions)
        {
            return fractions;
        }
        if (_catalog.Plan(record.MutatorTypeId, record.SourceTypeId) is not Accepted<MutationPlan> plan)
        {
            throw new InvalidDataException($"stored mutation {record.ItemId} has no roll fractions and its mutator mapping is unavailable");
        }
        var recovered = new Dictionary<int, double>();
        foreach (var range in plan.Value.Ranges)
        {
            if (!record.Multipliers.TryGetValue(range.AttributeId, out var multiplier))
            {
                throw new InvalidDataException($"stored mutation {record.ItemId} has no multiplier for attribute {range.AttributeId}");
            }
            var width = range.Max - range.Min;
            // A prior version stored only the multiplier; an equal-ended interval cannot preserve the original draw.
            recovered[range.AttributeId] = width == 0 ? 0 : Math.Clamp((multiplier - range.Min) / width, 0, 1);
        }
        return recovered;
    }

    /// <summary>The event payload of <see cref="MutatedTopic"/>.</summary>
    public sealed record ItemMutated(long ItemId, int ResultTypeId, int MutatorTypeId, int SourceTypeId, long CharacterId);

    public async Task<ItemMutationOutcome> MutateAsync(ItemMutationRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var outcome = await MutateInTransactionAsync(connection, transaction, request, cancellationToken);
        if (outcome is ItemMutationRefused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return outcome;
        }
        await transaction.CommitAsync(cancellationToken);
        _events.Wake();
        return outcome;
    }

    private async Task<ItemMutationOutcome> MutateInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ItemMutationRequest request, CancellationToken cancellationToken)
    {
        if (request.MutatorItemId == request.SourceItemId)
        {
            return Refuse(ItemMutationRefusal.SameItem);
        }

        // Both rows are locked in ascending id order, so two requests that name the same pair cannot deadlock.
        var firstId = Math.Min(request.MutatorItemId, request.SourceItemId);
        var secondId = Math.Max(request.MutatorItemId, request.SourceItemId);
        var first = await _items.LockAndGetAsync(connection, transaction, firstId, cancellationToken);
        var second = await _items.LockAndGetAsync(connection, transaction, secondId, cancellationToken);
        var mutator = request.MutatorItemId == firstId ? first : second;
        var source = request.SourceItemId == firstId ? first : second;

        if (mutator is null)
        {
            return Refuse(ItemMutationRefusal.MutatorMissing);
        }
        if (source is null)
        {
            return Refuse(ItemMutationRefusal.SourceMissing);
        }
        if (mutator.OwnerId != request.CharacterId || source.OwnerId != request.CharacterId)
        {
            return Refuse(ItemMutationRefusal.NotOwner);
        }
        if (mutator.LocationId != request.LocationId
            || source.LocationId != (request.SourceLocationId ?? request.LocationId)
            || request.MutatorFlag is { } mutatorFlag && mutator.Flag != mutatorFlag
            || request.SourceFlag is { } sourceFlag && source.Flag != sourceFlag)
        {
            return Refuse(ItemMutationRefusal.NotInReach);
        }
        if (!IsLoose(mutator) || !IsLoose(source))
        {
            return Refuse(ItemMutationRefusal.NotStowed);
        }
        if (request.Position is { } position
            && (!await IsReachableAsync(connection, transaction, request.CharacterId, mutator, position, cancellationToken)
                || !await IsReachableAsync(connection, transaction, request.CharacterId, source, position, cancellationToken)))
        {
            return Refuse(ItemMutationRefusal.NotInReach);
        }
        if (!source.Singleton)
        {
            return Refuse(ItemMutationRefusal.NotSingleton);
        }
        if (mutator.BlueprintLocked || source.BlueprintLocked)
        {
            return Refuse(ItemMutationRefusal.ItemLocked);
        }

        var plan = _catalog.Plan(mutator.TypeId, source.TypeId);
        if (plan is Refused<MutationPlan> refusedPlan)
        {
            return Refuse(refusedPlan.Reason == MutationRefusal.UnknownMutator
                ? ItemMutationRefusal.NotAMutaplasmid
                : ItemMutationRefusal.InputNotMappable);
        }
        var accepted = (Accepted<MutationPlan>)plan;

        // The base values are the source type's own (never the output's placeholders), read from the static data.
        await _statics.EnsureTypesAsync([source.TypeId], cancellationToken);
        var sourceAttributes = _statics.Type(source.TypeId)?.Attributes ?? new Dictionary<int, double>();

        // One roll per attribute, in the plan's order. A roll outside [0, 1] is a caller bug and throws (rolls back).
        var rolls = new Dictionary<int, double>();
        var multipliers = new Dictionary<int, double>();
        foreach (var range in accepted.Value.Ranges)
        {
            var fraction = _roll(range.AttributeId);
            var multiplier = MutationMath.MultiplierAt(range, fraction);
            rolls[range.AttributeId] = fraction;
            multipliers[range.AttributeId] = Math.Clamp(multiplier, range.Low, range.High);
        }
        var values = MutationMath.MutatedValues(sourceAttributes, accepted.Value.Ranges, multipliers);
        if (values is Refused<IReadOnlyDictionary<int, double>> refusedValues)
        {
            return refusedValues.Reason == MutationRefusal.AttributeMissingInSource
                ? Refuse(ItemMutationRefusal.AttributeMissingInSource)
                : throw new InvalidOperationException($"mutation of {source.TypeId} by {mutator.TypeId} refused after validation: {refusedValues.Reason}");
        }
        var mutatedValues = ((Accepted<IReadOnlyDictionary<int, double>>)values).Value;
        var baseValues = accepted.Value.Ranges.ToDictionary(range => range.AttributeId, range => sourceAttributes[range.AttributeId]);

        // Consume exactly one unit of the mutaplasmid, and the source module.
        var changes = new List<ItemMutationChange>(3);
        if (mutator.Singleton || mutator.Quantity <= 1)
        {
            await _items.DeleteAsync(connection, transaction, mutator.Id, cancellationToken);
            changes.Add(new ItemMutationChange(mutator with { Quantity = 0 }, mutator.LocationId, mutator.Flag, mutator.Quantity));
        }
        else
        {
            await _items.SetQuantityAsync(connection, transaction, mutator.Id, mutator.Quantity - 1, cancellationToken);
            changes.Add(new ItemMutationChange(mutator with { Quantity = mutator.Quantity - 1 }, mutator.LocationId, mutator.Flag, mutator.Quantity));
        }
        await _items.DeleteAsync(connection, transaction, source.Id, cancellationToken);
        changes.Add(new ItemMutationChange(source with { Quantity = 0 }, source.LocationId, source.Flag, source.Quantity));

        // The client presents the result at the mutaplasmid's location and flag (V24.01 craftingWindow.py:329-331).
        var resultId = await _items.CreateAsync(connection, transaction,
            new NewItem(accepted.Value.OutputTypeId, request.CharacterId, mutator.LocationId, mutator.Flag, 1, Singleton: true), cancellationToken);
        var result = new Item(resultId, accepted.Value.OutputTypeId, request.CharacterId, mutator.LocationId, mutator.Flag, 1, Singleton: true, CustomName: null);
        changes.Add(new ItemMutationChange(result, 0, 0, 0));

        var createdAtMs = _nowUnixMs();
        await _mutations.InsertAsync(connection, transaction, new ItemMutationRecord(
            resultId, mutator.TypeId, mutator.Id, source.TypeId, source.Id, request.CharacterId, createdAtMs,
            multipliers, baseValues, mutatedValues, rolls), cancellationToken);

        await _events.PublishMutatedAsync(connection, transaction,
            new ItemMutated(resultId, accepted.Value.OutputTypeId, mutator.TypeId, source.TypeId, request.CharacterId),
            $"char:{request.CharacterId}", cancellationToken);

        return new ItemMutationAccepted(new ItemMutationReceipt(
            resultId, accepted.Value.OutputTypeId, mutator.LocationId, mutator.Flag,
            mutator.Id, mutator.TypeId, source.Id, source.TypeId, rolls, mutatedValues, changes));
    }

    /// <summary>Checks that an input is in the current hangar, current ship cargo, or an owned inventory under the current station/ship.</summary>
    private async Task<bool> IsReachableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, Item item, ItemMutationPosition position,
        CancellationToken cancellationToken)
    {
        if (position.DockedLocationId is { } dockedLocationId && dockedLocationId > 0 && item.LocationId == dockedLocationId)
        {
            return item.Flag == ItemFlags.Hangar;
        }
        if (position.ShipId is { } shipId && shipId > 0 && item.LocationId == shipId && item.Flag == ItemFlags.Cargo)
        {
            var ship = await _items.LockAndGetAsync(connection, transaction, shipId, cancellationToken);
            return ship is not null && ship.OwnerId == characterId && await IsShipAsync(ship.TypeId, cancellationToken)
                && (position.DockedLocationId is { } docked && docked > 0
                    ? ship.LocationId == docked && ship.Flag == ItemFlags.Hangar
                    : position.SolarSystemId is { } solarSystemId && solarSystemId > 0
                        && ship.LocationId == solarSystemId && ship.Flag == ItemFlags.InSpace);
        }
        if (item.Flag != ItemFlags.Hangar)
        {
            return false;
        }

        var seen = new HashSet<long>();
        var currentLocationId = item.LocationId;
        for (var depth = 0; depth < 32; depth++)
        {
            if (position.DockedLocationId is { } stationId && stationId > 0 && currentLocationId == stationId)
            {
                return true;
            }
            if (currentLocationId <= 0 || !seen.Add(currentLocationId)
                || await _items.LockAndGetAsync(connection, transaction, currentLocationId, cancellationToken) is not { } container
                || container.OwnerId != characterId)
            {
                return false;
            }
            if (await IsShipAsync(container.TypeId, cancellationToken))
            {
                return (position.DockedLocationId is { } docked && docked > 0
                        && container.LocationId == docked && container.Flag == ItemFlags.Hangar)
                    || (container.Id == position.ShipId && position.SolarSystemId is { } solarSystemId && solarSystemId > 0
                        && container.LocationId == solarSystemId && container.Flag == ItemFlags.InSpace);
            }
            if (!await IsContainerAsync(container.TypeId, cancellationToken))
            {
                return false;
            }
            currentLocationId = container.LocationId;
        }
        return false;
    }

    private async Task<bool> IsShipAsync(int typeId, CancellationToken cancellationToken)
    {
        await _statics.EnsureTypesAsync([typeId], cancellationToken);
        return _statics.Type(typeId) is { } type
            && string.Equals(_statics.CategoryName(type.CategoryId), "Ship", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsContainerAsync(int typeId, CancellationToken cancellationToken)
    {
        await _statics.EnsureTypesAsync([typeId], cancellationToken);
        var type = _statics.Type(typeId);
        if (type is null)
        {
            return false;
        }
        if (typeId == ItemVolumes.PlasticWrapTypeId) // which the V24.01 ItemChecker treats as a local container
        {
            return true;
        }
        var capacityId = _statics.AttributeId("capacity");
        return _statics.GroupName(type.GroupId).Contains("container", StringComparison.OrdinalIgnoreCase)
            && capacityId is { } attributeId && type.Attributes.GetValueOrDefault(attributeId, 0) > 0;
    }

    /// <summary>Loose in a hangar or a cargo hold: a fitted item (a slot flag) is not usable as an input.</summary>
    private static bool IsLoose(Item item) => item.Flag is ItemFlags.Hangar or ItemFlags.Cargo;

    private static ItemMutationOutcome Refuse(ItemMutationRefusal reason) => new ItemMutationRefused(reason);
}
