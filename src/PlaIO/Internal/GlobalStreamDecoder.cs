using PlaIO.Model;

namespace PlaIO.Internal;

/// <summary>Decodes the global stream: the payloads of every indexed block in file order appended to one window,
/// because a mode-2 token stream may reference the output of any preceding block. A block that does not decode to its
/// declared size (a false entry of the scan) is not part of the real stream: it is skipped and reported with the window
/// offset it would have contributed, and the window stays as if it were not there. The window never grows past
/// <c>maxBytes</c>: a block whose declared unpacked size would cross the cap throws before it is decoded.</summary>
internal static class GlobalStreamDecoder
{
    /// <summary>Decodes every block of the document into a fresh window. The skipped blocks are reported in file
    /// order.</summary>
    public static List<byte> DecodeAll(PlaDocument document, long maxBytes, out IReadOnlyList<(PlaBlock Block, long WindowPosition)> skipped)
    {
        var window = new List<byte>();
        var failed = new List<(PlaBlock, long)>();
        foreach (var block in document.Blocks)
        {
            int before = window.Count;
            if (!TryAppend(document, block, window, maxBytes)) failed.Add((block, before));
        }
        skipped = failed.AsReadOnly();
        return window;
    }

    /// <summary>Decodes every block before <paramref name="through"/> into a fresh window, skipping the blocks that do
    /// not decode as <see cref="DecodeAll"/> does. The block itself is not decoded.</summary>
    public static List<byte> DecodePrefix(PlaDocument document, PlaBlock through, long maxBytes)
    {
        var window = new List<byte>();
        foreach (var block in document.Blocks)
        {
            if (ReferenceEquals(block, through)) break;
            TryAppend(document, block, window, maxBytes);
        }
        return window;
    }

    /// <summary>Appends the decoded payload of one block to the window, or leaves the window unchanged and returns
    /// false when the block does not decode to its declared size (a false entry of the scan, so a partial decode is
    /// rolled back).</summary>
    public static bool TryAppend(PlaDocument document, PlaBlock block, List<byte> window, long maxBytes)
    {
        // The cap is a property of the whole window, so it is checked against the declared contribution before any
        // decoding; a block that fails the cap is an error, not a skipped false entry.
        if (window.Count + block.UnpackedSize > maxBytes)
            throw new InvalidDataException($"The decoded global stream would grow past {maxBytes} bytes (the reader's cap) at the block at 0x{block.Offset:x}.");
        int before = window.Count;
        try
        {
            Append(document, block, window);
            return true;
        }
        catch (InvalidDataException)
        {
            window.RemoveRange(before, window.Count - before);
            return false;
        }
    }

    /// <summary>Appends one block's full contribution to the window: raw payloads as they are, zero-mask payloads as
    /// their full decode, mode-2 payloads as their token-stream output with the declared tail zero-filled (the
    /// directory addresses the buffer space, so the window holds it). Throws <see cref="InvalidDataException"/> when
    /// the block does not decode to its declared size.</summary>
    private static void Append(PlaDocument document, PlaBlock block, List<byte> window)
    {
        if (block.Mode != PlaBlockMode.Lz)
        {
            window.AddRange(document.ReadPayload(block));
            return;
        }

        document.ReadLzPayload(block, window, out int produced, out _);
        if (produced < block.UnpackedSize) window.AddRange(new byte[(int)(block.UnpackedSize - produced)]);
    }
}
