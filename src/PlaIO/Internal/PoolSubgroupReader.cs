using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

/// <summary>Reads the BL-tagged records of one decoded pool. A mode-2 payload is an LZ token stream like a mode-2
/// block payload; its back-references reach into the pool stream — the reconstruction of every preceding pool of the
/// document (each subgroup's header, raw payloads as they are, zero-mask and mode-2 payloads as their full decodes,
/// concatenated in pool order), followed by the requested pool's own reconstruction so far. The pools are therefore
/// decoded in order until the requested one, and the cost of a read grows with the file. The window keeps only its
/// tail: a back-reference distance is a u16, so nothing reaches further back than
/// <see cref="WindowTail"/> bytes and the memory stays constant in the size of the file.</summary>
internal static class PoolSubgroupReader
{
    /// <summary>The window tail kept for back-references: the largest distance the format can name.</summary>
    private const int WindowTail = 1 << 16;

    public static IReadOnlyList<PoolSubgroup> Read(PlaDocument document, CompressedPool through)
    {
        var window = new List<byte>();
        foreach (var pool in document.FindCompressedPools())
        {
            var subgroups = ReadPool(pool.Decode(), window);
            if (ReferenceEquals(pool, through)) return subgroups;
        }
        throw new ArgumentException("The pool is not one of the compressed pools of this document.", nameof(through));
    }

    /// <summary>Walks one decoded pool: parses each BL-tagged record, decodes its payload against
    /// <paramref name="window"/>, and appends the record's reconstruction (header plus decoded payload) to the window
    /// so the following subgroups can reference it.</summary>
    private static IReadOnlyList<PoolSubgroup> ReadPool(byte[] pool, List<byte> window)
    {
        var subgroups = new List<PoolSubgroup>();
        int pos = 0;
        while (pool.Length - pos >= FdbFormat.MinBlockHeaderLength)
        {
            int next = pool.AsSpan(pos).IndexOf("BL"u8);
            if (next < 0) break;
            pos += next;
            if (!BlockHeaderParser.TryParse(pool.AsSpan(pos), pos, pool.Length, out var header, poolSubgroup: true))
            {
                pos++;
                continue;
            }

            ReadOnlySpan<byte> payload = pool.AsSpan((int)header.PayloadOffset, (int)header.PackedSize);
            ReadOnlySpan<byte> headerBytes = pool.AsSpan(pos, (int)header.PayloadOffset - pos);
            byte[] data;
            bool partial;
            switch (header.Mode)
            {
                case PlaBlockMode.Raw:
                    data = payload.ToArray();
                    partial = false;
                    break;
                case PlaBlockMode.ZeroMask:
                    data = new byte[header.UnpackedSize];
                    PayloadDecoder.DecodeZeroMask(payload, data, out _, out partial);
                    break;
                default:
                    (data, partial) = DecodeLz(payload, header, window);
                    break;
            }
            subgroups.Add(new PoolSubgroup(header, data, partial));

            // The window holds the reconstruction: the header bytes, then the decoded payload — except for a payload
            // that does not fully decode (a truncated zero-mask literal, a mode-2 stream that fails), where the raw
            // bytes keep the distances of the following subgroups plausible. Only the tail is kept; a distance
            // cannot reach further back anyway.
            window.AddRange(headerBytes);
            window.AddRange(partial && data.Length != header.UnpackedSize ? payload.ToArray() : data);
            if (window.Count > 4 * WindowTail) window.RemoveRange(0, window.Count - WindowTail);
            pos = (int)header.End;
        }
        return subgroups.AsReadOnly();
    }

    /// <summary>Decodes one mode-2 subgroup payload. A 13-byte header — a u32, the marker 0x47 at +4, then the packed
    /// payload length at +5 (the length including the header) and the unpacked size at +9 — precedes the token stream;
    /// without it the payload starts with the file block's 9-byte stream header or with the tokens themselves.
    /// Returns the declared unpacked buffer with a zero tail when the stream produces less than declared (partial),
    /// or empty data with partial set when the stream does not decode at all.</summary>
    private static (byte[] Data, bool Partial) DecodeLz(ReadOnlySpan<byte> payload, PlaBlock header, List<byte> window)
    {
        bool hasPoolHeader = payload.Length > 13 && payload[4] == 0x47
            && BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]) == (uint)header.PackedSize
            && BinaryPrimitives.ReadUInt32LittleEndian(payload[9..]) == (uint)header.UnpackedSize;
        ReadOnlySpan<byte> body = hasPoolHeader ? payload[13..] : payload;

        var output = new byte[header.UnpackedSize];
        try
        {
            LzPayloadDecoder.Decode(body, output, window, header.Offset, out int produced);
            return (output, produced < header.UnpackedSize);
        }
        catch (InvalidDataException)
        {
            // The stream does not decode; the caller keeps the raw payload in the window so the distances of the
            // following subgroups stay plausible.
            return ([], true);
        }
    }
}
