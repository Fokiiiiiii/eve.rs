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
        var radius = await FlownShipAsync(pilot, cancellationToken) is { } snapshot ? snapshot.World.Get(pilot.ShipId, SimShips.Radius) : 0d;
        return Math.Max(0d, centres - Math.Max(0d, radius));
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
