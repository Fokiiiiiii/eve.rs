using Eve.Dogma;
using Eve.Sde;
using Eve.Structures;

namespace Eve.Host;

/// <summary>Reads service-module fuel dogma and structure fuel bonuses from the pinned SDE.</summary>
public sealed class ServiceFuelRulesSource : IServiceFuelRules
{
    private readonly DogmaStatic _dogma;
    private readonly IReadOnlyDictionary<int, int[]> _servicesByModuleType;
    private readonly IReadOnlyDictionary<int, int> _moduleTypeOf;
    private readonly int _fuelConsumptionGroupAttribute;
    private readonly int _fuelPerHourAttribute;
    private readonly int _fuelToOnlineAttribute;

    private ServiceFuelRulesSource(DogmaStatic dogma, IReadOnlyDictionary<int, int[]> servicesByModuleType)
    {
        _dogma = dogma;
        _servicesByModuleType = servicesByModuleType;
        _moduleTypeOf = servicesByModuleType
            .SelectMany(pair => pair.Value.Select(serviceId => (serviceId, TypeId: pair.Key)))
            .GroupBy(pair => pair.serviceId)
            .ToDictionary(group => group.Key, group => group.First().TypeId);
        _fuelConsumptionGroupAttribute = dogma.AttributeId("serviceModuleFuelConsumptionGroup")
            ?? throw new InvalidOperationException("the pinned SDE has no serviceModuleFuelConsumptionGroup dogma attribute");
        _fuelPerHourAttribute = dogma.AttributeId("serviceModuleFuelAmount")
            ?? throw new InvalidOperationException("the pinned SDE has no serviceModuleFuelAmount dogma attribute");
        _fuelToOnlineAttribute = dogma.AttributeId("serviceModuleFuelOnlineAmount")
            ?? throw new InvalidOperationException("the pinned SDE has no serviceModuleFuelOnlineAmount dogma attribute");
    }

    public static async Task<ServiceFuelRulesSource> LoadAsync(
        DogmaStatic dogma,
        SdeReader sde,
        CancellationToken cancellationToken = default)
    {
        var build = await sde.BuildAsync(cancellationToken)
            ?? throw new InvalidOperationException("the pinned SDE is not loaded");
        var summaries = await sde.ReadTypeSummariesAsync(cancellationToken);
        var servicesByModuleType = new Dictionary<int, int[]>();
        foreach (var (name, services) in StructureServiceIds.ServicesByModuleTypeName)
        {
            var matches = summaries
                .Where(type => string.Equals(type.Name, name, StringComparison.Ordinal))
                .Select(type => type.TypeId)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"SDE build {build} resolves '{name}' to {matches.Length} types");
            servicesByModuleType.Add(matches[0], services);
        }

        await dogma.EnsureTypesAsync(servicesByModuleType.Keys, cancellationToken);
        await dogma.TypesInCategoryAsync("Structure", cancellationToken);
        return new ServiceFuelRulesSource(dogma, servicesByModuleType);
    }

    public ServiceFuelRule? RuleOf(int serviceId)
    {
        if (!_moduleTypeOf.TryGetValue(serviceId, out var typeId)
            || DogmaType(typeId) is not { } type
            || !type.Attributes.TryGetValue(_fuelPerHourAttribute, out var hourly)
            || !type.Attributes.TryGetValue(_fuelToOnlineAttribute, out var online))
            return null;
        return new ServiceFuelRule(serviceId, checked((int)hourly), checked((int)online));
    }

    public StructureServiceModuleFuelRule? RuleOfModule(int moduleTypeId, int structureTypeId)
    {
        if (!_servicesByModuleType.ContainsKey(moduleTypeId)
            || DogmaType(moduleTypeId) is not { } module || DogmaType(structureTypeId) is null
            || !module.Attributes.ContainsKey(_fuelConsumptionGroupAttribute)
            || !module.Attributes.ContainsKey(_fuelPerHourAttribute)
            || !module.Attributes.ContainsKey(_fuelToOnlineAttribute))
            return null;

        const long structureId = 1;
        const long moduleId = 2;
        const long ownerId = 3;
        var world = new DogmaWorld(_dogma, structureId: structureId);
        world.Add(new DogmaItem(structureId, structureTypeId, ownerId, structureId, state: ItemState.Online));
        world.Add(new DogmaItem(moduleId, moduleTypeId, ownerId, structureId, StructureServiceIds.ServiceSlotFirst, ItemState.Offline));
        var hourly = world.Get(moduleId, _fuelPerHourAttribute);
        var online = world.Get(moduleId, _fuelToOnlineAttribute);
        return double.IsFinite(hourly) && double.IsFinite(online) && hourly >= 0 && online >= 0
            ? new StructureServiceModuleFuelRule(hourly, online)
            : null;
    }

    public IReadOnlyList<int> ServicesOfModule(int moduleTypeId) =>
        _servicesByModuleType.TryGetValue(moduleTypeId, out var services) ? services : [];

    public int? ModuleTypeOf(int serviceId) =>
        _moduleTypeOf.TryGetValue(serviceId, out var typeId) ? typeId : null;

    private TypeDef? DogmaType(int typeId) => _dogma.Type(typeId);
}
