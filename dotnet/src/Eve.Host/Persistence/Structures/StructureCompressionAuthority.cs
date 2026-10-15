using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Inventory;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Skills;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The compression of <c>structureCompressionMgr</c>: a pilot compresses ore, ice and moon ore in the hangar (or in the ship) it has at a structure, and
/// decompresses gas there. The gates are EveJS's (<c>mining/miningIndustry.js</c> <c>resolveReprocessingContext</c>, <c>compressInventoryItem</c>,
/// <c>decompressGasInStructure</c>): the session is docked at a structure whose Reprocessing service is online, the item is the pilot's in the hangar or
/// in the ship docked there, and the type is in the compression list (336) or a compressed gas. Every refusal is a named <c>CustomNotify</c>; EveJS answers
/// None. The stack is replaced in one transaction: the output joins a stack of its type the container has, or is created, and the source is deleted (the
/// ship's compression does the same, <c>CharacterShips.CompressAsync</c>). The efficiencies are the SDE's (see DEVIATIONS).
/// </summary>
public sealed class StructureCompressionAuthority
{
    private const int CompressionTypeList = 336;
    private const int GasDecompressionSkill = 62452;
    private const int SkillBonusAttribute = 3260;
    private const int StructureBaseEfficiencyAttribute = 3262;
    private const int HullBonusAttribute = 3261;
    private const int HangarFlag = ItemFlags.Hangar;

    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _store;
    private readonly PgItemStore _items;
    private readonly DogmaStatic _dogma;
    private readonly SdeReader _sde;
    private readonly PgSkillStore _skills;
    private readonly ConcurrentDictionary<int, bool> _gasGroups = new();
    private readonly CorporationHangarAccess _hangarAccess;
    private readonly OfficeAtStation _officeAt;
    private readonly CharacterSessions _sessions;
    private IReadOnlyDictionary<int, int>? _sourceOf;

    /// <summary>Whether the corporation has an active office rented at the station (EveJS getCorporationOfficeByInventoryID with the station check).</summary>
    public delegate Task<bool> OfficeAtStation(NpgsqlConnection connection, NpgsqlTransaction transaction, long corporationId, long stationId, CancellationToken cancellationToken);

    /// <param name="hangarAccess">The corporation division access of the inventory (the same rule as the take and deposit of the hangar).</param>
    /// <param name="officeAt">The office rental of the corporation at the station, read in the caller transaction.</param>
    /// <param name="sessions">The sessions of the corporation members, who are told of a change to its office hangar.</param>
    public StructureCompressionAuthority(NpgsqlDataSource source, StructureStore store, PgItemStore items, DogmaStatic dogma, SdeReader sde, PgSkillStore skills,
        CorporationHangarAccess hangarAccess, OfficeAtStation officeAt, CharacterSessions sessions)
    {
        _source = source;
        _store = store;
        _items = items;
        _dogma = dogma;
        _sde = sde;
        _skills = skills;
        _hangarAccess = hangarAccess;
        _officeAt = officeAt;
        _sessions = sessions;
    }

    /// <summary>
    /// <c>CompressItemInStructure</c>: the ore, ice or moon ore becomes its compressed form, the same number of units. Returns the six values of the reply and
    /// the item changes the pilot is told.
    /// </summary>
    public async Task<StructureCompressionOutcome> CompressAsync(ServiceCall call, long itemId, CancellationToken cancellationToken = default)
    {
        var (characterId, structureId, shipId) = PlaceOf(call);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await GateAsync(connection, transaction, structureId, cancellationToken);
        var item = await SourceItemAsync(call, connection, transaction, characterId, structureId, shipId, itemId, cancellationToken);
        await _dogma.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (!await _dogma.TypeListNamesAsync(CompressionTypeList, item.TypeId, cancellationToken)
            || await _dogma.CompressedTypeOfAsync(item.TypeId, cancellationToken) is not { } compressedTypeId)
        {
            throw Refusal("That item cannot be compressed in a structure.");
        }
        return await AnnounceAsync(call, await ReplaceAsync(connection, transaction, item, compressedTypeId, item.Quantity, cancellationToken), item, structureId, cancellationToken);
    }

    /// <summary>
    /// <c>DecompressGasInStructure</c>: a compressed gas becomes gas of its source type, with the structure's and the pilot's efficiency: the output is the floor of
    /// the quantity times the total (capped at one). Less than one unit left removes the item, and the reply has no output.
    /// </summary>
    public async Task<StructureCompressionOutcome> DecompressAsync(ServiceCall call, long itemId, CancellationToken cancellationToken = default)
    {
        var (characterId, structureId, shipId) = PlaceOf(call);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var structure = await GateAsync(connection, transaction, structureId, cancellationToken);
        var item = await SourceItemAsync(call, connection, transaction, characterId, structureId, shipId, itemId, cancellationToken);
        await _dogma.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (await GasSourceOfAsync(item.TypeId, cancellationToken) is not { } gasTypeId)
        {
            throw Refusal("That item is not compressed gas.");
        }
        var total = Math.Min(1.0, await StructureEfficiencyAsync(structure.TypeId, cancellationToken) + await CharacterEfficiencyAsync(connection, transaction, characterId, cancellationToken));
        // the rounding of EveJS floorAfterNormalizing: 0.8 + 0.03 times 1000 is 830, not a float's 829
        var output = (long)Math.Floor(Math.Round(item.Quantity * total, 9));
        if (output <= 0)
        {
            await _items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
            var removed = item with { LocationId = CharacterShips.JunkLocation };
            await transaction.CommitAsync(cancellationToken);
            return await AnnounceAsync(call, (new StructureCompressionOutcome(item.Id, item.TypeId, item.Quantity, 0, gasTypeId, 0),
                [new ItemChanged(removed, item.LocationId, item.Flag, item.Quantity)]), item, structureId, cancellationToken);
        }
        return await AnnounceAsync(call, await ReplaceAsync(connection, transaction, item, gasTypeId, output, cancellationToken), item, structureId, cancellationToken);
    }

    /// <summary>
    /// <c>GetMyGasDecompressionEfficiency</c>: the structure's and the pilot's efficiency. Zero and zero when the pilot is not docked at a structure with its
    /// Reprocessing service online (the EveJS answer then).
    /// </summary>
    public async Task<(double Structure, double Character)> EfficiencyAsync(ServiceCall call, CancellationToken cancellationToken = default)
    {
        var (characterId, structureId, _) = PlaceOf(call);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (structureId <= 0 || await _store.GetAsync(connection, transaction, structureId, false, cancellationToken) is not { } record || record.Snapshot.DestroyedMs > 0
            || !ReprocessingOnline(await _store.ServicesAsync(connection, transaction, structureId, cancellationToken)))
        {
            return (0, 0);
        }
        return (await StructureEfficiencyAsync(record.TypeId, cancellationToken), await CharacterEfficiencyAsync(connection, transaction, characterId, cancellationToken));
    }

    /// <summary>The session's place: the character, the structure it is docked at (0 when none) and the ship it flies (0 when none).</summary>
    private static (long CharacterId, long StructureId, long ShipId) PlaceOf(ServiceCall call) =>
        (call.Session.CharacterId ?? 0, call.Session.GetInt("structureid") ?? 0, call.Session.GetInt("shipid") ?? 0);

    private static bool ReprocessingOnline(IReadOnlyDictionary<int, int> services) =>
        services.GetValueOrDefault(StructureServiceIds.Reprocessing) == ServiceState.Online;

    /// <summary>Opens the gate: docked at a structure that exists and is not gone, its Reprocessing service online. Returns the structure's record.</summary>
    private async Task<StructureRecord> GateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken)
    {
        if (structureId <= 0 || await _store.GetAsync(connection, transaction, structureId, true, cancellationToken) is not { } record || record.Snapshot.DestroyedMs > 0)
        {
            throw Refusal("You must be docked at a structure to compress here.");
        }
        if (!ReprocessingOnline(await _store.ServicesAsync(connection, transaction, structureId, cancellationToken)))
        {
            throw Refusal("The reprocessing service of this structure is offline.");
        }
        return record;
    }

    /// <summary>
    /// The item is the pilot's in the hangar of the structure, or in the ship it flies when that ship is docked there, or a corporation hangar item of the
    /// pilot's corporation in its office at the structure that the pilot may take from (EveJS <c>itemIsInStructureCompressionSource</c>, with
    /// <c>itemIsInDockableCorpOfficeWithTakeAccess</c>).
    /// </summary>
    private async Task<Item> SourceItemAsync(ServiceCall call, NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long structureId, long shipId, long itemId, CancellationToken cancellationToken)
    {
        var corporationId = CorporationOf(call.Session);
        if (await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item || item.Singleton || item.Quantity <= 0
            || (item.OwnerId != characterId && (corporationId <= 0 || item.OwnerId != corporationId)))
        {
            throw Refusal("That item is not yours or cannot be used here.");
        }
        if (item.OwnerId == characterId)
        {
            if (item.LocationId == structureId && item.Flag == HangarFlag)
            {
                return item;
            }
            if (shipId > 0 && item.LocationId == shipId && await _items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is { } ship
                && ship.LocationId == structureId && ship.OwnerId == characterId)
            {
                return item;
            }
        }
        else if (item.LocationId == structureId && ItemFlags.IsCorporationHangar(item.Flag)
            && await _officeAt(connection, transaction, corporationId, structureId, cancellationToken)
            && await _hangarAccess(characterId, corporationId, structureId, item.Flag, CorporationHangarOperation.Take, cancellationToken))
        {
            return item;
        }
        throw Refusal("That item is not in your hangar or your ship at this structure.");
    }

    /// <summary>The corporation of the session (its corporationID, else its corpid, as the inventory broker reads it); 0 when none.</summary>
    private static long CorporationOf(ClientSession session) =>
        session.GetInt("corporationID") is { } corporationId && corporationId != 0 ? corporationId : session.GetInt("corpid") ?? 0;

    /// <summary>The compressed type's source in the SDE (<c>compressibleTypes</c>, read backwards), loaded once.</summary>
    private async Task<int?> SourceOfAsync(int compressedTypeId, CancellationToken cancellationToken)
    {
        if (_sourceOf is null)
        {
            var map = new Dictionary<int, int>();
            foreach (var row in await _sde.ReadAsync("compressibleTypes", null, cancellationToken))
            {
                map[row.Doc.GetProperty("compressedTypeID").GetInt32()] = (int)row.Key;
            }
            _sourceOf = map;
        }
        return _sourceOf.TryGetValue(compressedTypeId, out var source) ? source : null;
    }

    /// <summary>A compressed gas: its source belongs to a gas group (the group's name says gas or cloud, as EveJS <c>resolveGasDecompressionSourceType</c>).</summary>
    private async Task<int?> GasSourceOfAsync(int compressedTypeId, CancellationToken cancellationToken)
    {
        if (await SourceOfAsync(compressedTypeId, cancellationToken) is not { } source)
        {
            return null;
        }
        await _dogma.EnsureTypesAsync([source], cancellationToken);
        if (_dogma.Type(source) is not { } type)
        {
            return null;
        }
        if (!_gasGroups.TryGetValue(type.GroupId, out var isGas))
        {
            isGas = false;
            foreach (var group in await _sde.ReadAsync("groups", [type.GroupId], cancellationToken))
            {
                var name = group.Doc.GetProperty("name").GetProperty("en").GetString() ?? "";
                isGas = name.Contains("gas", StringComparison.OrdinalIgnoreCase) || name.Contains("cloud", StringComparison.OrdinalIgnoreCase);
            }
            _gasGroups[type.GroupId] = isGas;
        }
        return isGas ? source : null;
    }

    /// <summary>The structure's efficiency: the Reprocessing facility's base (SDE 3262 on module 35899) and the hull's bonus (3261; the refineries have one).</summary>
    private async Task<double> StructureEfficiencyAsync(int hullTypeId, CancellationToken cancellationToken)
    {
        var facility = StructureServiceIds.ModuleTypeOf[StructureServiceIds.Reprocessing];
        await _dogma.EnsureTypesAsync([facility, hullTypeId], cancellationToken);
        var baseEfficiency = _dogma.Type(facility)?.Attributes.GetValueOrDefault(StructureBaseEfficiencyAttribute) ?? 0;
        var hullBonus = _dogma.Type(hullTypeId)?.Attributes.GetValueOrDefault(HullBonusAttribute) ?? 0;
        return Math.Clamp(baseEfficiency + hullBonus, 0, 1);
    }

    /// <summary>The pilot's efficiency: a percent per level of the Gas Decompression Efficiency skill (SDE 3260 on the skill).</summary>
    private async Task<double> CharacterEfficiencyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await _dogma.EnsureTypesAsync([GasDecompressionSkill], cancellationToken);
        var level = await _skills.LevelAsync(connection, transaction, characterId, GasDecompressionSkill, cancellationToken);
        var perLevel = _dogma.Type(GasDecompressionSkill)?.Attributes.GetValueOrDefault(SkillBonusAttribute) ?? 0;
        return Math.Max(0, level * perLevel / 100.0);
    }

    /// <summary>
    /// Replaces a stack by <paramref name="quantity"/> of <paramref name="typeId"/> in the same place: the output joins a stack of that type the container has
    /// (same owner and flag, not a singleton), or is created, and the source is deleted. One transaction.
    /// </summary>
    private async Task<(StructureCompressionOutcome Outcome, IReadOnlyList<ItemChanged> Changes)> ReplaceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Item source, int typeId, long quantity, CancellationToken cancellationToken)
    {
        var changes = new List<ItemChanged>(3);
        await _dogma.EnsureTypesAsync([typeId], cancellationToken);
        var join = (await _items.InContainerAsync(connection, transaction, source.LocationId, cancellationToken))
            .FirstOrDefault(other => other.Id != source.Id && other.TypeId == typeId && other.Flag == source.Flag && other.OwnerId == source.OwnerId && !other.Singleton);
        long outputId;
        if (join is not null && await _items.LockAndGetAsync(connection, transaction, join.Id, cancellationToken) is { } joined)
        {
            outputId = joined.Id;
            await _items.SetQuantityAsync(connection, transaction, joined.Id, joined.Quantity + quantity, cancellationToken);
            changes.Add(new ItemChanged(joined with { Quantity = joined.Quantity + quantity }, joined.LocationId, joined.Flag, joined.Quantity));
        }
        else
        {
            outputId = await _items.CreateAsync(connection, transaction, new NewItem(typeId, source.OwnerId, source.LocationId, source.Flag, quantity, false), cancellationToken);
            changes.Add(new ItemChanged(new Item(outputId, typeId, source.OwnerId, source.LocationId, source.Flag, quantity, false, null), source.LocationId, source.Flag, 0));
        }
        await _items.DeleteAsync(connection, transaction, source.Id, cancellationToken);
        changes.Add(new ItemChanged(source with { LocationId = CharacterShips.JunkLocation }, source.LocationId, source.Flag, source.Quantity));
        await transaction.CommitAsync(cancellationToken);
        return (new StructureCompressionOutcome(source.Id, source.TypeId, source.Quantity, outputId, typeId, quantity), changes);
    }

    /// <summary>
    /// The pilot's client hears each item that changed (the stack that left, the output), as the ship's compression tells it. A corporation hangar item
    /// changes the corporation's office hangar: the members online who may query that division are told too (EveJS tells the members watching it).
    /// </summary>
    private async Task<StructureCompressionOutcome> AnnounceAsync(ServiceCall call, (StructureCompressionOutcome Outcome, IReadOnlyList<ItemChanged> Changes) result,
        Item source, long structureId, CancellationToken cancellationToken)
    {
        foreach (var change in result.Changes)
        {
            ShipNotifications.SendItemChange(call.Session, change, RowOf);
        }
        if (source.OwnerId == (call.Session.CharacterId ?? 0) || !ItemFlags.IsCorporationHangar(source.Flag))
        {
            return result.Outcome;
        }
        foreach (var session in _sessions.OfCorporation(source.OwnerId))
        {
            if (ReferenceEquals(session, call.Session) || session.CharacterId is not { } observer
                || !await _hangarAccess(observer, source.OwnerId, structureId, source.Flag, CorporationHangarOperation.Query, cancellationToken))
            {
                continue;
            }
            foreach (var change in result.Changes)
            {
                ShipNotifications.SendItemChange(session, change, RowOf);
            }
        }
        return result.Outcome;
    }

    private ItemRow RowOf(Item item)
    {
        var type = _dogma.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The refusal the client shows: a named notice. The client reports success on any reply, so an empty answer would look like a compression.</summary>
    public static Exception Refusal(string message) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
