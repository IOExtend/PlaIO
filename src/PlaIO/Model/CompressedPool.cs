using PlaIO;
using PlaIO.Internal;

namespace PlaIO.Model;

/// <summary>A pool of QuickLZ frames stored between indexed FDB block payloads.</summary>
public sealed class CompressedPool
{
    private readonly PlaDocument _document;

    internal CompressedPool(PlaDocument document, PoolRange range, int index)
    {
        _document = document;
        Index = index;
        Offset = range.Offset;
        End = range.End;
        TotalDecompressed = range.UnpackedSize;
        BlockCount = range.FrameCount;
    }

    internal int Index { get; }

    /// <summary>The file offset of the pool marker.</summary>
    public long Offset { get; }

    /// <summary>The first file offset after the pool.</summary>
    public long End { get; }

    /// <summary>The declared size of the decoded pool in bytes.</summary>
    public long TotalDecompressed { get; }

    /// <summary>The number of QuickLZ frames in the pool.</summary>
    public int BlockCount { get; }

    /// <summary>Decodes the frames and concatenates their payloads in file order.</summary>
    /// <exception cref="InvalidDataException">The pool is malformed or a frame fails to decode.</exception>
    /// <exception cref="ObjectDisposedException">The owning document is disposed.</exception>
    public byte[] Decode() => _document.DecodePool(Offset);

    /// <summary>Decodes the pool and reads BL-tagged records in pool order. A mode-2 payload is an LZ token stream
    /// whose back-references reach into the pool stream — the reconstruction of every preceding pool of the document
    /// and of this pool before the record — so the pools are decoded in order until this one and the cost of the call
    /// grows with the size of the file. Reading the pools in file order advances one shared window, so every pool
    /// decodes once; a read that names an earlier pool rebuilds the window. An unreadable preceding pool resets
    /// the history, and a subgroup that references missing bytes returns empty data with Partial set.</summary>
    /// <exception cref="InvalidDataException">The pool does not decode.</exception>
    /// <exception cref="ObjectDisposedException">The owning document is disposed.</exception>
    public IReadOnlyList<PoolSubgroup> ReadSubgroups() => _document.ReadPoolSubgroups(this);
}
