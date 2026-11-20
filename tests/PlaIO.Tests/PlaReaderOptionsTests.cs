using PlaIO.Internal;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public class PlaReaderOptionsTests
{
    [Fact]
    public void DefaultsMatchTheReaderLimits()
    {
        var options = PlaReaderOptions.Default;
        Assert.Equal(FdbFormat.MaxGlobalStreamBytes, options.MaxGlobalStreamBytes);
        Assert.Equal(FdbFormat.MaxDecodedPoolBytes, options.MaxDecodedPoolBytes);
        Assert.Equal(FdbFormat.MaxCoverLength, options.MaxCoverBytes);
    }

    [Fact]
    public void ABlockPastTheGlobalStreamCapIsRejectedBeforeItIsDecoded()
    {
        var bytes = Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, "ABCD"u8.ToArray()));
        using var document = PlaDocument.Open(new MemoryStream(bytes),
            options: new PlaReaderOptions { MaxGlobalStreamBytes = 3 });

        // The cap is a property of the global window: a single payload read stays under the scanner's own limits.
        Assert.Equal("ABCD"u8.ToArray(), document.ReadPayload(document.Blocks[0]));
        var e = Assert.Throws<InvalidDataException>(() => document.OpenStream());
        Assert.Contains("cap", e.Message);
    }

    [Fact]
    public void AGlobalStreamAtTheCapIsDecoded()
    {
        var bytes = Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, "CD"u8.ToArray()));
        using var document = PlaDocument.Open(new MemoryStream(bytes),
            options: new PlaReaderOptions { MaxGlobalStreamBytes = 4 });

        Assert.Equal(4, document.OpenStream().Length);
    }

    [Fact]
    public void APoolPastTheCapIsRejected()
    {
        var bytes = Synthetic.Fdb(Synthetic.QuickLzPool(
            (3, Synthetic.QuickLzFrame([1, 2, 3], 3, flags: 0x44))));
        using var document = PlaDocument.Open(new MemoryStream(bytes),
            options: new PlaReaderOptions { MaxDecodedPoolBytes = 2 });

        var pool = Assert.Single(document.FindCompressedPools());
        Assert.Equal(3, pool.TotalDecompressed);
        var e = Assert.Throws<InvalidDataException>(() => pool.Decode());
        Assert.Contains("cap", e.Message);
    }

    [Fact]
    public void TheSkippedBlocksOfTheStreamAreExposedInFileOrder()
    {
        // The middle block does not decode (see PlaStreamTests.ExtentsPastASkippedBlockAreRejectedAsShifted).
        var brokenLz = Synthetic.Concat([0x40], "ABCD"u8.ToArray(), [0xFF, 0xFF], [0x10]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()),
            Synthetic.Block(PlaBlockMode.Lz, brokenLz, unpacked: 6),
            Synthetic.Block(PlaBlockMode.Raw, "CD"u8.ToArray()))));

        var stream = document.OpenStream();
        var skipped = Assert.Single(stream.SkippedBlocks);
        Assert.Equal(document.Blocks[1].Offset, skipped.Offset);
        Assert.Equal(PlaBlockMode.Lz, skipped.Mode);
        Assert.Equal(2, stream.UnreliableFrom);
    }
}
