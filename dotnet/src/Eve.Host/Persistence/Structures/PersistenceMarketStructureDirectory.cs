using System.Text.Json;
using Eve.Market;
using Eve.Marshal;
using Eve.Sde;
using Eve.Structures;

namespace Eve.Host;

/// <summary>Resolves current persisted Upwell structures through their service profile and the pinned SDE map hierarchy.</summary>
public sealed class PersistenceMarketStructureDirectory(
    StructureStore structures,
    PersistenceStructureDirectoryReadModel access,
    MarketTopology topology,
    StructureRulesSource rules,
    IReadOnlySet<int> refineryTypeIds) : IMarketStructureDirectory
{
    public static async Task<IReadOnlySet<int>> ReadRefineryTypeIdsAsync(SdeReader sde,CancellationToken cancellationToken = default)
    {
        var types=await sde.ReadAsync("types",cancellationToken:cancellationToken);
        return types.Where(row => row.Doc.TryGetProperty("name",out var name) &&
                SdeReader.English(name) is "Athanor" or "Tatara")
            .Select(row => checked((int)row.Key))
            .ToHashSet();
    }

    private bool IsEligible(MarketStructure structure)
    {
        if (refineryTypeIds.Contains(structure.TypeId)) return false;
        try
        {
            return rules.RulesOf(structure.TypeId,structure.Location.SolarSystemId).Size
                is StructureSize.Large or StructureSize.ExtraLarge;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDeployedState(int state) => state is
        StructureState.ShieldVulnerable or StructureState.ArmorReinforce or StructureState.ArmorVulnerable or
        StructureState.HullReinforce or StructureState.HullVulnerable or StructureState.AnchorVulnerable or
        StructureState.DeployVulnerable;
    public async ValueTask<MarketStructure?> GetAsync(long structureId,CancellationToken cancellationToken)
    {
        if (structureId <= 0) return null;
        var record=await structures.GetAsync(structureId,cancellationToken);
        if (record is null || record.Snapshot.StructureId != structureId || record.Snapshot.DestroyedMs > 0 ||
            record.OwnerCorporationId <= 0 || record.SolarSystemId <= 0)
            return null;
        if (!IsDeployedState(record.Snapshot.State)) return null;
        var location=topology.StructureLocation(structureId,record.SolarSystemId);
        if (location is null) return null;
        var name=string.IsNullOrWhiteSpace(record.Name) ? $"Structure {structureId}" : record.Name;
        var structure=new MarketStructure(structureId,record.TypeId,record.OwnerCorporationId,location.Value,name);
        return IsEligible(structure) ? structure : null;
    }

    public async ValueTask<bool> CanAccessMarketAsync(long characterId,long structureId,CancellationToken cancellationToken)
    {
        if (await GetAsync(structureId,cancellationToken) is null) return false;
        return await access.CharacterHasServiceAsync(
            characterId,structureId,StructureServiceIds.Market,cancellationToken);
    }

    public async ValueTask<double?> BrokerTaxPercentAsync(long characterId,long structureId,CancellationToken cancellationToken)
    {
        if (!await CanAccessMarketAsync(characterId,structureId,cancellationToken)) return null;
        var value=await access.CharacterGetServiceAsync(
            characterId,structureId,StructureServiceIds.Market,cancellationToken);
        return value switch
        {
            PyReal real when double.IsFinite(real.Value) => real.Value,
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => null,
        };
    }
}