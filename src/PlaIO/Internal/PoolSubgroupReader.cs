using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

/// <summary>Reads the BL-tagged records of one decoded pool. A mode-2 payload is an LZ token stream like a mode-2
/// block payload; its back-references reach into the pool stream — the reconstruction of every preceding pool of the
/// document (each subgroup's header, raw payloads as they are, zero-mask and mode-2 payloads as their full decodes,
/// concatenated in pool order), followed by this pool's own reconstruction so far. The window keeps only its tail: a
/// back-reference distance is a u16, so nothing reaches further back than <see cref="WindowTail"/> bytes and the
/// memory stays constant in the size of the file.</summary>
internal static class PoolSubgroupReader
{
    /// <summary>The window tail kept for back-references: the largest distance the format can name.</summary>
    private const int WindowTail = 1 << 16;

    /// <summary>Walks one decoded pool: parses each BL-tagged record, decodes its payload against
    /// <paramref name="window"/>, and appends the record's contribution to the window — its header bytes and its
    /// payload exactly once — so the following subgroups can reference it. A truncated zero-mask payload contributes
    /// its stored bytes. An undecodable mode-2 payload resets the history.</summary>
    internal static IReadOnlyList<PoolSubgroup> ReadPool(byte[] pool, List<byte> window)
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
            if (header.Mode == PlaBlockMode.Lz)
            {
                // The LZ decoder appends every produced byte to the window itself, so the header goes in first and
                // the payload's contribution is made exactly once — inside DecodeLz.
                window.AddRange(headerBytes);
                (data, partial) = DecodeLz(payload, header, window);
            }
            else
            {
                if (header.Mode == PlaBlockMode.Raw)
                {
                    data = payload.ToArray();
                    partial = false;
                }
                else
                {
                    data = new byte[header.UnpackedSize];
                    PayloadDecoder.DecodeZeroMask(payload, data, out _, out partial);
                }
                window.AddRange(headerBytes);
                // A truncated zero-mask literal leaves the rest of its buffer zero-filled; the window takes the raw
                // bytes instead, because that is what the saved stream addresses.
                window.AddRange(partial ? payload.ToArray() : data);
            }
            subgroups.Add(new PoolSubgroup(header, data, partial));
            if (window.Count > 4 * WindowTail) window.RemoveRange(0, window.Count - WindowTail);
            pos = (int)header.End;
        }
        return subgroups.AsReadOnly();
    }

    /// <summary>Decodes one mode-2 subgroup payload. A 13-byte header — a u32, the marker 0x47 at +4, then the packed
    /// payload length at +5 (the length including the header) and the unpacked size at +9 — precedes the token stream;
    /// without it the payload starts with the file block's 9-byte stream header or with the tokens themselves.
    /// Returns the declared unpacked buffer with a zero tail when the stream produces less than declared (partial),
    /// or empty data with partial set when the stream does not decode at all. The window receives the payload's
    /// contribution exactly once: the decoder appends the produced bytes, this method adds the declared tail of a
    /// buffer block. A failed stream resets the history so later references cannot reach across the missing output.</summary>
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
            if (produced < header.UnpackedSize)
                window.AddRange(new byte[(int)(header.UnpackedSize - produced)]); // the buffer space stays addressed
            return (output, produced < header.UnpackedSize);
        }
        catch (InvalidDataException)
        {
            window.Clear();
            return ([], true);
        }
    }
}
