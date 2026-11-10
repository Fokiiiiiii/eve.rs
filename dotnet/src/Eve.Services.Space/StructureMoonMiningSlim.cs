using Eve.Dogma;
using Eve.Inventory;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

internal sealed record StructureMoonMiningSlimState(IReadOnlyList<SimStructureModule> Modules, long ClosestMoonId);

/// <summary>The structure module tuples and target moon the V24 refinery slim item uses.</summary>
internal static class StructureMoonMiningSlim
{
    private const int MoonDrillTypeId = 45_009;
    private const int ChargeCategoryId = 8;

    public static async Task<StructureMoonMiningSlimState> ReadAsync(
        NpgsqlConnection connection,
        PgItemStore items,
        MoonExtractionStore extractions,
        DogmaStatic statics,
        long structureId,
        CancellationToken cancellationToken)
    {
        var contents = await items.InContainerAsync(connection, null, structureId, cancellationToken);
        var candidates = contents.Where(item => IsShipSlot(item.Flag) || IsServiceSlot(item.Flag) && item.TypeId == MoonDrillTypeId).ToArray();
        await statics.EnsureTypesAsync(candidates.Select(item => item.TypeId), cancellationToken);
        var modules = candidates
            .Where(item => IsServiceSlot(item.Flag)
                ? item.TypeId == MoonDrillTypeId
                : statics.Type(item.TypeId)?.CategoryId != ChargeCategoryId)
            .Select(item => new SimStructureModule(item.Id, item.TypeId, item.Flag))
            .ToArray();
        var profile = await extractions.ProfileAsync(connection, null, structureId, cancellationToken);
        return new StructureMoonMiningSlimState(modules, profile?.MoonId > 0 ? profile.MoonId : 0);
    }

    private static bool IsShipSlot(int flag) => flag is >= 11 and <= 34;

    private static bool IsServiceSlot(int flag) => flag is >= 164 and <= 171;
}
