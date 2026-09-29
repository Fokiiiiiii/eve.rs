namespace Eve.Space;

/// <summary>A message on the link between the service host and the simulation (docs/adr/0007-space-simulation-process.md).</summary>
public abstract record SimMessage;

public sealed record SimHello(ushort Version) : SimMessage;

public sealed record SimHelloAck(ushort Version, uint PollHz) : SimMessage;

/// <summary>How a ship comes into space.</summary>
public enum EnterHow : byte
{
    /// <summary>It undocks from a station.</summary>
    Undock = 0,

    /// <summary>It is already in space (a login in space, a jump arrival).</summary>
    InSpace = 1,
}

/// <summary>A fitted module, as the ship's slim item lists it.</summary>
public sealed record SimModule(long ItemId, int TypeId, int Flag);

/// <summary>What motion and the slim item need to know of a ship: the host works these out (dogma, skills, fitting), the simulation only uses them.</summary>
public sealed record SimShip(
    long ShipId, long CharacterId, int TypeId, int GroupId, int CategoryId, string Name, long OwnerId, int CorporationId, int AllianceId, int WarFactionId,
    double SecurityStatus, double Radius, double Mass, double MaxVelocity, double Agility, double WarpSpeedAu, double ShieldRechargeMs, IReadOnlyList<SimModule> Modules);

/// <summary>A ship enters a solar system.</summary>
public sealed record SimEnter(ulong SessionId, int SolarSystemId, EnterHow How, long StationId, SimShip Ship) : SimMessage;

/// <summary>Whether an <see cref="SimEnter"/> was done: <see cref="Error"/> is null when it was, the reason when it was not.</summary>
public sealed record SimEnterReply(ulong SessionId, string? Error) : SimMessage;

/// <summary>Why a ship leaves a scene.</summary>
public enum LeaveWhy : byte
{
    Docked = 0,
    LoggedOff = 1,
    Jumped = 2,
}

public sealed record SimLeave(ulong SessionId, LeaveWhy Why) : SimMessage;

/// <summary>The client bound the solar system's ballpark: send it the scene.</summary>
public sealed record SimBind(ulong SessionId) : SimMessage;

/// <summary>The client asked for the state again (<c>UpdateStateRequest</c>).</summary>
public sealed record SimUpdateState(ulong SessionId) : SimMessage;

/// <summary>The codes of the commands a pilot gives.</summary>
public static class SimCommands
{
    public const byte SetSpeedFraction = 1;
    public const byte GotoDirection = 2;
    public const byte Stop = 3;
    public const byte Dock = 4;
    public const byte FollowBall = 5;
    public const byte Orbit = 6;
    public const byte AlignTo = 7;
    public const byte WarpTo = 8;
}

public sealed record SimCommand(ulong SessionId, byte Code, IReadOnlyList<double> Args) : SimMessage;

/// <summary>New numbers for a ship in space.</summary>
public sealed record SimShipChanged(ulong SessionId, SimShip Ship) : SimMessage;

/// <summary>A notification the simulation has for a player's client: the host numbers it and sends it. The arguments are a marshalled tuple.</summary>
public sealed record SimNotify(ulong SessionId, string Name, string IdType, ReadOnlyMemory<byte> Args) : SimMessage;

/// <summary>The ship has arrived and the docking is done in space: the host moves the character.</summary>
public sealed record SimDocked(ulong SessionId, long StationId) : SimMessage;

/// <summary>A command the scene cannot do.</summary>
public sealed record SimRefused(ulong SessionId, byte Command, string Reason) : SimMessage;

/// <summary>A command was accepted, after the update it made. A <c>Dock</c> is answered with the time (a FILETIME).</summary>
public sealed record SimAccepted(ulong SessionId, byte Command, long At) : SimMessage;

/// <summary>A <c>Dock</c> could not be done from where the ship is: it now approaches the station, <paramref name="Distance"/> metres off its surface.</summary>
public sealed record SimApproaching(ulong SessionId, byte Command, double Distance) : SimMessage;
