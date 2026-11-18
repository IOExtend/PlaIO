using Xunit;

namespace PlaIO.Tests;

/// <summary>The decoded global stream and the extent reads: the stream is the file decoded in block order, and a
/// directory record's position and count name a byte extent of it — including bytes produced by a mode 2 block.</summary>
public class PlaStreamTests
{
    [Fact]
    public void ExtentsAddressTheDecodedStreamNotTheRawFile()
    {
        // Block A: raw "ABCDEFGH". Block B: mode 2 producing "wxyz" (token 0x40 = 4 literals).
        var lz = Synthetic.Concat([0x40], "wxyz"u8.ToArray(), [0xFF, 0xFE]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "ABCDEFGH"u8.ToArray()),
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 4))));

        var stream = document.OpenStream();
        Assert.Equal(12, stream.Length);
        Assert.Equal("CDE"u8.ToArray(), stream.ReadExtent(2, 3));   // inside the raw block
        Assert.Equal("wxyz"u8.ToArray(), stream.ReadExtent(8, 4));  // inside the mode-2 output
    }

    [Fact]
    public void RecordExtentReadsTheObjectData()
    {
        var data = "0123456789"u8.ToArray();
        var record = new ObjectDirectoryRecord(7, 4, 5, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, data))));

        var stream = document.OpenStream();
        Assert.Equal("45678"u8.ToArray(), stream.ReadExtent(record));
    }

    [Fact]
    public void AZeroLengthExtentAtTheEndIsEmpty()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()))));

        var stream = document.OpenStream();
        Assert.Empty(stream.ReadExtent(2, 0));
        Assert.Empty(stream.ReadExtent(new ObjectDirectoryRecord(0, 2, 0, Guid.NewGuid())));
        Assert.Equal("AB"u8.ToArray(), stream.ReadExtent(0, stream.Length));
    }

    [Fact]
    public void ExtentBoundsPastTheStreamAreRejected()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()))));

        var stream = document.OpenStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadExtent(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadExtent(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadExtent(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadExtent(3, 0));
    }

    [Fact]
    public void ARecordExtentPastTheStreamIsRejected()
    {
        var guid = Guid.NewGuid();
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()))));

        var stream = document.OpenStream();
        var e = Assert.Throws<InvalidDataException>(() =>
            stream.ReadExtent(new ObjectDirectoryRecord(3, 1, 10, guid)));
        Assert.Contains(guid.ToString(), e.Message);
    }

    [Fact]
    public void ARecordPositionWiderThanALongIsRejectedAsPastTheStream()
    {
        var guid = Guid.NewGuid();
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()))));

        var stream = document.OpenStream();
        var e = Assert.Throws<InvalidDataException>(() =>
            stream.ReadExtent(new ObjectDirectoryRecord(3, ulong.MaxValue, 1, guid)));
        Assert.Contains(guid.ToString(), e.Message);
    }

    [Fact]
    public void ExtentsPastASkippedBlockAreRejectedAsShifted()
    {
        // Block A: raw "AB". Block B: a mode 2 block that does not decode (its match distance reaches past the decoded
        // stream, in both the strict and the buffer-mode attempt). Block C: raw "CD", its bytes are shifted in the
        // stream by the skipped block.
        var brokenLz = Synthetic.Concat([0x40], "ABCD"u8.ToArray(), [0xFF, 0xFF], [0x10]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()),
            Synthetic.Block(PlaBlockMode.Lz, brokenLz, unpacked: 6),
            Synthetic.Block(PlaBlockMode.Raw, "CD"u8.ToArray()))));

        var stream = document.OpenStream();
        Assert.NotNull(stream.UnreliableFrom);
        Assert.Equal(2, stream.UnreliableFrom);
        // Before the boundary the reads are exact.
        Assert.Equal("AB"u8.ToArray(), stream.ReadExtent(0, 2));
        // At and past the boundary the offsets are shifted: a read of the following record reports it.
        var e = Assert.Throws<InvalidDataException>(() => stream.ReadExtent(2, 2));
        Assert.Contains("shifted", e.Message);
        Assert.Throws<InvalidDataException>(() => stream.ReadExtent(new ObjectDirectoryRecord(3, 2, 2, Guid.NewGuid())));
    }

    [Fact]
    public void AShortZeroMaskBlockAtAGroupBoundaryMakesLaterExtentsUnreliable()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()),
            Synthetic.Block(PlaBlockMode.ZeroMask, [1, 9], unpacked: 16),
            Synthetic.Block(PlaBlockMode.Raw, "CDEFGHIJKLMN"u8.ToArray()))));

        var stream = document.OpenStream();
        Assert.Equal(2, stream.UnreliableFrom);
        Assert.Equal("AB"u8.ToArray(), stream.ReadExtent(0, 2));
        Assert.Throws<InvalidDataException>(() => stream.ReadExtent(new ObjectDirectoryRecord(3, 2, 2, Guid.Empty)));
    }

    [Fact]
    public void AZeroLengthExtentAtTheUnreliableBoundaryIsStillRejected()
    {
        // A zero-mask block whose stream ends inside a group does not decode; the record after it is shifted.
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, "AB"u8.ToArray()),
            Synthetic.Block(PlaBlockMode.ZeroMask, [0x80], unpacked: 8))));
        var stream = document.OpenStream();
        Assert.Equal(2, stream.UnreliableFrom);
        Assert.Throws<InvalidDataException>(() => stream.ReadExtent(2, 0));
    }
}
