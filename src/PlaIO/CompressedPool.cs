namespace PlaIO;

/// <summary>A compressed storage pool of the ODB object database that the file holds in a gap between blocks: the
/// compression sign, the declared decompressed total and the blocks (each memcpy or LZ77-triple coded). The object
/// database keeps its groups, journals and library data in such pools; a pool is not a block and does not take part in
/// the block index or the payloads. The decoded bytes are produced on request by <see cref="Decode"/>; the cost of a
/// call grows with the pool size. The layout and the codec follow the format notes of this repository and are confirmed
/// against the sample files.</summary>
public sealed class CompressedPool
{
    private readonly PlaDocument _document;

    internal CompressedPool(PlaDocument document, long offset, long end, long total, int blockCount)
    {
        _document = document;
        Offset = offset;
        End = end;
        TotalDecompressed = total;
        BlockCount = blockCount;
    }

    /// <summary>File offset of the pool's compression sign.</summary>
    public long Offset { get; }

    /// <summary>File offset past the last block of the pool.</summary>
    public long End { get; }

    /// <summary>The declared size of the decoded pool in bytes; the blocks decode to exactly this size.</summary>
    public long TotalDecompressed { get; }

    /// <summary>The number of blocks; at most one megabyte and change of packed payload per block.</summary>
    public int BlockCount { get; }

    /// <summary>Decodes the pool: every block memcpy or LZ77-triple decoded, concatenated. The call does not cache; each
    /// call decodes again.</summary>
    /// <exception cref="InvalidDataException">A block does not decode or the blocks do not add up to
    /// <see cref="TotalDecompressed"/>.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public byte[] Decode() => _document.DecodePool(Offset);

    /// <summary>Decodes the pool and reads its object records: the BL-tagged subgroups whose headers parse, with their
    /// payloads decoded (raw or zero mask; mode 2 is reported with an empty payload). Recomputed on each call; a
    /// rejected BL candidate is retried one byte on, so a short false header can skip over a real record.</summary>
    /// <exception cref="InvalidDataException">The pool does not decode.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public IReadOnlyList<PoolSubgroup> ReadSubgroups() => Internal.SubgroupParser.Parse(Decode());
}
