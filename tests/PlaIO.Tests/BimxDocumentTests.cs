using System.Buffers.Binary;
using Xunit;

namespace PlaIO.Tests;

public class BimxDocumentTests
{
    private static readonly byte[] Trailing = [1, 2, 3, 4, 5, 6, 7];

    [Fact]
    public void CoverIsMeasuredAndRead()
    {
        var png = Synthetic.SimplePng();
        using var document = BimxDocument.Open(new MemoryStream(Synthetic.Bimx(png, Trailing)));
        Assert.Equal(4, document.CoverOffset);
        Assert.Equal(png.Length, document.CoverLength);
        Assert.Equal(4 + png.Length, document.ContentOffset);
        Assert.Equal(4 + png.Length + Trailing.Length, document.Length);
        Assert.Equal(png, document.ReadCover());
    }

    [Fact]
    public void CoverMayEndTheFile()
    {
        var png = Synthetic.SimplePng();
        using var document = BimxDocument.Open(new MemoryStream(Synthetic.Bimx(png, [])));
        Assert.Equal(document.Length, document.ContentOffset);
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var png = Synthetic.SimplePng();
        var pastEnd = Synthetic.Png(("IHDR", new byte[13]));
        BinaryPrimitives.WriteUInt32BigEndian(pastEnd.AsSpan(8), 1000);
        var huge = Synthetic.Png(("IHDR", new byte[13]), ("IEND", []));
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(8), 0x80000000);
        return new()
        {
            { "missing PNG signature", Synthetic.Bimx(new byte[png.Length], []) },
            { "chunk past the end", Synthetic.Bimx(pastEnd, []) },
            { "chunk of 2^31 bytes", Synthetic.Bimx(huge, []) },
            { "no IEND", Synthetic.Bimx(Synthetic.Png(("IHDR", new byte[13]), ("IDAT", [1])), [1, 2, 3]) },
            { "cut signature", Synthetic.Bimx(png[..5], []) },
            { "wrong signature", Synthetic.Concat("BIMX"u8.ToArray(), png) },
            { "FDB bytes", Synthetic.Fdb(new byte[40]) },
            { "empty", [] },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedFilesAreRejected(string reason, byte[] bytes)
    {
        Assert.NotEmpty(reason);
        Assert.Throws<InvalidDataException>(() => BimxDocument.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void CoverAboveTheReaderLimitIsNotRead()
    {
        var png = Synthetic.SimplePng();
        using var document = BimxDocument.Open(new MemoryStream(Synthetic.Bimx(png, Trailing)), leaveOpen: false, maxCoverLength: png.Length - 1);
        Assert.Equal(png.Length, document.CoverLength);
        Assert.Throws<InvalidDataException>(() => document.ReadCover());
    }

    [Fact]
    public void LeaveOpenAndDispose()
    {
        var bytes = Synthetic.Bimx(Synthetic.SimplePng(), Trailing);
        var kept = new MemoryStream(bytes);
        BimxDocument.Open(kept, leaveOpen: true).Dispose();
        Assert.True(kept.CanRead);

        var owned = new MemoryStream(bytes);
        var document = BimxDocument.Open(owned);
        document.Dispose();
        Assert.False(owned.CanRead);
        Assert.Throws<ObjectDisposedException>(() => document.ReadCover());
    }

    [Fact]
    public void OpenByPathReadsTheFileAndReleasesItOnFailure()
    {
        string path = Path.GetTempFileName();
        try
        {
            var png = Synthetic.SimplePng();
            File.WriteAllBytes(path, Synthetic.Bimx(png, Trailing));
            using (var document = BimxDocument.Open(path))
                Assert.Equal(png, document.ReadCover());
            File.WriteAllBytes(path, Synthetic.Fdb(new byte[40]));
            Assert.Throws<InvalidDataException>(() => BimxDocument.Open(path));
            File.Delete(path); // the failed open released the file
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NonSeekableStreamIsRejected() =>
        Assert.Throws<ArgumentException>(() => BimxDocument.Open(new NonSeekableStream(Synthetic.Bimx(Synthetic.SimplePng(), []))));

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
