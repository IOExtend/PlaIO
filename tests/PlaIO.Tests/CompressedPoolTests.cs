using PlaIO.Model;
using System.Buffers.Binary;
using Xunit;

namespace PlaIO.Tests;

public class CompressedPoolTests
{
    [Fact]
    public void ReadsShortAndLongStoredFrames()
    {
        byte[] first = "ABC"u8.ToArray();
        byte[] second = Enumerable.Repeat((byte)'Z', 220).ToArray();
        byte[] bytes = Synthetic.Fdb(Synthetic.QuickLzPool(((uint)first.Length, Synthetic.QuickLzFrame(first, first.Length, flags: 0x44)),
            ((uint)second.Length, Synthetic.QuickLzFrame(second, second.Length, flags: 0x46))));
        using var doc = PlaDocument.Open(new MemoryStream(bytes));

        var pool = Assert.Single(doc.FindCompressedPools());
        Assert.Equal(32, pool.Offset);
        Assert.Equal(bytes.Length, pool.End);
        Assert.Equal(2, pool.BlockCount);
        Assert.Equal(first.Length + second.Length, pool.TotalDecompressed);
        Assert.Equal(Synthetic.Concat(first, second), pool.Decode());
    }

    [Fact]
    public void DecodesAnOverlappingQuickLzMatchAndLiteralTail()
    {
        int word = 'A' | ('B' << 8) | ('C' << 16);
        ushort token = (ushort)((((word ^ (word >> 12)) & 0xFFF) << 4) | 10);
        byte[] tokenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tokenBytes, token);
        byte[] body = Synthetic.Concat([0x10, 0, 0, 0x80], "ABCD"u8.ToArray(), tokenBytes, "0123456789"u8.ToArray());
        byte[] expected = "ABCDABCDABCDABCD0123456789"u8.ToArray();
        using var doc = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.QuickLzPool(((uint)expected.Length, Synthetic.QuickLzFrame(body, expected.Length, flags: 0x45))))));

        Assert.Equal(expected, Assert.Single(doc.FindCompressedPools()).Decode());
    }

    [Fact]
    public void DecodedPoolCanBeLargerThanTheContainer()
    {
        byte[] body = Synthetic.Concat([0x10, 0, 0, 0x80], new byte[4], [0, 0, 190], new byte[11]);
        byte[] bytes = Synthetic.Fdb(Synthetic.QuickLzPool((205, Synthetic.QuickLzFrame(body, 205, flags: 0x47))));
        using var doc = PlaDocument.Open(new MemoryStream(bytes));

        Assert.True(bytes.Length < 205);
        Assert.Equal(new byte[205], Assert.Single(doc.FindCompressedPools()).Decode());
    }

    [Fact]
    public void MarkerInsideAnIndexedPayloadIsIgnored()
    {
        byte[] pool = Synthetic.QuickLzPool((3, Synthetic.QuickLzFrame([1, 2, 3], 3, flags: 0x44)));
        using var doc = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, pool),
            pool)));

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(block.End, Assert.Single(doc.FindCompressedPools()).Offset);
    }

    [Fact]
    public void FindsMarkerAcrossTheScanWindowBoundary()
    {
        byte[] bytes = Synthetic.Fdb(new byte[64 * 1024 - 8], Synthetic.QuickLzPool());
        using var doc = PlaDocument.Open(new MemoryStream(bytes));
        var pool = Assert.Single(doc.FindCompressedPools());
        Assert.Equal(bytes.Length, pool.End);
        Assert.Empty(pool.Decode());
    }

    [Fact]
    public void RejectsATruncatedFrameAtEndOfFile()
    {
        byte[] frame = Synthetic.QuickLzFrame([1, 2, 3], 3, flags: 0x44);
        using var doc = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.QuickLzPool((3, frame))[..^1])));

        Assert.Empty(doc.FindCompressedPools());
    }

    [Fact]
    public void APoolWithInconsistentLengthsDoesNotHideAValidPool()
    {
        byte[] frame = Synthetic.QuickLzFrame([1, 2, 3], 3, flags: 0x44);
        byte[] bytes = Synthetic.Fdb(Synthetic.QuickLzPool((4, frame)), Synthetic.QuickLzPool((3, frame)));
        using var doc = PlaDocument.Open(new MemoryStream(bytes));

        var pool = Assert.Single(doc.FindCompressedPools());
        Assert.Equal(bytes.Length, pool.End);
        Assert.Equal(new byte[] { 1, 2, 3 }, pool.Decode());
    }

    [Fact]
    public void DecodeRejectsAnUninitializedMatch()
    {
        byte[] frame = Synthetic.QuickLzFrame([1, 0, 0, 0x80, 1, 0, 0, 0], 3, flags: 0x45);
        using var doc = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.QuickLzPool((3, frame)))));
        var pool = Assert.Single(doc.FindCompressedPools());
        Assert.Throws<InvalidDataException>(pool.Decode);
    }

    [Fact]
    public void DecodeAfterDisposeThrows()
    {
        var doc = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.QuickLzPool())));
        var pool = Assert.Single(doc.FindCompressedPools());
        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(pool.Decode);
        Assert.Throws<ObjectDisposedException>(doc.FindCompressedPools);
    }

    [SkippableFact]
    public void SavedFilePoolFramesDecodeToTheirDeclaredSize()
    {
        using var corpus = new SampleCorpus();
        int decoded = 0;
        foreach (var sample in corpus.Fdb)
        {
            foreach (var pool in sample.Document.FindCompressedPools())
            {
                Assert.Equal(pool.TotalDecompressed, pool.Decode().LongLength);
                decoded++;
            }
        }
        if (decoded == 0) throw TestFiles.Missing("FDB fixture with QuickLZ pools");
    }

}
