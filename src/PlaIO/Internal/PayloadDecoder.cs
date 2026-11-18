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

    /// <summary>Mode 1 streamed from the file: the payload bytes are read in chunks and decoded as they arrive, appended
    /// to <paramref name="window"/>, so a caller that only needs to know whether the stream decodes (a candidate block of
    /// the scan) stops after the first chunk instead of reading the whole payload into memory first. The buffer holds the
    /// declared unpacked size: the tail stays zero when the source ends at a group boundary before the buffer is full,
    /// and a source that ends inside a group — a mask byte whose set bit takes a literal that is not there — throws.</summary>
    /// <exception cref="InvalidDataException">The source ends inside a group.</exception>
    public static void DecodeZeroMask(Stream stream, long offset, long packed, long unpacked, List<byte> window)
    {
        var buffer = new byte[1 << 16];
        stream.Seek(offset, SeekOrigin.Begin);
        int bufLen = 0, bufPos = 0;
        long written = 0, read = 0;
        while (written < unpacked)
        {
            if (bufPos >= bufLen)
            {
                if (read >= packed) break; // the payload ended at a group boundary; the tail of the window stays zero
                bufLen = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, packed - read));
                bufPos = 0;
                read += bufLen;
                if (bufLen == 0) break; // the source ended at a group boundary; the tail of the window stays zero
            }
            int mask = buffer[bufPos++];
            for (int bit = 0; bit < 8 && written < unpacked; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                {
                    window.Add(0);
                    written++;
                    continue;
                }
                if (bufPos >= bufLen)
                {
                    if (read >= packed)
                        throw new InvalidDataException("Mode-1 stream ended inside a group; the last literal is truncated.");
                    bufLen = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, packed - read));
                    bufPos = 0;
                    read += bufLen;
                    if (bufLen == 0)
                        throw new InvalidDataException("Mode-1 stream ended inside a group; the last literal is truncated.");
                }
                window.Add(buffer[bufPos++]);
                written++;
            }
        }
    }
}
