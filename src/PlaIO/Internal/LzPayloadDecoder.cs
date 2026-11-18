using System.Buffers.Binary;

namespace PlaIO.Internal;

/// <summary>Decodes the mode 2 (LZ) token stream of one block payload and appends every produced byte to the window —
/// the global decompressed stream of the whole file. Back-references reach anywhere inside that stream, including the
/// output of preceding blocks. Returns the number of body bytes consumed.</summary>
/// <remarks>
/// <para>A 9-byte stream header has a 0x46 or 0x47 marker and two little-endian sizes matching the packed and
/// unpacked lengths. The token stream follows the header.</para>
/// <para>The stream is a chain of sequences. A sequence starts with a token byte: the high nibble is the number of
/// literal bytes that follow (15 = extended: the following bytes are added while they equal 255). After the literals, if
/// the output is not complete yet, come the match fields: a 16-bit big-endian distance and a length byte. A distance of 0
/// emits the match length as zero bytes (RLE); a positive distance copies the match length from that many bytes back in
/// the global window, byte by byte, so overlapping copies replicate a repeating pattern. The low nibble of the token is
/// the high byte of the match length, so a match spans up to 4095 bytes (a length byte of 0 with a non-zero high nibble
/// still means 256). When the output is full the sequence ends; the remaining bytes of the body are slack the writer
/// reserved and are not consumed.</para>
/// <para>Two endings depart from the strict scheme. A final match may omit the length byte: the body ends after the
/// distance, and the match length is the remainder of the output. And some blocks (repeating kinds, sizes near whole
/// mebibytes) declare an output buffer larger than the stream actually produces: the stream simply ends with the body,
/// and the real output is what was produced so far. Such a block is detected by retrying with this relaxed ending when
/// the strict decode runs out of body; the produced count then fills only part of the output and the caller pads the
/// rest (the directory addresses the buffer space, so the block's contribution to the global window is the declared
/// size). A back-reference that reaches beyond the decoded stream is always an error.</para>
/// </remarks>
internal static class LzPayloadDecoder
{
    /// <summary>Decodes one mode 2 payload. Returns the consumed source length; <paramref name="produced"/> receives the
    /// number of output bytes the stream actually generated (equal to <c>output.Length</c> except for a buffer block).</summary>
    public static int Decode(ReadOnlySpan<byte> body, Span<byte> output, List<byte> window, long blockOffset, out int produced)
    {
        var result = DecodeAttempt(body, output, window, blockOffset, bufferMode: false);
        if (!result.ok)
        {
            result = DecodeAttempt(body, output, window, blockOffset, bufferMode: true);
            if (!result.ok)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: {result.error}");
        }
        produced = result.produced;
        return result.consumed;
    }

    private static (bool ok, int consumed, int produced, string error) DecodeAttempt(
        ReadOnlySpan<byte> body, Span<byte> output, List<byte> window, long blockOffset, bool bufferMode)
    {
        int src = 0, o = 0;
        int windowStart = window.Count;
        uint declared = (uint)output.Length;
        if (body.Length > 9 && body[0] is 0x46 or 0x47)
        {
            uint firstSize = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
            uint secondSize = BinaryPrimitives.ReadUInt32LittleEndian(body[5..]);
            if ((firstSize == (uint)body.Length && secondSize == declared) ||
                (secondSize == (uint)body.Length && firstSize == declared))
                src = 9;
        }

        void RollBack()
        {
            window.RemoveRange(windowStart, window.Count - windowStart);
        }

        try
        {
            while (o < output.Length)
            {
                if (src >= body.Length)
                {
                    if (bufferMode) break; // the stream simply ends with the body
                    throw new InvalidDataException($"the token stream ends after {o} of {output.Length} output bytes.");
                }
                int token = body[src++];
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    byte x;
                    do
                    {
                        if (src >= body.Length)
                        {
                            if (bufferMode) return End(o, src, output.Length); // the stream simply ends with the body
                            throw new InvalidDataException("the literal length extension runs past the payload.");
                        }
                        x = body[src++];
                        litLen += x;
                    }
                    while (x == 255 && src < body.Length);
                }
                int litTake = Math.Min(litLen, body.Length - src);
                int litEnd = Math.Min(o + litLen, output.Length);
                litTake = Math.Min(litTake, litEnd - o);
                for (int i = 0; i < litTake; i++, o++)
                {
                    byte v = body[src++];
                    output[o] = v;
                    window.Add(v);
                }
                if (o >= output.Length)
                    return End(o, src, output.Length); // trailing literals completed the block; the body tail is slack
                if (litTake < litLen && src >= body.Length)
                {
                    // the declared literal run reaches past the body
                    if (bufferMode) return End(o, src, output.Length); // the stream simply ends with the body
                    throw new InvalidDataException($"the literal run of {litLen} bytes reaches past the payload at {src} ({o} of {output.Length} output bytes).");
                }
                if (src + 2 > body.Length)
                {
                    if (bufferMode) return End(o, src, output.Length); // the stream simply ends with the body
                    throw new InvalidDataException($"the distance of the sequence at {src - 1} runs past the payload ({o} of {output.Length} output bytes).");
                }
                int dist = (body[src] << 8) | body[src + 1];
                src += 2;
                int matchLen;
                if (src >= body.Length)
                {
                    // final match without a length byte: the match length is the remainder of the output
                    matchLen = output.Length - o;
                }
                else
                {
                    int lenByte = body[src++];
                    matchLen = lenByte == 0 ? 256 : (((token & 0xF) << 8) | lenByte);
                }
                if (o + matchLen > output.Length)
                    matchLen = output.Length - o; // the final match is clipped at the declared size
                if (dist == 0)
                {
                    for (int i = 0; i < matchLen; i++, o++) { output[o] = 0; window.Add(0); }
                    continue;
                }
                if (dist > window.Count)
                    throw new InvalidDataException($"the back-reference distance {dist} exceeds the decoded stream ({window.Count} bytes).");
                for (int i = 0; i < matchLen; i++, o++)
                {
                    byte v = window[window.Count - dist];
                    output[o] = v;
                    window.Add(v);
                }
            }
            return End(o, src, output.Length);
        }
        catch (InvalidDataException e)
        {
            RollBack();
            return (false, src, o, e.Message);
        }

        static (bool, int, int, string) End(int o, int src, int declared) => (true, src, o, null!);
    }
}
