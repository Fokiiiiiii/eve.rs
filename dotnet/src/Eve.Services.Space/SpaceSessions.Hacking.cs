using Eve.Ships;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{

    public const string AnalyzerEffectName = "doHacking";

    private const int AccessDifficultyBonus = 902;
    private const int VirusCoherence = 1909;
    private const int VirusStrength = 1910;
    private const int VirusElementSlots = 1911;
    private const int DataHackingSkill = 21718;
    private const int ArchaeologySkill = 13278;

    /// <summary>The site containers an analyzer hacks (the target lookup and the end of an attempt). Null without a space simulation.</summary>
    public SiteContainerHacks? HackableContainers { get; set; }

    /// <summary>The hacking service: the analyzer's refusal and open, and the window's warp close. Set by the host.</summary>
    public HackingMgrService? HackingService { get; set; }

    /// <summary>Checks the authoritative pilot, scene, lock and surface range before an analyzer reserves a site container.</summary>
    public async ValueTask<string?> AnalyzerStartRefusalAsync(
        ClientSession session, HackableTarget target, double maxRange, CancellationToken cancellationToken)
    {
        var sessionId = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(sessionId, out var pilot) || pilot.CharacterId != session.CharacterId
            || pilot.SolarSystemId != target.SolarSystemId
            || target.SolarSystemId is < int.MinValue or > int.MaxValue
            || pilot.SimulationScene != SimSceneKey.SolarSystem((int)target.SolarSystemId))
        {
            return "TARGET_NOT_FOUND";
        }

        if (!_lockedTargets.TryGetValue(sessionId, out var lockedTargets) || !lockedTargets.Contains(target.ItemId))
            return "TARGET_NOT_LOCKED";
        if (!double.IsFinite(maxRange) || maxRange <= 0)
            return "ANALYZER_RANGE_UNAVAILABLE";

        var distance = await SurfaceDistanceToItemAsync(session, target.ItemId, cancellationToken);
        if (distance is null)
            return "TARGET_NOT_FOUND";
        return distance > maxRange + 1 ? "TARGET_OUT_OF_RANGE" : null;
    }

    /// <summary>The surface distance from the pilot's ship to an item of the scene, or null when either is not in it.</summary>
    public async Task<double?> SurfaceDistanceToItemAsync(ClientSession session, long itemId, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        if (await PositionOfAsync(session, pilot.ShipId, cancellationToken) is not { } own
            || await PositionOfAsync(session, itemId, cancellationToken) is not { } other)
        {
            return null;
        }
        var centres = Math.Sqrt(Math.Pow(own.X - other.X, 2) + Math.Pow(own.Y - other.Y, 2) + Math.Pow(own.Z - other.Z, 2));
        var shipRadius = own.Radius
            ?? (await FlownShipAsync(pilot, cancellationToken) is { } snapshot ? snapshot.World.Get(pilot.ShipId, SimShips.Radius) : 0d);
        return Math.Max(0d, centres - Math.Max(0d, shipRadius) - Math.Max(0d, other.Radius ?? 0d));
    }

    private AnalyzerValues AnalyzerValuesOf(ShipSnapshot snapshot, long moduleId, int moduleTypeId)
    {
        var type = _statics.Type(moduleTypeId);
        var skills = new HashSet<int>();
        foreach (var name in new[] { "requiredSkill1", "requiredSkill2" })
        {
            if (_statics.AttributeId(name) is { } attribute && type?.Attributes.TryGetValue(attribute, out var skill) == true)
            {
                skills.Add((int)skill);
            }
        }
        return new AnalyzerValues(
            moduleTypeId,
            CanHackData: skills.Contains(DataHackingSkill),
            CanHackRelic: skills.Contains(ArchaeologySkill),
            MaxRange: snapshot.World.Get(moduleId, MaxRange),
            VirusCoherence: snapshot.World.Get(moduleId, VirusCoherence),
            VirusStrength: snapshot.World.Get(moduleId, VirusStrength),
            VirusSlots: snapshot.World.Get(moduleId, VirusElementSlots),
            AccessDifficultyBonus: snapshot.World.Get(moduleId, AccessDifficultyBonus));
    }
}
