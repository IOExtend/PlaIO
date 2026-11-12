using PlaIO.Internal;

namespace PlaIO.Tests;

/// <summary>The block index rule applied literally over a byte array with a one-byte step. Used only to
/// check that the windowed scanner finds exactly the same blocks.</summary>
internal static class NaiveScanner
{
    public static List<PlaBlock> Scan(byte[] data)
    {
        var blocks = new List<PlaBlock>();
        long pos = 32;
        while (pos + 13 <= data.Length)
        {
            if (BlockHeaderParser.TryParse(data.AsSpan((int)pos), pos, data.Length, out var block))
            {
                blocks.Add(block);
                pos = block.End;
            }
            else pos++;
        }
        return blocks;
    }
}
