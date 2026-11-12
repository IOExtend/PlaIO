using PlaIO.Internal;
using Xunit;

namespace PlaIO.Tests;

public class BlockScannerTests
{
    private static List<PlaBlock> Scan(byte[] data, int windowSize = 1 << 20, int maxBlocks = FdbFormat.MaxBlocks) =>
        new BlockScanner(new MemoryStream(data), data.Length, windowSize, maxBlocks).Scan();

    private static byte[] RawBlock(int size, byte fill = 0x11) => Synthetic.Block(PlaBlockMode.Raw, Enumerable.Repeat(fill, size).ToArray());

    [Fact]
    public void ChainFromOffsetThirtyTwoIsFoundInOrder()
    {
        byte[][] parts =
        [
            RawBlock(20),
            Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(new byte[50]), unpacked: 50, guid: Guid.NewGuid()),
            Synthetic.Block(PlaBlockMode.Lz, new byte[30], tag: "DY", unpacked: 90, extra: 5),
            RawBlock(300),
        ];
        var blocks = Scan(Synthetic.Fdb(parts));
        Assert.Equal(4, blocks.Count);
        long offset = 32;
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.Equal(offset, blocks[i].Offset);
            Assert.Equal(offset + parts[i].Length, blocks[i].End);
            offset += parts[i].Length;
        }
        Assert.Equal([PlaBlockMode.Raw, PlaBlockMode.ZeroMask, PlaBlockMode.Lz, PlaBlockMode.Raw], blocks.Select(b => b.Mode));
    }

    [Fact]
    public void JunkAndFalseTagsBetweenBlocksAreSkipped()
    {
        var falseTag = Synthetic.Block(PlaBlockMode.Raw, new byte[4], flagsOverride: 0x0080); // bad marker
        var data = Synthetic.Fdb(RawBlock(16), "junk BL DY BD"u8.ToArray(), falseTag, new byte[16], RawBlock(24));
        var blocks = Scan(data);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(32, blocks[0].Offset);
        Assert.Equal(data.Length, blocks[1].End);
    }

    [Fact]
    public void OneByteStepFindsBlocksAtOddOffsets()
    {
        // An odd-length payload puts the next block at an odd offset; odd-length junk shifts a later one back to even.
        var data = Synthetic.Fdb(RawBlock(18), RawBlock(20), new byte[3], RawBlock(8), new byte[5], RawBlock(9));
        var blocks = Scan(data);
        Assert.Equal(4, blocks.Count);
        Assert.Equal(32 + 13 + 18, blocks[1].Offset);
        Assert.Equal(1, blocks[1].Offset % 2);
        Assert.Equal(blocks[1].End + 3, blocks[2].Offset);
        Assert.Equal(blocks[2].End + 5, blocks[3].Offset);
    }

    [Fact]
    public void HeaderInsideAnAcceptedPayloadIsNotIndexed()
    {
        var inner = RawBlock(10);
        var outer = Synthetic.Block(PlaBlockMode.Raw, Synthetic.Concat(new byte[5], inner, new byte[5]));
        var blocks = Scan(Synthetic.Fdb(outer));
        Assert.Equal(32, Assert.Single(blocks).Offset);
    }

    [Fact]
    public void TruncatedLastBlockIsIgnored()
    {
        var last = RawBlock(40);
        Assert.Single(Scan(Synthetic.Fdb(RawBlock(10), last[..^1])));
        Assert.Single(Scan(Synthetic.Fdb(RawBlock(10), last[..8])));
    }

    [Fact]
    public void HeaderOnlyFileHasNoBlocks() => Assert.Empty(Scan(Synthetic.Header()));

    [Fact]
    public void BlockLimitIsEnforced()
    {
        var data = Synthetic.Fdb(RawBlock(4), RawBlock(4), RawBlock(4), RawBlock(4));
        Assert.Equal(3, Scan(Synthetic.Fdb(RawBlock(4), RawBlock(4), RawBlock(4)), maxBlocks: 3).Count);
        var e = Assert.Throws<InvalidDataException>(() => Scan(data, maxBlocks: 3));
        Assert.Contains("3", e.Message);
    }

    [Fact]
    public void WindowBelowSixtyFourIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockScanner(new MemoryStream(), 0, windowSize: 63));

    [Fact]
    public void ShortStreamIsReportedAsMalformed()
    {
        var data = Synthetic.Fdb(RawBlock(10));
        Assert.Throws<InvalidDataException>(() => new BlockScanner(new MemoryStream(data), data.Length + 100).Scan());
    }

    [Theory]
    [InlineData(64)]
    [InlineData(333)]
    [InlineData(1 << 20)]
    public void WindowedScanEqualsNaiveScan(int windowSize)
    {
        for (int seed = 0; seed < 20; seed++)
        {
            byte[] data = RandomContainer(new Random(seed));
            var expected = NaiveScanner.Scan(data);
            var actual = Scan(data, windowSize);
            Assert.True(expected.Count > 0);
            Assert.Equal(Describe(expected), Describe(actual));
        }
    }

    private static List<string> Describe(List<PlaBlock> blocks) =>
        blocks.Select(b => $"{b.Offset}:{b.Flags:x}:{b.PackedSize}:{b.UnpackedSize}:{b.Mode}:{b.PayloadOffset}").ToList();

    /// <summary>Junk (rich in tag bytes) with planted blocks of every mode, odd and even sizes, payloads longer than the
    /// small windows, so headers straddle window edges.</summary>
    private static byte[] RandomContainer(Random random)
    {
        var parts = new List<byte[]> { Synthetic.Header() };
        int count = random.Next(5, 40);
        for (int i = 0; i < count; i++)
        {
            int junk = random.Next(0, 90);
            var bytes = new byte[junk];
            for (int j = 0; j < junk; j++) bytes[j] = random.Next(4) == 0 ? "BLDY"u8[random.Next(4)] : (byte)random.Next(256);
            parts.Add(bytes);

            var mode = (PlaBlockMode)random.Next(3);
            var payload = Synthetic.RandomBytes(random, random.Next(0, 700));
            parts.Add(Synthetic.Block(mode, payload, tag: random.Next(2) == 0 ? "BL" : "DY", ver: (byte)random.Next(2),
                unpacked: mode == PlaBlockMode.Raw ? null : (uint)(payload.Length * 2),
                guid: random.Next(2) == 0 ? Synthetic.RandomGuid(random) : null,
                extra: random.Next(3) == 0 ? (uint)random.Next() : null));
        }
        return Synthetic.Concat(parts.ToArray());
    }
}
