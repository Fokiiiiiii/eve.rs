using System.Buffers.Binary;
using System.Text;

namespace Eve.Space;

public static partial class SimCodec
{
    private static class Kind
    {
        public const byte Hello = 1;
        public const byte HelloAck = 2;
        public const byte Enter = 3;
        public const byte Leave = 4;
        public const byte Bind = 5;
        public const byte UpdateState = 6;
        public const byte Command = 7;
        public const byte ShipChanged = 8;
        public const byte Notify = 9;
        public const byte Docked = 10;
        public const byte Refused = 11;
        public const byte Accepted = 12;
        public const byte EnterReply = 13;
        public const byte Approaching = 14;
        public const byte Jumping = 15;
        public const byte Jumped = 16;
        public const byte Targets = 17;
        public const byte TargetRefused = 18;
        public const byte Activate = 19;
        public const byte Condition = 20;
        public const byte Destroyed = 21;
        public const byte Spawn = 22;
        public const byte SpawnReply = 23;
        public const byte LaunchDrone = 36;
        public const byte DroneCommand = 37;
        public const byte DroneResults = 38;
        public const byte DroneScooped = 39;
        public const byte DroneReleased = 40;
        public const byte HeatProfile = 41;
        public const byte ModuleOverheat = 42;
        public const byte ModuleOffline = 43;
        public const byte RackOverheat = 44;
        public const byte Destroy = 24;
        public const byte Respawn = 25;
        public const byte PlaceWreck = 26;
        public const byte ChargeSpent = 27;
        public const byte PlaceContainer = 28;
        public const byte ContainerPlaced = 29;
        public const byte ChangeWreck = 30;
        public const byte RemoveWreck = 31;
        public const byte Measured = 32;
        public const byte AdjustCharge = 33;
        public const byte ChargeAdjusted = 34;
        public const byte Arrived = 35;
        public const byte PlaceAsteroid = 45;
        public const byte DroneWorkCycle = 46;
        public const byte DroneWorkResult = 47;
        public const byte RemoteRepairCycle = 48;
        public const byte RemoteRepairCycleResult = 49;
        public const byte Position = 50;
        public const byte ModuleWorkCycle = 51;
        public const byte ModuleWorkResult = 52;
        public const byte ModuleEnded = 53;
        public const byte LaunchFighter = 54;
        public const byte FighterOrder = 55;
        public const byte FighterResults = 56;
        public const byte FighterLanded = 57;
        public const byte FighterReleased = 58;
        public const byte FighterGotoPoint = 59;
        public const byte FighterSize = 60;

/// <summary>Protocol 38 (FP-C4b, FP-C5): a refresh of a running effect (host to simulation).</summary>
public const byte UpdateEffect = 61;

        /// <summary>The timed buffs of a ship of this session (protocol 43, track C6), host to simulation.</summary>
        public const byte BuffState = 62;
        public const byte Deployed = 63;
        public const byte RemoveDeployed = 64;

    }

    private ref struct Reader(ReadOnlyMemory<byte> data)
    {
        private readonly ReadOnlyMemory<byte> _data = data;
        private int _at = 1; // after the kind byte

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _data.Length - _at < count)
            {
                throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
            }
            var slice = _data.Span.Slice(_at, count);
            _at += count;
            return slice;
        }

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public string Text()
        {
            var bytes = Take(U16());
            try
            {
                return Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new SimLinkException(SimLinkError.Utf8, "text is not UTF-8");
            }
        }

        public ReadOnlyMemory<byte> Bytes()
        {
            var length = U32();
            if (length > int.MaxValue || _data.Length - _at < (int)length)
            {
                throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
            }
            var slice = _data.Slice(_at, (int)length);
            _at += (int)length;
            return slice;
        }

        public int Remaining => _data.Length - _at;

        public void Finish()
        {
            if (_at != _data.Length)
            {
                throw new SimLinkException(SimLinkError.Trailing, "bytes follow the end of the message");
            }
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _out = new();

        public void U8(byte value) => _out.WriteByte(value);

        private void Put(Action<Span<byte>> write, int size)
        {
            Span<byte> buffer = stackalloc byte[size];
            write(buffer);
            _out.Write(buffer);
        }

        public void U16(ushort value) => Put(b => BinaryPrimitives.WriteUInt16LittleEndian(b, value), 2);

        public void U32(uint value) => Put(b => BinaryPrimitives.WriteUInt32LittleEndian(b, value), 4);

        public void I32(int value) => Put(b => BinaryPrimitives.WriteInt32LittleEndian(b, value), 4);

        public void U64(ulong value) => Put(b => BinaryPrimitives.WriteUInt64LittleEndian(b, value), 8);

        public void I64(long value) => Put(b => BinaryPrimitives.WriteInt64LittleEndian(b, value), 8);

        public void F64(double value) => Put(b => BinaryPrimitives.WriteDoubleLittleEndian(b, value), 8);

        public void Text(string text)
        {
            var bytes = Strict.GetBytes(text);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("text is longer than a u16 length allows", nameof(text));
            }
            U16((ushort)bytes.Length);
            _out.Write(bytes);
        }

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            U32((uint)bytes.Length);
            _out.Write(bytes);
        }

        public byte[] ToArray() => _out.ToArray();
    }
}
