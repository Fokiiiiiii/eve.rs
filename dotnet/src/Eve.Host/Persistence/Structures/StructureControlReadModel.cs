using System.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>Read-only control views over the existing structure, pilot, inventory and fitting authorities.</summary>
public sealed class StructureControlReadModel(
    NpgsqlDataSource source, StructureStore structures, StructurePilotStore pilots,
    PgItemStore items, PgModuleStateStore modules, DogmaStatic statics,
    Func<long, long, long, CancellationToken, ValueTask<bool>> hasSetting)
{
    public async ValueTask<ControlledStructureInventory?> InventoryAsync(
        ClientSession session, long structureId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character
            || await ReadAsync(session, character, structureId, cancellationToken) is not { } read)
            return null;
        await statics.EnsureTypesAsync([read.Record.TypeId, .. read.Contents.Select(item => item.TypeId)], cancellationToken);
        return IsStructure(read.Record.TypeId)
            ? new ControlledStructureInventory(ItemOf(read.Record), read.Contents)
            : null;
    }

    public async ValueTask<ShipSnapshot?> DogmaAsync(
        ClientSession session, ShipSnapshot personal, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || personal.Character.Id != character
            || session.GetInt("structureid") is not { } structureId
            || await ReadAsync(session, character, structureId, cancellationToken) is not { } read)
            return null;
        var slots = read.Contents.Where(item => Slots.IsSlotFlag(item.Flag)).OrderBy(item => item.Flag).ThenBy(item => item.Id).ToArray();
        var mutations = await items.Mutations.GetManyAsync(slots.Select(item => item.Id).ToArray(), cancellationToken);
        await statics.EnsureTypesAsync(
            [read.Record.TypeId, personal.Character.TypeId, .. slots.Select(item => item.TypeId),
             .. mutations.Values.Select(mutation => mutation.SourceTypeId), .. personal.Skills.Select(skill => skill.TypeId)], cancellationToken);
        if (!IsStructure(read.Record.TypeId) || statics.CategoryId("Charge") is not { } chargeCategory
            || slots.Any(item => statics.Type(item.TypeId) is null))
            return null;
        var fitted = slots.Where(item => statics.Type(item.TypeId)!.CategoryId != chargeCategory).ToArray();
        var charges = slots.Where(item => statics.Type(item.TypeId)!.CategoryId == chargeCategory).ToArray();
        var states = await modules.StatesAsync(fitted.Select(item => item.Id), cancellationToken);
        var conditions = await modules.ModuleConditionsAsync(fitted.Select(item => item.Id), cancellationToken);
        var ship = ItemOf(read.Record);
        var world = new DogmaWorld(statics, character, structureId, structureId);
        world.Add(new DogmaItem(character, personal.Character.TypeId, character, structureId, ShipSnapshot.PilotFlag, ItemState.Online));
        foreach (var attribute in ShipSnapshot.LearningAttributes)
            world.SetValue(character, attribute, personal.World.Get(character, attribute));
        world.Add(ItemDogma.Of(ship, character, ship.LocationId, ship.Flag));
        foreach (var module in fitted)
            world.Add(ItemDogma.Of(module, character, structureId, module.Flag, states[module.Id], mutations.GetValueOrDefault(module.Id)));
        foreach (var charge in charges)
        {
            world.Add(ItemDogma.Of(charge, character, structureId, charge.Flag, mutation: mutations.GetValueOrDefault(charge.Id)));
            world.SetValue(charge.Id, ShipSnapshot.QuantityCharge, charge.Quantity);
        }
        foreach (var skill in personal.Skills)
            if (personal.World.Item(ShipSnapshot.SkillItemId(character, skill.TypeId)) is { } item)
                world.Add(item);
        return new ShipSnapshot
        {
            Character = personal.Character, Ship = ship, Fitted = fitted, Charges = charges,
            Skills = personal.Skills, World = world, ModuleConditions = conditions,
            Condition = await modules.ShipConditionAsync(structureId, cancellationToken),
        };
    }

    private bool IsStructure(int typeId) =>
        statics.CategoryId("Structure") is { } category && statics.Type(typeId)?.CategoryId == category;

    private static Item ItemOf(StructureRecord record) =>
        new(record.Snapshot.StructureId, record.TypeId, record.OwnerCorporationId, record.SolarSystemId, 0, 1, true, record.Name);

    private async Task<(StructureRecord Record, IReadOnlyList<Item> Contents)?> ReadAsync(
        ClientSession session, long character, long structureId, CancellationToken cancellationToken)
    {
        if (session.SessionId <= 0 || structureId <= 0 || session.GetInt("shipid") != structureId
            || session.GetInt("structureid") != structureId)
            return null;
        if (!await hasSetting(character, structureId, StructureControlBackend.DefenseCanControlStructure, cancellationToken))
            return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var pilot = await pilots.GetBySessionAsync(connection, transaction, session.SessionId, cancellationToken);
        if (pilot is null || pilot.CharacterId != character || pilot.StructureId != structureId || pilot.SessionId != session.SessionId)
            return null;
        var record = await structures.GetAsync(connection, transaction, structureId, false, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs != 0 || record.OwnerCorporationId <= 0 || record.SolarSystemId <= 0)
            return null;
        var contents = await items.InContainerAsync(connection, transaction, structureId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (record, contents.Where(item => item.OwnerId == record.OwnerCorporationId).ToArray());
    }
}
