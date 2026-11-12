namespace PlaIO.Internal;

/// <summary>Builds the block index in one forward pass: from offset 32, look for a "BL" or "DY" tag, accept a block whose
/// header passes <see cref="BlockHeaderParser.TryParse"/>, continue at the end of its payload (the payload is skipped, not
/// read), otherwise move one byte on. Memory is one window buffer plus the list.</summary>
/// <remarks>The step is one byte: blocks are not aligned and may start at any offset, so a two-byte step would miss blocks
/// that follow an odd-length payload.</remarks>
internal sealed class BlockScanner
{
    private readonly Stream _stream;
    private readonly long _length;
    private readonly int _windowSize;
    private readonly int _maxBlocks;

    public BlockScanner(Stream stream, long length, int windowSize = 1 << 20, int maxBlocks = FdbFormat.MaxBlocks)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBlocks);
        _stream = stream;
        _length = length;
        _windowSize = windowSize;
        _maxBlocks = maxBlocks;
    }

    public List<PlaBlock> Scan()
    {
        var blocks = new List<PlaBlock>();
        var buffer = new byte[_windowSize + FdbFormat.MaxBlockHeaderLength];
        long winStart = 0, winEnd = 0;
        long pos = FdbFormat.FirstBlockOffset;
        // The next "BL" and "DY" at or after pos in the current window (searchEnd when there is none); -1 means search again.
        long nextBl = -1, nextDy = -1;
        while (pos + FdbFormat.MinBlockHeaderLength <= _length)
        {
            // Keep a full header after every candidate unless the window already reaches the end of the file.
            if (pos < winStart || pos >= winEnd || (pos + FdbFormat.MaxBlockHeaderLength >= winEnd && winEnd < _length))
            {
                int count = (int)Math.Min(buffer.Length, _length - pos);
                StreamReads.ReadExactlyAt(_stream, pos, buffer.AsSpan(0, count), "the block index");
                winStart = pos;
                winEnd = pos + count;
                nextBl = nextDy = -1;
            }

            // Candidates start before searchEnd; the second tag byte may lie at searchEnd.
            long searchEnd = winEnd == _length ? winEnd - 1 : winEnd - FdbFormat.MaxBlockHeaderLength;
            var window = buffer.AsSpan((int)(pos - winStart), (int)(searchEnd + 1 - pos));
            if (nextBl < pos) nextBl = window.IndexOf("BL"u8) is var bl and >= 0 ? pos + bl : searchEnd;
            if (nextDy < pos) nextDy = window.IndexOf("DY"u8) is var dy and >= 0 ? pos + dy : searchEnd;
            long candidate = Math.Min(nextBl, nextDy);
            if (candidate >= searchEnd)
            {
                pos = searchEnd;
                continue;
            }

            int at = (int)(candidate - winStart);
            if (BlockHeaderParser.TryParse(buffer.AsSpan(at, (int)(winEnd - candidate)), candidate, _length, out var block))
            {
                if (blocks.Count == _maxBlocks)
                    throw new InvalidDataException($"More than {_maxBlocks} blocks; reader limit reached.");
                blocks.Add(block);
                pos = block.End;
            }
            else
            {
                pos = candidate + 1;
            }
        }
        return blocks;
    }
}
