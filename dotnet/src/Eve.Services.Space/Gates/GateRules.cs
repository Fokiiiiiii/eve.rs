using Eve.Sde;
using Eve.Universe;

namespace Eve.Services.Space.Gates;

/// <summary>
/// The stargate rules: which ship groups a gate admits (the client's <c>allowedShipsTypeListID</c> and the SDE type list), where a gate leads
/// (the SDE <c>mapStargates</c> destination) and a stargate type's alignment toll (the client's <c>alignmentBasedToll</c>).
/// </summary>
public sealed class GateRules
{
    private readonly IReadOnlyDictionary<long, int> _listOfGate;
    private readonly IReadOnlyDictionary<int, IReadOnlySet<int>> _groupsOfList;
    private readonly IReadOnlyDictionary<long, long> _destinationOf;
    private readonly IReadOnlyDictionary<long, int> _typeOfGate;
    private readonly IReadOnlyDictionary<int, AlignmentToll> _tollOfType;

    public GateRules(IReadOnlyDictionary<long, int> listOfGate, IReadOnlyDictionary<int, IReadOnlySet<int>> groupsOfList,
        IReadOnlyDictionary<long, long> destinationOf, IReadOnlyDictionary<long, int> typeOfGate, IReadOnlyDictionary<int, AlignmentToll> tollOfType)
    {
        _listOfGate = listOfGate;
        _groupsOfList = groupsOfList;
        _destinationOf = destinationOf;
        _typeOfGate = typeOfGate;
        _tollOfType = tollOfType;
    }

    /// <summary>Reads the lists and tolls of the client export named by <paramref name="clientDirectory"/> (blank: none) and the SDE's type lists and stargates.</summary>
    public static async Task<GateRules> LoadAsync(SdeReader sde, string clientDirectory, CancellationToken cancellationToken = default)
    {
        var lists = await ClientGateRules.AllowedShipListsAsync(clientDirectory, cancellationToken);
        var tolls = await ClientGateRules.TollsAsync(clientDirectory, cancellationToken);
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
        foreach (var row in await sde.ReadAsync("mapStargates", cancellationToken: cancellationToken))
        {
            types[row.Key] = row.Doc.GetProperty("typeID").GetInt32();
            if (row.Doc.TryGetProperty("destination", out var destination) && destination.TryGetProperty("stargateID", out var gate))
            {
                destinations[row.Key] = gate.GetInt64();
            }
        }
        return new GateRules(lists.ToDictionary(pair => pair.Key, pair => pair.Value), groupsOfList, destinations, types, tolls);
    }

    /// <summary>Whether a ship of this group may go through the gate. A gate with no list admits every ship.</summary>
    public bool ShipAllowed(long gateId, int shipGroupId) =>
        !_listOfGate.TryGetValue(gateId, out var list) || _groupsOfList[list].Contains(shipGroupId);

    /// <summary>The gate at the other end (the SDE destination), or null for a gate the SDE does not have.</summary>
    public long? DestinationOf(long gateId) => _destinationOf.TryGetValue(gateId, out var destination) ? destination : null;

    /// <summary>The alignment toll of a gate's type, or null when the type has none.</summary>
    public AlignmentToll? TollOf(long gateId) =>
        _typeOfGate.TryGetValue(gateId, out var type) && _tollOfType.TryGetValue(type, out var toll) ? toll : null;

    /// <summary>Whether a pilot aligned to one of the toll's factions (the session's war faction) pays the aligned multiplier.</summary>
    public static bool IsAligned(AlignmentToll toll, long? warFactionId) => warFactionId is { } faction && toll.AlignmentRestrictionList.Contains(faction);

    /// <summary>The toll for a ship of this mass, as the client computes it: the larger of the minimum and (mass times price per kilo) to the exponent plus the fixed addition, times the aligned multiplier when aligned.</summary>
    public static double TollAmount(AlignmentToll toll, double shipMass, bool aligned)
    {
        var amount = Math.Max(toll.MinCost, Math.Pow(shipMass * toll.PricePerKilo, toll.Exponent) + toll.FixedAddition);
        return aligned ? amount * toll.AlignedDiscountMultiplier : amount;
    }
}
