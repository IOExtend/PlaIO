using PlaIO.Internal;

namespace PlaIO;

/// <summary>The kind of a file, decided by its first bytes.</summary>
public enum PlaFileKind
{
    /// <summary>Neither signature is present.</summary>
    Unknown = 0,

    /// <summary>The container of .pla and .pln files (bytes 0..7 are <c>ROF FDB </c>). The two extensions share the
    /// container and are not told apart.</summary>
    Fdb = 1,

    /// <summary>A .bimx file (bytes 0..3 are <c>BIMx</c>).</summary>
    Bimx = 2,
}

/// <summary>File kind detection.</summary>
public static class PlaFile
{
    /// <summary>The number of leading bytes <see cref="DetectKind(ReadOnlySpan{byte})"/> needs to recognise every kind.</summary>
    public const int SignatureLength = 8;

    /// <summary>The kind of a file whose first bytes are <paramref name="head"/>.</summary>
    public static PlaFileKind DetectKind(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(FdbFormat.FdbSignature)) return PlaFileKind.Fdb;
        if (head.StartsWith(FdbFormat.BimxSignature)) return PlaFileKind.Bimx;
        return PlaFileKind.Unknown;
    }

    /// <summary>The kind of the file at <paramref name="path"/>; reads at most <see cref="SignatureLength"/> bytes. I/O
    /// errors propagate.</summary>
    public static PlaFileKind DetectKind(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        Span<byte> head = stackalloc byte[SignatureLength];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return DetectKind(head[..read]);
    }
}
