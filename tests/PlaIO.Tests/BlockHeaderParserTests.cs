using PlaIO.Internal;
using Xunit;

namespace PlaIO.Tests;

public class BlockHeaderParserTests
{
    private const long LargeFile = 1L << 40;

    private static PlaBlock Parse(byte[] bytes, long offset = 0, long? fileLength = null)
    {
        Assert.True(BlockHeaderParser.TryParse(bytes, offset, fileLength ?? offset + bytes.Length, out var block));
        return block;
    }

    private static bool Accepts(byte[] bytes, long? fileLength = null) =>
        BlockHeaderParser.TryParse(bytes, 0, fileLength ?? bytes.Length, out _);

    [Fact]
    public void MinimalRawBlockHasAThirteenByteHeader()
    {
        var block = Parse(Synthetic.Block(PlaBlockMode.Raw, [1, 2, 3, 4, 5]), offset: 100);
        Assert.Equal(100, block.Offset);
        Assert.Equal("BL", block.Tag);
        Assert.Equal(13, block.HeaderLength);
        Assert.Equal(PlaBlockMode.Raw, block.Mode);
        Assert.Equal(5, block.PackedSize);
        Assert.Equal(5, block.UnpackedSize);
        Assert.Null(block.Guid);
        Assert.Null(block.Extra);
        Assert.Equal(113, block.PayloadOffset);
        Assert.Equal(118, block.End);
    }

    [Fact]
    public void DyWithGuidZeroMaskAndExtra()
    {
        var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var block = Parse(Synthetic.Block(PlaBlockMode.ZeroMask, new byte[20], tag: "DY", ver: 1, unpacked: 77, guid: guid, extra: 0xCAFEBABE));
        Assert.Equal("DY", block.Tag);
        Assert.Equal(1, block.Version);
        Assert.Equal(37, block.HeaderLength);
        Assert.Equal(guid, block.Guid);
        Assert.Equal(0xCAFEBABEu, block.Extra);
        Assert.Equal(PlaBlockMode.ZeroMask, block.Mode);
        Assert.Equal(20, block.PackedSize);
        Assert.Equal(77, block.UnpackedSize);
    }

    [Fact]
    public void GuidIsStoredInDotNetByteOrder()
    {
        var guid = Guid.NewGuid();
        var bytes = Synthetic.Block(PlaBlockMode.Raw, [], guid: guid);
        Assert.Equal(guid.ToByteArray(), bytes[11..27]);
        Assert.Equal(guid, Parse(bytes).Guid);
    }

    [Fact]
    public void SizeClassSevenUsesAFourByteSizeField()
    {
        var header = Synthetic.Block(PlaBlockMode.Raw, [], packed: 0x10000);
        var block = Parse(header, fileLength: LargeFile);
        Assert.Equal(7, block.SizeClass);
        Assert.Equal(15, block.HeaderLength);
        Assert.Equal(0x10000, block.PackedSize);
    }

    [Fact]
    public void SizeClassFifteenUsesAnEightBytePackedSizeField()
    {
        var header = Synthetic.Block(PlaBlockMode.Lz, [], packed: 0x100000000, unpacked: 1000);
        var block = Parse(header, fileLength: LargeFile);
        Assert.Equal(15, block.SizeClass);
        Assert.Equal(23, block.HeaderLength);
        Assert.Equal(0x100000000, block.PackedSize);
        Assert.Equal(1000, block.UnpackedSize);
    }

    [Theory]
    [InlineData(0x8080, PlaBlockMode.Raw)]
    [InlineData(0x8180, PlaBlockMode.ZeroMask)]
    [InlineData(0x8280, PlaBlockMode.Lz)]
    [InlineData(0x8380, PlaBlockMode.ZeroMask)]
    public void ModeComesFromFlags(int flags, PlaBlockMode mode)
    {
        var block = Parse(Synthetic.Block(PlaBlockMode.Raw, new byte[3], unpacked: 3, flagsOverride: (ushort)flags));
        Assert.Equal(mode, block.Mode);
    }

    [Fact]
    public void IdKindAndFlagTenAreKeptRaw()
    {
        var block = Parse(Synthetic.Block(PlaBlockMode.Raw, new byte[3], id: 0xDEADBEEF, kind: 0xBEEF, flagsOverride: 0x8090));
        Assert.Equal(0xDEADBEEFu, block.Id);
        Assert.Equal(0xBEEF, block.Kind);
        Assert.Equal(0x8090, block.Flags);
        Assert.Equal(13, block.HeaderLength); // the kind value does not change the header layout
    }

    [Fact]
    public void WrongTagIsRejected()
    {
        var bytes = Synthetic.Block(PlaBlockMode.Raw, new byte[3]);
        bytes[1] = (byte)'X';
        Assert.False(Accepts(bytes));
    }

    [Fact]
    public void VersionTwoIsRejected() => Assert.False(Accepts(Synthetic.Block(PlaBlockMode.Raw, new byte[3], ver: 2)));

    [Theory]
    [InlineData(0x0080)] // 0x8000 missing
    [InlineData(0xC080)] // 0x4000 set
    [InlineData(0x80C0)] // 0x0040 set
    [InlineData(0x0000)]
    public void BadMarkerIsRejected(int flags) =>
        Assert.False(Accepts(Synthetic.Block(PlaBlockMode.Raw, new byte[3], flagsOverride: (ushort)flags)));

    [Fact]
    public void ClassMismatchIsRejected() =>
        Assert.False(Accepts(Synthetic.Block(PlaBlockMode.Raw, new byte[20], classOverride: 2)));

    [Fact]
    public void PackedAboveLongMaxIsRejected() =>
        Assert.False(BlockHeaderParser.TryParse(Synthetic.Block(PlaBlockMode.Lz, [], packed: 0x8000000000000000, unpacked: 10),
            0, long.MaxValue, out _));

    [Fact]
    public void PayloadEndingAtEndOfFileIsAcceptedOneBytePastIsRejected()
    {
        var bytes = Synthetic.Block(PlaBlockMode.Raw, new byte[40]);
        Assert.True(Accepts(bytes, bytes.Length));
        Assert.False(Accepts(bytes, bytes.Length - 1));
    }

    [Fact]
    public void ExpansionLimitIsInclusive()
    {
        Assert.True(Accepts(Synthetic.Block(PlaBlockMode.ZeroMask, new byte[10], unpacked: 10 * 300 + 65536)));
        Assert.False(Accepts(Synthetic.Block(PlaBlockMode.ZeroMask, new byte[10], unpacked: 10 * 300 + 65537)));
    }

    [Fact]
    public void UnpackedLimitIsInclusive()
    {
        Assert.True(Accepts(Synthetic.Block(PlaBlockMode.ZeroMask, [], packed: 1_000_000, unpacked: 1u << 28), LargeFile));
        Assert.False(Accepts(Synthetic.Block(PlaBlockMode.ZeroMask, [], packed: 1_000_000, unpacked: (1u << 28) + 1), LargeFile));
    }

    [Fact]
    public void CutHeaderIsRejectedAtEveryLengthWithoutThrowing()
    {
        var bytes = Synthetic.Block(PlaBlockMode.ZeroMask, [], tag: "DY", unpacked: 0, guid: Guid.NewGuid(), extra: 1);
        Assert.Equal(37, bytes.Length);
        Assert.True(Accepts(bytes, LargeFile));
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(Accepts(bytes[..length], LargeFile), $"accepted a header cut to {length} bytes");
    }
}
