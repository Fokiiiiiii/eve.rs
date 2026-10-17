using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Eve.Dungeons;

/// <summary>A room of a dungeon site in its system: its order (0 is the entry), its anchor, and the point a pilot warps to on entering it.</summary>
public sealed record RoomPlacement(int Index, string Key, long? RoomId, double[] Anchor, double[] EntryPoint);

/// <summary>
/// An acceleration gate of a dungeon: <paramref name="GateIndex"/> is its place in the dungeon's gate list (its id within the instance), the room it leads
/// to, the ids it may be used by (<paramref name="AllowedRaces"/>, empty for none), and its position in the system.
/// </summary>
public sealed record GatePlacement(int GateIndex, string Key, long TypeId, string DestinationRoomKey, IReadOnlyList<long> AllowedRaces, double[] Position);

/// <summary>The rooms and the placed gates of one dungeon instance in its system.</summary>
public sealed record DungeonLayout(IReadOnlyList<RoomPlacement> Rooms, IReadOnlyList<GatePlacement> Gates)
{
    /// <summary>The room with the key, or null when the dungeon has none.</summary>
    public RoomPlacement? Room(string key) => Rooms.FirstOrDefault(room => room.Key == key);
}

/// <summary>
/// The rooms and gates of a dungeon site in its system (docs/project/ACCELERATION_GATES.md). The client data gives a site's rooms, which room each gate leads
/// to, and the gate type, but no coordinates: the placement is a rule, deterministic per site, so a restart places them where they were. The distances are
/// EveJS's (<c>keeperService.js</c>: a room is 1 000 km out and 400 km further for each room after the first; a gate sits 22 km from its room's anchor; a pilot
/// arrives 15 km out from the room's anchor, away from the site), and the offsets that EveJS hashes are spread evenly instead.
/// </summary>
public static class DungeonRoomLayout
{
    /// <summary>The acceleration gate type (the client's gate, group 366). A gate of another type is not placed (see the owner data's gaps).</summary>
    public const long AccelerationGateTypeId = 17_831;

    /// <summary>The distance of the first room from the site, metres (EveJS <c>ROOM_ENTRY_BASE_DISTANCE_METERS</c>).</summary>
    public const double RoomBaseDistanceMeters = 1_000_000;

    /// <summary>How much further each room after the first is from the site, metres (EveJS <c>ROOM_ENTRY_DISTANCE_STEP_METERS</c>).</summary>
    public const double RoomDistanceStepMeters = 400_000;

    /// <summary>The vertical step of a room from the site per room, metres (EveJS <c>ROOM_ENTRY_VERTICAL_STEP_METERS</c>).</summary>
    public const double RoomVerticalStepMeters = 10_000;

    /// <summary>How far a gate is from the anchor of the room it is in, metres (EveJS <c>buildContentOffset</c> base distance).</summary>
    public const double GateRingMeters = 22_000;

    /// <summary>How far in front of its room's anchor a pilot arrives, away from the site, metres.</summary>
    public const double EntryOffsetMeters = 15_000;

    // the golden angle: rooms spread around the site without two of them on one bearing
    private const double GoldenAngle = 2.399963229728653;

    /// <summary>Lays out a dungeon's rooms and gates around <paramref name="origin"/> (the site's position in its system, metres).</summary>
    public static DungeonLayout Build(Dungeon dungeon, double[] origin)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Length != 3)
        {
            throw new ArgumentException("the origin has three coordinates", nameof(origin));
        }
        var ordered = dungeon.Rooms.OrderBy(room => room.Entry ? 0 : 1).ThenBy(room => SuffixOf(room.Key)).ToList();
        var rooms = new RoomPlacement[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            var anchor = AnchorOf(index, origin);
            rooms[index] = new RoomPlacement(index, ordered[index].Key, ordered[index].RoomID, anchor, EntryPointOf(anchor, origin));
        }
        var indexOf = rooms.ToDictionary(room => room.Key, room => room.Index, StringComparer.Ordinal);

        // a gate is in the room before the one it leads to, and the site's own ring for the first room
        var placed = new List<(int GateIndex, DungeonGate Gate, int Source)>();
        for (var gateIndex = 0; gateIndex < dungeon.Gates.Count; gateIndex++)
        {
            var gate = dungeon.Gates[gateIndex];
            if (gate.TypeID != AccelerationGateTypeId || gate.DestinationRoomKey is null || !indexOf.TryGetValue(gate.DestinationRoomKey, out var destination))
            {
                continue;
            }
            placed.Add((gateIndex, gate, Math.Max(0, destination - 1)));
        }
        var perSource = placed.GroupBy(entry => entry.Source).ToDictionary(group => group.Key, group => group.Count());
        var seen = new Dictionary<int, int>();
        var gates = new GatePlacement[placed.Count];
        for (var i = 0; i < placed.Count; i++)
        {
            var (gateIndex, gate, source) = placed[i];
            var ordinal = seen.GetValueOrDefault(source);
            seen[source] = ordinal + 1;
            var angle = 2 * Math.PI * ordinal / perSource[source];
            var anchor = rooms[source].Anchor;
            var position = new[]
            {
                anchor[0] + Math.Cos(angle) * GateRingMeters,
                anchor[1],
                anchor[2] + Math.Sin(angle) * GateRingMeters,
            };
            gates[i] = new GatePlacement(gateIndex, gate.Key, AccelerationGateTypeId, gate.DestinationRoomKey!, gate.AllowedRaces, position);
        }
        return new DungeonLayout(rooms, gates);
    }

    /// <summary>The anchor of the room at <paramref name="index"/>: the site for the first room, and a bearing and distance from the site for the others.</summary>
    public static double[] AnchorOf(int index, double[] origin)
    {
        if (index <= 0)
        {
            return [origin[0], origin[1], origin[2]];
        }
        var bearing = index * GoldenAngle;
        var distance = RoomBaseDistanceMeters + (index - 1) * RoomDistanceStepMeters;
        var vertical = (index % 2 == 0 ? 1 : -1) * RoomVerticalStepMeters * index;
        return
        [
            origin[0] + Math.Cos(bearing) * distance,
            origin[1] + vertical,
            origin[2] + Math.Sin(bearing) * distance,
        ];
    }

    /// <summary>The point a pilot arrives at in a room: 15 km out from its anchor, on the side away from the site.</summary>
    public static double[] EntryPointOf(double[] anchor, double[] origin)
    {
        var dx = anchor[0] - origin[0];
        var dy = anchor[1] - origin[1];
        var dz = anchor[2] - origin[2];
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var (ux, uy, uz) = length > 0 ? (dx / length, dy / length, dz / length) : (1d, 0d, 0d);
        return [anchor[0] + ux * EntryOffsetMeters, anchor[1] + uy * EntryOffsetMeters, anchor[2] + uz * EntryOffsetMeters];
    }

    // the number after the last colon of a room key ("room:13850" is 13850); the entry has none and sorts first
    private static long SuffixOf(string key)
    {
        var text = key[(key.LastIndexOf(':') + 1)..];
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
