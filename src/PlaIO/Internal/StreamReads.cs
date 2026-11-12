namespace PlaIO.Internal;

internal static class StreamReads
{
    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>; a stream that ends early is reported as a
    /// malformed file naming <paramref name="what"/>.</summary>
    public static void ReadExactlyAt(Stream stream, long offset, Span<byte> buffer, string what)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException($"Unexpected end of file while reading {what} at offset 0x{offset:x}.", e);
        }
    }
}
