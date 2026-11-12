using PlaIO.Internal;

namespace PlaIO;

/// <summary>An open .bimx file: the <c>BIMx</c> signature followed by a PNG cover image. What follows the cover is not
/// documented and is not interpreted.</summary>
public sealed class BimxDocument : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _maxCoverLength;
    private bool _disposed;

    private BimxDocument(Stream stream, bool leaveOpen, long length, long coverLength, long maxCoverLength)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _maxCoverLength = maxCoverLength;
        Length = length;
        CoverLength = coverLength;
    }

    /// <summary>Length of the file in bytes.</summary>
    public long Length { get; }

    /// <summary>File offset of the PNG cover image: 4, right after the signature.</summary>
    public long CoverOffset => 4;

    /// <summary>Length of the cover image, from the PNG signature through the IEND chunk and its CRC.</summary>
    public long CoverLength { get; }

    /// <summary>File offset of the content after the cover; not interpreted.</summary>
    public long ContentOffset => CoverOffset + CoverLength;

    /// <summary>Opens the file at <paramref name="path"/> for reading.</summary>
    /// <exception cref="InvalidDataException">The file is not a .bimx file or its cover image is cut short.</exception>
    public static BimxDocument Open(string path)
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

    /// <summary>Opens a .bimx file from a readable, seekable stream and measures the cover image without reading it. The
    /// document owns the stream unless <paramref name="leaveOpen"/> is true; when this method throws, the stream stays with
    /// the caller.</summary>
    /// <exception cref="ArgumentException">The stream cannot read or seek.</exception>
    /// <exception cref="InvalidDataException">The data is not a .bimx file or its cover image is cut short.</exception>
    public static BimxDocument Open(Stream stream, bool leaveOpen = false) => Open(stream, leaveOpen, FdbFormat.MaxCoverLength);

    internal static BimxDocument Open(Stream stream, bool leaveOpen, long maxCoverLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));

        long length = stream.Length;
        var head = new byte[Math.Min(PlaFile.SignatureLength, length)];
        StreamReads.ReadExactlyAt(stream, 0, head, "the file signature");
        var kind = PlaFile.DetectKind(head);
        if (kind != PlaFileKind.Bimx)
            throw new InvalidDataException($"Not a BIMx file (detected kind: {kind}).");

        long coverLength = PngChunks.Measure(stream, 4, length);
        return new BimxDocument(stream, leaveOpen, length, coverLength, maxCoverLength);
    }

    /// <summary>The PNG cover image.</summary>
    /// <exception cref="InvalidDataException">The cover is larger than this reader reads into memory (64 MiB).</exception>
    public byte[] ReadCover()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CoverLength > _maxCoverLength)
            throw new InvalidDataException($"The cover image is {CoverLength} bytes; this reader reads at most {_maxCoverLength}.");
        var cover = new byte[CoverLength];
        StreamReads.ReadExactlyAt(_stream, CoverOffset, cover, "the cover image");
        return cover;
    }

    /// <summary>Releases the stream unless the document was opened with leaveOpen.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}
