using Xunit;

namespace PlaIO.Tests;

public class PlaDocumentTests
{
    private static readonly byte[] RawPayload = Enumerable.Range(0, 33).Select(i => (byte)(i + 1)).ToArray();
    private static readonly byte[] Plain = [0, 0, 5, 0, 7, 7, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0];

    private static byte[] OneBlockOfEachMode() => Synthetic.Fdb(
        Synthetic.Block(PlaBlockMode.Raw, RawPayload),
        Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(Plain), unpacked: (uint)Plain.Length, guid: Guid.NewGuid(), extra: 1),
        Synthetic.Block(PlaBlockMode.Lz, new byte[12], tag: "DY", unpacked: 40));

    [Fact]
    public void ReadsModesZeroAndOneAndRefusesModeTwo()
    {
        using var document = PlaDocument.Open(new MemoryStream(OneBlockOfEachMode()));
        Assert.Equal([PlaBlockMode.Raw, PlaBlockMode.ZeroMask, PlaBlockMode.Lz], document.Blocks.Select(b => b.Mode));
        Assert.Equal(RawPayload, document.ReadPayload(document.Blocks[0]));
        Assert.Equal(Plain, document.ReadPayload(document.Blocks[1]));
        var e = Assert.Throws<NotSupportedException>(() => document.ReadPayload(document.Blocks[2]));
        Assert.Contains("mode 2", e.Message);
    }

    [Fact]
    public void ImpossibleExpansionIsRejectedBeforeAllocating()
    {
        // 2^28 bytes cannot come from 1,000,000 packed bytes (at most 8 per mask byte).
        var data = Synthetic.Fdb(Synthetic.Block(PlaBlockMode.ZeroMask, new byte[1_000_000], unpacked: 1u << 28));
        using var document = PlaDocument.Open(new MemoryStream(data));
        var block = Assert.Single(document.Blocks);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => document.ReadPayload(block));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }

    [Fact]
    public void ShortZeroMaskStreamNamesTheBlock()
    {
        var data = Synthetic.Fdb(Synthetic.Block(PlaBlockMode.ZeroMask, [0x00, 0x00, 0x01], unpacked: 24));
        using var document = PlaDocument.Open(new MemoryStream(data));
        var e = Assert.Throws<InvalidDataException>(() => document.ReadPayload(document.Blocks[0]));
        Assert.Contains("0x20", e.Message);
        Assert.Contains("16 of 24", e.Message);
    }

    [Fact]
    public void ShortRawReadIsMalformedData()
    {
        // The block fits the declared length but the data stops early; the payload is larger than the scan window, so
        // only reading the payload reaches the missing bytes.
        var data = Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, new byte[1_500_000]));
        using var document = PlaDocument.Open(new LyingLengthStream(data[..^100], data.Length));
        var block = Assert.Single(document.Blocks);
        Assert.Throws<InvalidDataException>(() => document.ReadPayload(block));
    }

    [Fact]
    public void BlockOfAnotherDocumentIsRejected()
    {
        using var large = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, new byte[500]))));
        using var small = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, new byte[5]))));
        Assert.Throws<ArgumentException>(() => small.ReadPayload(large.Blocks[0]));

        // Same layout and length: only the owner may read it.
        byte[] data = Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, new byte[50]));
        using var first = PlaDocument.Open(new MemoryStream(data));
        using var second = PlaDocument.Open(new MemoryStream(data));
        Assert.Throws<ArgumentException>(() => second.ReadPayload(first.Blocks[0]));
        Assert.Equal(new byte[50], first.ReadPayload(first.Blocks[0]));
    }

    [Fact]
    public void NonSeekableStreamIsRejected() =>
        Assert.Throws<ArgumentException>(() => PlaDocument.Open(new NonSeekableStream(Synthetic.Fdb())));

    [Fact]
    public void LeaveOpenIsHonoured()
    {
        var kept = new MemoryStream(Synthetic.Fdb());
        PlaDocument.Open(kept, leaveOpen: true).Dispose();
        Assert.True(kept.CanRead);

        var owned = new MemoryStream(Synthetic.Fdb());
        PlaDocument.Open(owned).Dispose();
        Assert.False(owned.CanRead);
    }

    [Fact]
    public void ReadAfterDisposeThrows()
    {
        var document = PlaDocument.Open(new MemoryStream(OneBlockOfEachMode()));
        var block = document.Blocks[0];
        document.Dispose();
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(() => document.ReadPayload(block));
        Assert.Throws<ObjectDisposedException>(() => document.FindDirectories());
    }

    [Fact]
    public void OpenByPathReadsTheFileAndReleasesItOnFailure()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, OneBlockOfEachMode());
            using (var document = PlaDocument.Open(path))
                Assert.Equal(RawPayload, document.ReadPayload(document.Blocks[0]));
            File.WriteAllBytes(path, Synthetic.Bimx(Synthetic.SimplePng(), new byte[40]));
            Assert.Throws<InvalidDataException>(() => PlaDocument.Open(path));
            File.Delete(path); // the failed open released the file
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DirectoriesAreSearchedInRawBlocksOnly()
    {
        var payload = Synthetic.DirectoryPayload(4, Synthetic.Extents(100, new Random(6)), tail: 20);
        var data = Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(payload), unpacked: (uint)payload.Length),
            Synthetic.Block(PlaBlockMode.Raw, new byte[3000]),
            Synthetic.Block(PlaBlockMode.Raw, payload));
        using var document = PlaDocument.Open(new MemoryStream(data));
        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal(payload, document.ReadPayload(document.Blocks[0]));
        var directory = Assert.Single(document.FindDirectories());
        Assert.Same(document.Blocks[2], directory.Block);
        Assert.Equal(4, directory.RecordOffset);
        Assert.Equal(100, directory.Records.Count);
    }

    /// <summary>A memory stream that reports a larger length than the data it holds.</summary>
    private sealed class LyingLengthStream(byte[] data, long length) : MemoryStream(data)
    {
        public override long Length => length;
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
