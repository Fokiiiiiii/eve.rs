using Eve.Dogma;
using Eve.Structures;

namespace Eve.Host;

/// <summary>
/// The fuel of each structure service, from the SDE (pinned build, typeDogma of the service module): the hourly burn (attribute 2109) and the online charge (2110,

/// its service cannot go online (a named refusal), and the server still starts.
/// </summary>
public sealed class ServiceFuelRulesSource : IServiceFuelRules
{
    private const int FuelPerHourAttribute = 2109;
    private const int FuelToOnlineAttribute = 2110;

    private readonly IReadOnlyDictionary<int, ServiceFuelRule> _rules;

    private ServiceFuelRulesSource(IReadOnlyDictionary<int, ServiceFuelRule> rules) => _rules = rules;

    public static ServiceFuelRulesSource Load(DogmaStatic dogma)
    {
        var rules = new Dictionary<int, ServiceFuelRule>();
        foreach (var (serviceId, moduleTypeId) in StructureServiceIds.ModuleTypeOf)
        {
            if (dogma.Type(moduleTypeId) is not { } type || !type.Attributes.ContainsKey(FuelPerHourAttribute) || !type.Attributes.ContainsKey(FuelToOnlineAttribute))
            {
                Console.Error.WriteLine($"eve-host: service {serviceId} has no fuel rule (module {moduleTypeId} is not in the static data)");
                continue;
            }
            rules[serviceId] = new ServiceFuelRule(serviceId, (int)type.Attributes[FuelPerHourAttribute], (int)type.Attributes[FuelToOnlineAttribute]);
        }
        return new ServiceFuelRulesSource(rules);
    }

    public ServiceFuelRule? RuleOf(int serviceId) => _rules.GetValueOrDefault(serviceId);
}
