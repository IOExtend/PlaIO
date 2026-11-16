using System.Buffers.Binary;

namespace PlaIO.Internal;

/// <summary>Decodes the mode 2 (LZ) token stream of one block payload into the
/// block output (exactly the block's declared unpacked size), appending every
/// produced byte to the window — the global decompressed stream of the whole
/// file. Back-references reach anywhere inside that stream, including the output of
/// preceding blocks.</summary>
/// <remarks>
/// <para>The stream is a chain of sequences. A sequence starts with a token byte: the high nibble
/// is the number of literal bytes that follow (15 = extended: the following bytes are added while
/// they equal 255). After the literals, if the output is not complete yet, come the match fields:
/// a 16-bit big-endian distance and a length byte. A distance of 0 emits the match length as zero
/// bytes (RLE); a positive distance copies the match length from that many bytes back in the
/// global window, byte by byte, so overlapping copies replicate a repeating pattern. The low
/// nibble of the token is the high byte of the match length, so a match spans up to 4095 bytes
/// (a length byte of 0 with a non-zero high nibble still means 256). When the output is full the
/// sequence ends; the remaining bytes of the body are slack the writer reserved and are not
/// consumed.</para>
/// </remarks>
internal static class LzPayloadDecoder
{
    public static int Decode(ReadOnlySpan<byte> body, Span<byte> output, List<byte> window, long blockOffset)
    {
        int src = 0, o = 0;
        while (o < output.Length)
        {
            if (src >= body.Length)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the token stream ends after {o} of {output.Length} output bytes.");
            int token = body[src++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                byte x;
                do
                {
                    if (src >= body.Length)
                        throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the literal length extension runs past the payload.");
                    x = body[src++];
                    litLen += x;
                }
                while (x == 255 && src < body.Length);
            }
            if (src + litLen > body.Length)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the literal run of {litLen} bytes runs past the payload at {src}.");
            int litEnd = Math.Min(o + litLen, output.Length);
            for (; o < litEnd; o++)
            {
                byte v = body[src++];
                output[o] = v;
                window.Add(v);
            }
            if (o >= output.Length)
                return src; // trailing literals completed the block; the body tail is slack
            if (src + 2 > body.Length)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the distance of the sequence at {src - 1} runs past the payload.");
            int dist = (body[src] << 8) | body[src + 1];
            src += 2;
            if (src >= body.Length)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the match length of the sequence at {src - 2} runs past the payload.");
            int lenByte = body[src++];
            int matchLen = lenByte == 0 ? 256 : (((token & 0xF) << 8) | lenByte);
            if (o + matchLen > output.Length)
                matchLen = output.Length - o; // the final match is clipped at the declared size
            if (dist == 0)
            {
                for (int i = 0; i < matchLen; i++, o++) { output[o] = 0; window.Add(0); }
                continue;
            }
            if (dist > window.Count)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the back-reference distance {dist} exceeds the decoded stream ({window.Count} bytes).");
            for (int i = 0; i < matchLen; i++, o++)
            {
                byte v = window[window.Count - dist];
                output[o] = v;
                window.Add(v);
            }
        }
        return src;
    }
}
