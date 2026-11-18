namespace PlaIO.Internal;

internal static class PayloadDecoder
{
    /// <summary>How a zero-mask stream ended relative to the destination buffer.</summary>
    public enum ZeroMaskEnd
    {
        /// <summary>The destination buffer is full.</summary>
        Complete,
        /// <summary>The stream ended exactly at a group boundary before the buffer is full; the tail of the buffer stays
        /// zero. Valid for the pool subgroups, whose declared size is the buffer size.</summary>
        StreamEnd,
        /// <summary>The stream ended inside a group: a mask byte was read whose set bit takes a stream byte that is not
        /// there. A truncated literal, never a valid padding.</summary>
        MidGroup,
    }

    /// <summary>Mode 1: a stream of groups, each a mask byte followed by one byte per set bit. Bits are read from the least
    /// significant; a set bit takes the next stream byte, a clear bit writes 0x00. Decoding ends when <paramref name="dst"/>
    /// is full, so bits of the last mask past the end consume nothing; the <paramref name="end"/> status tells how the
    /// stream ended when <paramref name="dst"/> is not full. Returns the number of source bytes consumed; bytes left after
    /// that are allowed.</summary>
    public static int DecodeZeroMask(ReadOnlySpan<byte> src, Span<byte> dst, out ZeroMaskEnd end)
    {
        return DecodeCore(src, dst, out end, out _);
    }

    /// <summary>Mode 1 for the file blocks: the stream must fill the buffer exactly; a stream that ends before that — at a
    /// group boundary or inside a group — does not decode. Returns the number of source bytes consumed; bytes left after
    /// that are allowed.</summary>
    /// <exception cref="InvalidDataException">The stream ends before the buffer is full.</exception>
    public static int DecodeZeroMask(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int consumed = DecodeCore(src, dst, out _, out var written);
        if (written < dst.Length)
            throw Short(consumed, src.Length, written, dst.Length);
        return consumed;
    }

    private static int DecodeCore(ReadOnlySpan<byte> src, Span<byte> dst, out ZeroMaskEnd end, out int written)
    {
        int consumed = 0;
        written = 0;
        end = ZeroMaskEnd.Complete;
        while (written < dst.Length)
        {
            if (consumed >= src.Length)
            {
                end = ZeroMaskEnd.StreamEnd;
                return consumed;
            }
            int mask = src[consumed++];
            for (int bit = 0; bit < 8 && written < dst.Length; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                {
                    dst[written++] = 0;
                    continue;
                }
                if (consumed >= src.Length)
                {
                    end = ZeroMaskEnd.MidGroup;
                    return consumed;
                }
                dst[written++] = src[consumed++];
            }
        }
        return consumed;
    }

    private static InvalidDataException Short(int consumed, int available, int written, int expected) =>
        new($"Mode-1 stream ended after {consumed} of {available} bytes with {written} of {expected} bytes written.");
}
