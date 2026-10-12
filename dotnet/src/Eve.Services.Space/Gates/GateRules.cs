using Eve.Sde;
using Eve.Universe;

namespace Eve.Services.Space.Gates;

/// <summary>
/// The stargate rules: which ship groups a gate admits (the client's <c>allowedShipsTypeListID</c> and the SDE type list), where a gate leads
/// (the SDE <c>mapStargates</c> destination) and a stargate type's alignment toll (the client's <c>alignmentBasedToll</c>).
/// </summary>
/// <summary>A stargate's place: its solar system, type and position in metres (the static data's).</summary>
public sealed record GateSite(long GateId, int SolarSystemId, int TypeId, double X, double Y, double Z);

/// <summary>What a deploy blocker keeps from being anchored near a stargate: a warp bubble or a structure (the client's deployBlocker).</summary>
public enum AnchorKind { WarpBubble, Structure }

public sealed class GateRules
{
    /// <summary>The journal ref type of the alignment-based gate toll (the client's <c>refAllignmentBasedGateToll</c>, appConst 168).</summary>
    public const int AlignmentBasedGateTollRefType = 168;

    private readonly IReadOnlyDictionary<long, int> _listOfGate;
    private readonly IReadOnlyDictionary<int, IReadOnlySet<int>> _groupsOfList;
    private readonly IReadOnlyDictionary<long, long> _destinationOf;
    private readonly IReadOnlyDictionary<long, int> _typeOfGate;
    private readonly IReadOnlyDictionary<int, AlignmentToll> _tollOfType;
    private readonly IReadOnlyDictionary<int, DeployBlocker> _blockerOfType;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<GateSite>> _blockingGatesOfSystem;

    public GateRules(IReadOnlyDictionary<long, int> listOfGate, IReadOnlyDictionary<int, IReadOnlySet<int>> groupsOfList,
        IReadOnlyDictionary<long, long> destinationOf, IReadOnlyDictionary<long, int> typeOfGate, IReadOnlyDictionary<int, AlignmentToll> tollOfType,
        IReadOnlyDictionary<int, DeployBlocker>? blockerOfType = null, IEnumerable<GateSite>? sites = null)
    {
        _listOfGate = listOfGate;
        _groupsOfList = groupsOfList;
        _destinationOf = destinationOf;
        _typeOfGate = typeOfGate;
        _tollOfType = tollOfType;
        _blockerOfType = blockerOfType ?? new Dictionary<int, DeployBlocker>();
        // only the gates of a type with a deploy blocker are kept, by system
        _blockingGatesOfSystem = (sites ?? Array.Empty<GateSite>()).Where(site => _blockerOfType.ContainsKey(site.TypeId))
            .GroupBy(site => site.SolarSystemId).ToDictionary(group => group.Key, group => (IReadOnlyList<GateSite>)group.ToList());
    }

    /// <summary>Reads the lists and tolls of the client export named by <paramref name="clientDirectory"/> (blank: none) and the SDE's type lists and stargates.</summary>
    public static async Task<GateRules> LoadAsync(SdeReader sde, string clientDirectory, CancellationToken cancellationToken = default)
    {
        var lists = await ClientGateRules.AllowedShipListsAsync(clientDirectory, cancellationToken);
        var tolls = await ClientGateRules.TollsAsync(clientDirectory, cancellationToken);
        var blockers = await ClientGateRules.DeployBlockersAsync(clientDirectory, cancellationToken);
        var listIds = lists.Values.Distinct().Select(id => (long)id).ToArray();
        var groupsOfList = new Dictionary<int, IReadOnlySet<int>>();
        foreach (var row in await sde.ReadAsync("typeLists", listIds, cancellationToken: cancellationToken))
        {
            groupsOfList[(int)row.Key] = row.Doc.GetProperty("includedGroupIDs").EnumerateArray().Select(group => group.GetInt32()).ToHashSet();
        }
        foreach (var list in listIds)
        {
            if (!groupsOfList.ContainsKey((int)list))
            {
                throw new InvalidOperationException($"the SDE has no type list {list}, which a stargate names");
            }
        }
        var destinations = new Dictionary<long, long>();
        var types = new Dictionary<long, int>();
        var sites = new List<GateSite>();
        foreach (var row in await sde.ReadAsync("mapStargates", cancellationToken: cancellationToken))
        {
            var type = row.Doc.GetProperty("typeID").GetInt32();
            types[row.Key] = type;
            if (row.Doc.TryGetProperty("destination", out var destination) && destination.TryGetProperty("stargateID", out var gate))
            {
                destinations[row.Key] = gate.GetInt64();
            }
            if (blockers.ContainsKey(type))
            {
                var position = row.Doc.GetProperty("position");
                sites.Add(new GateSite(row.Key, row.Doc.GetProperty("solarSystemID").GetInt32(), type,
                    position.GetProperty("x").GetDouble(), position.GetProperty("y").GetDouble(), position.GetProperty("z").GetDouble()));
            }
        }
        return new GateRules(lists.ToDictionary(pair => pair.Key, pair => pair.Value), groupsOfList, destinations, types, tolls, blockers, sites);
    }

    /// <summary>Whether a ship of this group may go through the gate. A gate with no list admits every ship.</summary>
    public bool ShipAllowed(long gateId, int shipGroupId) =>
        !_listOfGate.TryGetValue(gateId, out var list) || _groupsOfList[list].Contains(shipGroupId);

    /// <summary>The gate at the other end (the SDE destination), or null for a gate the SDE does not have.</summary>
    public long? DestinationOf(long gateId) => _destinationOf.TryGetValue(gateId, out var destination) ? destination : null;

    /// <summary>The alignment toll of a gate's type, or null when the type has none.</summary>
    public AlignmentToll? TollOf(long gateId) =>
        _typeOfGate.TryGetValue(gateId, out var type) && _tollOfType.TryGetValue(type, out var toll) ? toll : null;

    /// <summary>
    /// The stargate whose deploy blocker keeps an anchor of this kind from this point (within the blocker's range, the client's
    /// "Cannot be anchored due to gravitational anomalies in the vicinity of an ancient Jovian stargate."), or null.
    /// </summary>
    public GateSite? GateBlocking(int solarSystemId, double x, double y, double z, AnchorKind kind)
    {
        if (!_blockingGatesOfSystem.TryGetValue(solarSystemId, out var gates))
        {
            return null;
        }
        foreach (var gate in gates)
        {
            var blocker = _blockerOfType[gate.TypeId];
            var range = kind == AnchorKind.WarpBubble ? blocker.BubbleRange : blocker.StructureRange;
            var (dx, dy, dz) = (gate.X - x, gate.Y - y, gate.Z - z);
            if (range > 0 && Math.Sqrt(dx * dx + dy * dy + dz * dz) <= range)
            {
                return gate;
            }
        }
        return null;
    }

    /// <summary>Whether a pilot aligned to one of the toll's factions (the session's war faction) pays the aligned multiplier.</summary>
    public static bool IsAligned(AlignmentToll toll, long? warFactionId) => warFactionId is { } faction && toll.AlignmentRestrictionList.Contains(faction);

    /// <summary>The toll for a ship of this mass, as the client computes it: the larger of the minimum and (mass times price per kilo) to the exponent plus the fixed addition, times the aligned multiplier when aligned.</summary>
    public static double TollAmount(AlignmentToll toll, double shipMass, bool aligned)
    {
        var amount = Math.Max(toll.MinCost, Math.Pow(shipMass * toll.PricePerKilo, toll.Exponent) + toll.FixedAddition);
        return aligned ? amount * toll.AlignedDiscountMultiplier : amount;
    }
}
