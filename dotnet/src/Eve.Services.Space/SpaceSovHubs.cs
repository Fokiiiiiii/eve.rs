using Eve.Dogma;
using Eve.Services.Core;
using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space;

/// <summary>A system's Sovereignty Hub as the sovereignty records have it: the hub's item, its owner (null for a neutral hub) and its system.</summary>
public sealed record SovHubBall(long HubId, int SolarSystemId, long? CorporationId, long? AllianceId);

/// <summary>
/// ST-9 (a): a system's Sovereignty Hub (type 32458, group 1012) is a structure ball in the simulation once a pilot comes into the system, as the
/// sovereignty records have it: the system's infrastructure hub, when its structure row is a Sovereignty Hub. The hub is inert: nothing in the simulation
/// acts on it until an entosis link targets it (ST-9 b). The ball sits at the system's origin, as the ESS ball does (DEVIATIONS "Sovereignty Hub ball").
/// </summary>
public sealed class SpaceSovHubs
{
    /// <summary>The Sovereignty Hub (SDE type 32458; the infrastructure hub of EVE.js's sovereignty).</summary>
    public const int TypeSovereigntyHub = 32458;

    // the attributes of a structure's points (SDE dogma attributes), as SpaceStructures reads them
    private const int ShieldCapacity = 263, ArmorHitPoints = 265, HullPoints = 9;

    // the hub's typeDogma has no radius attribute (162); the SDE's types.radius of 32458 is 45 000 m (pinned build 3396210)
    private const double SovereigntyHubRadiusMeters = 45_000d;

    private readonly ISimulation _sim;
    private readonly DogmaStatic _statics;
    private readonly Func<int, CancellationToken, Task<SovHubBall?>> _readHub;
    private readonly Action<string>? _log;

    /// <param name="readHub">The hub of a system as the sovereignty records have it (null for none): the host reads them with <see cref="HubOf"/>.</param>
    public SpaceSovHubs(ISimulation sim, SpaceSessions space, DogmaStatic statics, Func<int, CancellationToken, Task<SovHubBall?>> readHub, Action<string>? log = null)
    {
        _sim = sim;
        _statics = statics;
        _readHub = readHub;
        _log = log;
        space.SystemEntered += system => space.Background(() => RestoreAsync(system));
    }

    /// <summary>
    /// The Sovereignty Hub of a system in the sovereignty records: the system's infrastructure hub, whose structure row names the type 32458. Null when the
    /// system has no hub, or the hub's row is another type (a TCU or an Infrastructure Hub is not this ball).
    /// </summary>
    public static SovHubBall? HubOf(SovFwReadSnapshot snapshot, int solarSystemId)
    {
        if (!snapshot.HubsBySolarSystemId.TryGetValue(solarSystemId, out var hub) || hub.HubId is not > 0)
            return null;
        var row = snapshot.StructuresBySolarSystemId.TryGetValue(solarSystemId, out var structures)
            ? structures.FirstOrDefault(structure => structure.ItemId == hub.HubId)
            : null;
        return row is { TypeId: TypeSovereigntyHub }
            ? new SovHubBall(hub.HubId.Value, solarSystemId, hub.CorporationId, hub.AllianceId)
            : null;
    }

    /// <summary>
    /// Puts the hub of a system in space. Idempotent: the simulation ignores a second placement of the same item. A failure is logged and the system goes on
    /// without the ball (the hub is not a reason to refuse the pilot's entry).
    /// </summary>
    public async Task RestoreAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _readHub(solarSystemId, cancellationToken) is not { } hub)
                return;
            await _statics.EnsureTypesAsync([TypeSovereigntyHub], cancellationToken);
            if (_statics.Type(TypeSovereigntyHub) is { } type)
                _sim.PlaceStructure(Placement(hub, type));
            else
                _log?.Invoke($"sovereignty hub {hub.HubId} of system {solarSystemId} is not in the static data and is not put in space");
        }
        catch (Exception error)
        {
            _log?.Invoke($"the sovereignty hub of system {solarSystemId} was not put in space: {error.Message}");
        }
    }

    private static SimPlaceStructure Placement(SovHubBall hub, Eve.Dogma.TypeDef type)
    {
        var attributes = type.Attributes;
        double Attribute(int id) => attributes.GetValueOrDefault(id, 0d);
        // the hub has no hit model in the simulation (entosis only): its points are the static data's, as the ESS ball's are
        var health = new SimStructureHealth(Attribute(ShieldCapacity), Attribute(ArmorHitPoints), Attribute(HullPoints), [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0, [0, 0, 0], [0, 0, 0]);
        var corporation = hub.CorporationId ?? 0;
        return new SimPlaceStructure(
            hub.HubId, hub.SolarSystemId, type.Name, TypeSovereigntyHub, type.GroupId, [0d, 0d, 0d], SovereigntyHubRadiusMeters,
            corporation, (int)corporation, (int)(hub.AllianceId ?? 0), health,
            new SimStructureStatus(StructureState.ShieldVulnerable, UpkeepState.FullPower, null, null, null));
    }
}
