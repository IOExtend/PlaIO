namespace PlaIO.Internal;

internal static class PayloadDecoder
{
    /// <summary>Mode 1: a stream of groups, each a mask byte followed by one byte per set bit. Bits are read from the least
    /// significant; a set bit takes the next stream byte, a clear bit writes 0x00. Decoding ends when <paramref name="dst"/>
    /// is full, so bits of the last mask past the end consume nothing. Returns the number of source bytes consumed; bytes
    /// left after that are allowed.</summary>
    public static int DecodeZeroMask(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int consumed = 0, written = 0;
        while (written < dst.Length)
        {
            if (consumed >= src.Length) throw Short(consumed, src.Length, written, dst.Length);
            int mask = src[consumed++];
            for (int bit = 0; bit < 8 && written < dst.Length; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                {
                    dst[written++] = 0;
                    continue;
                }
                if (consumed >= src.Length) throw Short(consumed, src.Length, written, dst.Length);
                dst[written++] = src[consumed++];
            }
        }
        return consumed;
    }

    private static InvalidDataException Short(int consumed, int available, int written, int expected) =>
        new($"Mode-1 stream ended after {consumed} of {available} bytes with {written} of {expected} bytes written.");
}
