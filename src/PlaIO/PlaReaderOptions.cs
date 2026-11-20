using PlaIO.Model;

namespace PlaIO;

/// <summary>Options that control how much the readers decode into memory. The defaults are reader limits the library
/// has always used; they are choices of this reader, not facts of the format. The options are immutable: build a new
/// instance (an object initializer) to change a limit — <see cref="Default"/> cannot be mutated, so one reader never
/// changes the limits of another.</summary>
public sealed class PlaReaderOptions
{
    /// <summary>The options a reader uses when the caller passes none.</summary>
    public static PlaReaderOptions Default { get; } = new();

    /// <summary>Cap on the size of the decoded global stream (<see cref="PlaDocument.OpenStream"/>) and of the window a
    /// mode-2 <see cref="PlaDocument.ReadPayload(PlaBlock)"/> builds (default: the most one stream can hold). The
    /// stream holds the whole decoded file in memory; a block whose declared size grows the window past the cap makes
    /// the reader throw <see cref="InvalidDataException"/> naming the cap.</summary>
    public long MaxGlobalStreamBytes { get; init; } = Internal.FdbFormat.MaxGlobalStreamBytes;

    /// <summary>Cap on the decoded size of one compressed pool (default: 512 MiB). A pool that declares more makes
    /// <see cref="Model.CompressedPool.Decode"/> throw <see cref="InvalidDataException"/> naming the cap.</summary>
    public long MaxDecodedPoolBytes { get; init; } = Internal.FdbFormat.MaxDecodedPoolBytes;

    /// <summary>Cap on the .bimx cover image that is read into memory (default: 64 MiB). A larger cover makes
    /// <see cref="BimxDocument.ReadCover"/> throw <see cref="InvalidDataException"/> naming the cap.</summary>
    public long MaxCoverBytes { get; init; } = Internal.FdbFormat.MaxCoverLength;
}
