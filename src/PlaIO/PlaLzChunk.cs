namespace PlaIO;

/// <summary>One chunk of a mode 2 (LZ) block payload: a self-contained run of output bytes with its own sizes, found
/// by <see cref="PlaDocument.ReadChunks"/>. The payload is a chain of chunks; each chunk starts with a flags byte and
/// declares its sizes, so the chain can be walked without decoding the compressed runs.</summary>
/// <remarks>
/// In real files whose chunk chain walks cleanly, the sum of <see cref="UnpackedSize"/> over the chunks equals the
/// block's <see cref="PlaBlock.UnpackedSize"/>, and a chunk whose flags bit 0 is clear stores exactly
/// <see cref="PackedSize"/> raw output bytes. A chain that does not add up, or a chunk header that does not fit, marks
/// a scanner false positive or an unknown detail; <see cref="PlaDocument.ReadChunks"/> rejects such a block.
/// </remarks>
/// <param name="Offset">Offset of the chunk within the block payload; the flags byte sits here.</param>
/// <param name="HeaderLength">Size of the chunk header: 9 bytes (flags, two 32-bit sizes) or 3 bytes (flags, two
/// 8-bit sizes).</param>
/// <param name="PackedSize">Size of the chunk body after the header. A compressed chunk decodes these bytes to
/// <paramref name="UnpackedSize"/>; a stored chunk (<see cref="IsCompressed"/> false) holds them raw.</param>
/// <param name="UnpackedSize">Number of output bytes the chunk produces.</param>
/// <param name="IsCompressed">True when the chunk body is a compressed run (flags bit 0 set); false when the body is
/// stored raw.</param>
public readonly record struct PlaLzChunk(
    long Offset,
    int HeaderLength,
    long PackedSize,
    long UnpackedSize,
    bool IsCompressed);
