using System.Buffers.Binary;
using PlaIO.Internal;

namespace PlaIO.Tests;

/// <summary>Builders of synthetic container, block, directory and .bimx bytes. Every test input is made here; no sample
/// bytes are copied into the tests.</summary>
internal static class Synthetic
{
    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The 32 fixed header bytes, then <paramref name="tail"/>.</summary>
    public static byte[] Header(ushort layout = 0x10, ushort format = 2, uint field12 = 0x01010000, byte[]? tail = null)
    {
        var header = new byte[32 + (tail?.Length ?? 0)];
        "ROF FDB "u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), layout);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), format);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), field12);
        tail?.CopyTo(header, 32);
        return header;
    }

    /// <summary>A block header followed by <paramref name="payload"/>. The flags are built from the arguments unless
    /// <paramref name="flagsOverride"/> is given; the layout (GUID, packed width, unpacked, extra) always follows the final
    /// flags. <paramref name="packed"/> declares a packed size other than the payload length.</summary>
    public static byte[] Block(PlaBlockMode mode, byte[] payload, string tag = "BL", byte ver = 0, uint? unpacked = null,
        Guid? guid = null, uint? extra = null, uint id = 0x01020304, ushort kind = 0xFDEA, ushort? flagsOverride = null,
        int? classOverride = null, ulong? packed = null)
    {
        ulong declared = packed ?? (ulong)payload.Length;
        int sizeClass = classOverride ?? SizeClass.Of(declared);
        int modeFlag = mode switch { PlaBlockMode.ZeroMask => 0x100, PlaBlockMode.Lz => 0x200, _ => 0 };
        ushort flags = flagsOverride ?? (ushort)(0x8080 | sizeClass | (guid != null ? 0x20 : 0) | modeFlag | (extra != null ? 0x400 : 0));

        var header = new List<byte>();
        header.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
        header.AddRange(Le32(id));
        header.AddRange(Le16(kind));
        header.Add(ver);
        header.AddRange(Le16(flags));
        if ((flags & 0x20) != 0) header.AddRange((guid ?? Guid.Empty).ToByteArray());
        switch (SizeClass.FieldWidth(flags & 0xF))
        {
            case 2: header.AddRange(Le16((ushort)declared)); break;
            case 4: header.AddRange(Le32((uint)declared)); break;
            default: header.AddRange(Le64(declared)); break;
        }
        if ((flags & 0x300) != 0) header.AddRange(Le32(unpacked ?? (uint)payload.Length));
        if ((flags & 0x400) != 0) header.AddRange(Le32(extra ?? 0));
        header.AddRange(payload);
        return header.ToArray();
    }

    /// <summary>Mode-1 encoding of <paramref name="plain"/>: per 8 bytes a mask with a bit per non-zero byte, then those bytes.</summary>
    public static byte[] ZeroMask(byte[] plain)
    {
        var output = new List<byte>();
        for (int group = 0; group < plain.Length; group += 8)
        {
            int maskAt = output.Count;
            output.Add(0);
            int mask = 0;
            for (int bit = 0; bit < 8 && group + bit < plain.Length; bit++)
            {
                if (plain[group + bit] == 0) continue;
                mask |= 1 << bit;
                output.Add(plain[group + bit]);
            }
            output[maskAt] = (byte)mask;
        }
        return output.ToArray();
    }

    public static byte[] Record(ObjectDirectoryRecord record)
    {
        var bytes = new byte[30];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, record.ClassNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(2), record.Position);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), record.Count);
        record.ObjectId.ToByteArray().CopyTo(bytes, 14);
        return bytes;
    }

    /// <summary><paramref name="phase"/> filler bytes, the records, then <paramref name="tail"/> zero bytes.</summary>
    public static byte[] DirectoryPayload(int phase, IEnumerable<ObjectDirectoryRecord> records, int tail = 0)
    {
        var bytes = new List<byte>(Enumerable.Repeat((byte)0xEE, phase));
        foreach (var record in records) bytes.AddRange(Record(record));
        bytes.AddRange(new byte[tail]);
        return bytes.ToArray();
    }

    /// <summary><paramref name="n"/> extents that tile a range (each ends where the next starts), with a gap after a
    /// <paramref name="gapRate"/> share of them, shuffled into file order, with random GUIDs and classes.</summary>
    public static ObjectDirectoryRecord[] Extents(int n, Random random, double gapRate = 0, ulong start = 1000)
    {
        var records = new ObjectDirectoryRecord[n];
        ulong pos = start;
        for (int i = 0; i < n; i++)
        {
            uint count = (uint)random.Next(1, 1000);
            records[i] = new ObjectDirectoryRecord((ushort)random.Next(100, 700), pos, count, RandomGuid(random));
            pos += count;
            if (random.NextDouble() < gapRate) pos += (ulong)random.Next(1, 50);
        }
        random.Shuffle(records);
        return records;
    }

    public static Guid RandomGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    public static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A container: the 32-byte header, then the parts from offset 32.</summary>
    public static byte[] Fdb(params byte[][] parts) => Header().Concat(parts.SelectMany(p => p)).ToArray();

    /// <summary>A PNG: the signature and each chunk as [BE length][type][data][CRC]; the CRC is zero (not checked).</summary>
    public static byte[] Png(params (string Type, byte[] Data)[] chunks)
    {
        var bytes = new List<byte>(PngSignature);
        foreach (var (type, data) in chunks)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            bytes.AddRange(length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            bytes.AddRange(data);
            bytes.AddRange(new byte[4]);
        }
        return bytes.ToArray();
    }

    public static byte[] SimplePng() => Png(("IHDR", new byte[13]), ("IDAT", [1, 2, 3, 4, 5]), ("IEND", []));

    public static byte[] Bimx(byte[] png, byte[] trailing) => "BIMx"u8.ToArray().Concat(png).Concat(trailing).ToArray();

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Le16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    private static byte[] Le32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private static byte[] Le64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
}
