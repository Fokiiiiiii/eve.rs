using Eve.Dogma;
using Eve.Sde;
using Eve.Structures;

namespace Eve.Host;

/// <summary>
/// The fuel of each structure service, from the pinned SDE typeDogma attributes 2109 and 2110 on its service module.
/// The market hub type is resolved by its SDE English name because its item type is static data, not a runtime setting.
/// </summary>
public sealed class ServiceFuelRulesSource : IServiceFuelRules
{
    private const int FuelPerHourAttribute = 2109;
    private const int FuelToOnlineAttribute = 2110;

    private readonly IReadOnlyDictionary<int, ServiceFuelRule> _rules;

    private ServiceFuelRulesSource(IReadOnlyDictionary<int, ServiceFuelRule> rules) => _rules = rules;

    public static ServiceFuelRulesSource Load(DogmaStatic dogma) => Load(dogma,StructureServiceIds.ModuleTypeOf);

    public static async Task<ServiceFuelRulesSource> LoadAsync(
        DogmaStatic dogma,SdeReader sde,CancellationToken cancellationToken = default)
    {
        var moduleTypes=StructureServiceIds.ModuleTypeOf.ToDictionary(pair => pair.Key,pair => pair.Value);
        var typeRows=await sde.ReadAsync("types",cancellationToken:cancellationToken);
        var marketHubType=typeRows
            .Where(row => row.Doc.TryGetProperty("name",out var name) &&
                SdeReader.English(name)=="Standup Market Hub I")
            .Select(row => checked((int)row.Key))
            .SingleOrDefault();
        if (marketHubType>0)
            moduleTypes[StructureServiceIds.Market]=marketHubType;
        else
            Console.Error.WriteLine("eve-host: service 5 has no fuel rule (pinned SDE has no Standup Market Hub I type)");

        return Load(dogma,moduleTypes);
    }

    private static ServiceFuelRulesSource Load(DogmaStatic dogma,IReadOnlyDictionary<int,int> moduleTypes)
    {
        var rules=new Dictionary<int,ServiceFuelRule>();
        foreach (var (serviceId,moduleTypeId) in moduleTypes)
        {
            if (dogma.Type(moduleTypeId) is not { } type ||
                !type.Attributes.ContainsKey(FuelPerHourAttribute) ||
                !type.Attributes.ContainsKey(FuelToOnlineAttribute))
            {
                Console.Error.WriteLine($"eve-host: service {serviceId} has no fuel rule (module {moduleTypeId} is not in the static data)");
                continue;
            }
            rules[serviceId]=new ServiceFuelRule(serviceId,
                (int)type.Attributes[FuelPerHourAttribute],
                (int)type.Attributes[FuelToOnlineAttribute]);
        }
        return new ServiceFuelRulesSource(rules);
    }

    public ServiceFuelRule? RuleOf(int serviceId) => _rules.GetValueOrDefault(serviceId);
}
