using PlaIO.Internal;

namespace PlaIO;

/// <summary>An open FDB container (.pla or .pln): the header, the block index and payload decoding for modes 0, 1 and 2.
/// The file is never loaded whole; payloads are read on request. Not thread-safe.</summary>
public sealed class PlaDocument : IDisposable
{
    private static readonly Comparer<PlaBlock> ByOffset = Comparer<PlaBlock>.Create((a, b) => a.Offset.CompareTo(b.Offset));

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly List<PlaBlock> _blocks;
    private bool _disposed;

    private PlaDocument(Stream stream, bool leaveOpen, long length, FdbHeader header, List<PlaBlock> blocks)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Length = length;
        Header = header;
        _blocks = blocks;
        Blocks = blocks.AsReadOnly();
    }

    /// <summary>Length of the file in bytes.</summary>
    public long Length { get; }

    /// <summary>The container header.</summary>
    public FdbHeader Header { get; }

    /// <summary>The block index, in file order, built once by <see cref="Open(Stream, bool)"/>. No on-disk block map is
    /// known, so the index is found by scanning (from offset 32, one byte at a time, skipping accepted payloads) and makes no
    /// claim of completeness. The scan can take a byte run for a block header, so the index may contain false entries, and
    /// in real files a small share of mode 1 blocks does not decode to its declared size.</summary>
    public IReadOnlyList<PlaBlock> Blocks { get; }

    /// <summary>Opens the file at <paramref name="path"/> for reading.</summary>
    /// <exception cref="InvalidDataException">The file is not an FDB container or is cut short.</exception>
    public static PlaDocument Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.RandomAccess);
        try
        {
            return Open(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a container from a readable, seekable stream. The document owns the stream unless
    /// <paramref name="leaveOpen"/> is true; when this method throws, the stream stays with the caller.</summary>
    /// <exception cref="ArgumentException">The stream cannot read or seek.</exception>
    /// <exception cref="InvalidDataException">The data is not an FDB container or is cut short.</exception>
    public static PlaDocument Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));

        long length = stream.Length;
        if (length < FdbFormat.FirstBlockOffset)
            throw new InvalidDataException($"File is {length} bytes; an FDB container header needs at least {FdbFormat.FirstBlockOffset}.");
        var raw = new byte[Math.Min(FdbHeader.Size, length)];
        StreamReads.ReadExactlyAt(stream, 0, raw, "the FDB container header");
        var kind = PlaFile.DetectKind(raw);
        if (kind != PlaFileKind.Fdb)
            throw new InvalidDataException($"Not an FDB container (detected kind: {kind}).");

        var blocks = new BlockScanner(stream, length).Scan();
        return new PlaDocument(stream, leaveOpen, length, new FdbHeader(raw), blocks);
    }

    /// <summary>The decoded payload of a block of this document.</summary>
    /// <remarks>Reading a mode 2 (<see cref="PlaBlockMode.Lz"/>) block decodes every block of the file in order into
    /// one global window first, because a mode 2 token stream may reference the output of any preceding block; the cost
    /// of the call therefore grows with the size of the file. A preceding block that does not decode to its declared
    /// size (a false entry of the scan) is skipped.</remarks>
    /// <exception cref="InvalidDataException">A payload does not decode to its declared size, or the file is cut
    /// short.</exception>
    /// <exception cref="ArgumentException">The block is not one of the <see cref="Blocks"/> of this document.</exception>
    public byte[] ReadPayload(PlaBlock block) => ReadPayload(block, out _);

    internal byte[] ReadPayload(PlaBlock block, out int consumed)
    {
        ArgumentNullException.ThrowIfNull(block);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The index is in file order with distinct offsets, so a binary search finds the one block that may match.
        int index = _blocks.BinarySearch(block, ByOffset);
        if (index < 0 || !ReferenceEquals(_blocks[index], block))
            throw new ArgumentException($"The block at 0x{block.Offset:x} is not in the block index of this document.", nameof(block));

        switch (block.Mode)
        {
            case PlaBlockMode.Raw:
            {
                // The index accepts raw payloads of at most 2^28 bytes, so the size fits an array.
                var payload = new byte[block.PackedSize];
                StreamReads.ReadExactlyAt(_stream, block.PayloadOffset, payload, $"the payload of the block at 0x{block.Offset:x}");
                consumed = payload.Length;
                return payload;
            }
            case PlaBlockMode.ZeroMask:
            {
                // A mask byte yields at most 8 bytes, so a larger declared size cannot decode; reject it before allocating.
                if ((block.UnpackedSize + 7) / 8 > block.PackedSize)
                    throw new InvalidDataException($"Mode-1 block at 0x{block.Offset:x} declares {block.UnpackedSize} unpacked bytes from {block.PackedSize} packed bytes; a mask byte yields at most 8.");
                long needed = Math.Min(block.PackedSize, block.UnpackedSize + (block.UnpackedSize + 7) / 8);
                var source = new byte[needed];
                StreamReads.ReadExactlyAt(_stream, block.PayloadOffset, source, $"the payload of the block at 0x{block.Offset:x}");
                var payload = new byte[block.UnpackedSize];
                try
                {
                    consumed = PayloadDecoder.DecodeZeroMask(source, payload);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"Block at 0x{block.Offset:x}: {e.Message}", e);
                }
                return payload;
            }
            case PlaBlockMode.Lz:
            {
                // The mode-2 token stream references the decompressed output of all preceding blocks, so the whole
                // file is decoded from the start and the requested block's output is returned. The cost of a call
                // therefore grows with the size of the file. A preceding block that does not decode is a scanner
                // false positive and not part of the real stream, so it is skipped.
                var window = new List<byte>();
                foreach (var b in _blocks)
                {
                    if (ReferenceEquals(b, block)) break;
                    TryAppendToWindow(b, window);
                }
                var blockPayload = new byte[block.PackedSize];
                StreamReads.ReadExactlyAt(_stream, block.PayloadOffset, blockPayload, $"the payload of the block at 0x{block.Offset:x}");
                // Mode 2 (LZ): the token stream produces the declared unpacked size (some blocks declare a buffer
                // larger than the stream produces — the tail of the buffer stays zero) and may reference the window;
                // the body tail after the output is complete is writer slack.
                var output = new byte[block.UnpackedSize];
                int lzConsumed;
                try
                {
                    lzConsumed = LzPayloadDecoder.Decode(blockPayload, output, window, block.Offset, out _);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"Block at 0x{block.Offset:x}: {e.Message}", e);
                }
                consumed = lzConsumed;
                return output;
            }
            default:
                throw new NotSupportedException($"Block at 0x{block.Offset:x} uses an unknown payload mode {block.Mode}.");
        }
    }

    /// <summary>Decodes the whole file once into the global stream: the raw payloads, the full zero-mask decodes and the
    /// mode-2 outputs, in block order. If any indexed block fails to decode, the stream rejects reads that reach
    /// the resulting unreliable range. The stream holds the decoded bytes in memory independently of the document's
    /// lifetime. Object directory records address the stream by position and count.</summary>
    public PlaStream OpenStream()
    {
        var window = DecodeGlobalWindow(out var skipped);
        return new PlaStream(window, skipped.Count == 0 ? null : skipped[0].WindowPosition);
    }

    /// <summary>Appends the decoded payload of one block to the mode-2 global window, or leaves the window unchanged when
    /// the block does not decode to its declared size (a false entry of the scan is not part of the real stream, so a
    /// partial decode is rolled back). Returns whether the block was appended.</summary>
    private bool TryAppendToWindow(PlaBlock b, List<byte> window)
    {
        int before = window.Count;
        try
        {
            AppendToWindow(b, window);
            return true;
        }
        catch (InvalidDataException)
        {
            window.RemoveRange(before, window.Count - before);
            return false;
        }
    }

    /// <summary>Decodes the whole file once into the global window: the raw payloads, the full zero-mask decodes and the
    /// mode-2 outputs, in block order. Blocks that do not decode to their declared size (false entries of the scan) are
    /// skipped and reported in <paramref name="skipped"/> with the window offset each would have contributed; the window
    /// stays as if they were not there.</summary>
    internal List<byte> DecodeGlobalWindow(out IReadOnlyList<(PlaBlock Block, long WindowPosition)> skipped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = new List<byte>();
        var failed = new List<(PlaBlock, long)>();
        foreach (var b in _blocks)
        {
            int before = window.Count;
            if (!TryAppendToWindow(b, window)) failed.Add((b, before));
        }
        skipped = failed.AsReadOnly();
        return window;
    }

    /// <summary>Appends the decoded payload of one block to the mode-2 global window. Raw payloads are copied as they
    /// are; a zero-mask payload contributes its full decode; a mode 2 payload is decoded and the decoder appends every
    /// produced byte. Throws <see cref="InvalidDataException"/> when the block does not decode to its declared size.</summary>
    private void AppendToWindow(PlaBlock b, List<byte> window)
    {
        if (b.Mode != PlaBlockMode.Lz)
        {
            window.AddRange(ReadPayload(b));
            return;
        }

        var payload = new byte[b.PackedSize];
        StreamReads.ReadExactlyAt(_stream, b.PayloadOffset, payload, $"the payload of the block at 0x{b.Offset:x}");
        // Mode 2 (LZ): the token stream produces the declared unpacked size (some blocks declare a buffer
        // larger than the stream produces — the tail of the buffer stays zero) and the decoder appends every
        // produced byte to the window; the body tail after the output is complete is writer slack.
        var output = new byte[b.UnpackedSize];
        int produced;
        LzPayloadDecoder.Decode(payload, output, window, b.Offset, out produced);
        for (long i = produced; i < b.UnpackedSize; i++) window.Add(0); // the directory addresses the buffer space
    }

    /// <summary>Searches every raw block large enough for 64 records for an object directory (at most one per block).
    /// Computed on each call; reads the raw blocks one at a time. Mode-1 payloads are not searched.</summary>
    /// <remarks>The work grows with the total size of the raw blocks: each block gets a linear pre-check at all 30 record
    /// alignments, and each alignment that passes costs one sort of its records; a block needs about 1.5 times its size in
    /// memory while it is searched.</remarks>
    public IReadOnlyList<ObjectDirectory> FindDirectories()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var found = new List<ObjectDirectory>();
        foreach (var block in Blocks)
        {
            if (block.Mode != PlaBlockMode.Raw || block.PackedSize < DirectoryFinder.MinRecords * DirectoryFinder.RecordSize) continue;
            if (DirectoryFinder.FindInPayload(ReadPayload(block), block) is { } directory) found.Add(directory);
        }
        return found.AsReadOnly();
    }

    /// <summary>Finds compressed pools whose marker and frame headers parse, outside indexed block payloads.</summary>
    public IReadOnlyList<CompressedPool> FindCompressedPools()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return PoolReader.Find(_stream, Length, Blocks).Select(range => new CompressedPool(this, range)).ToList().AsReadOnly();
    }

    internal byte[] DecodePool(long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return PoolReader.Decode(_stream, Length, offset);
    }

    /// <summary>Releases the stream unless the document was opened with leaveOpen.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}
