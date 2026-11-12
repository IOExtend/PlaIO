using Xunit;

namespace PlaIO.Tests;

public class FdbHeaderTests
{
    private static PlaDocument Open(byte[] bytes) => PlaDocument.Open(new MemoryStream(bytes));

    [Fact]
    public void FieldsAreReadLittleEndian()
    {
        using var document = Open(Synthetic.Header(0x1234, 0x5678, 0x9ABCDEF0, new byte[32]));
        Assert.Equal(0x1234, document.Header.LayoutVersion);
        Assert.Equal(0x5678, document.Header.FormatVersion);
        Assert.Equal(0x9ABCDEF0u, document.Header.Field12);
    }

    [Fact]
    public void BytesSixteenToSixtyThreeAreKeptRaw()
    {
        var bytes = Synthetic.Header(tail: Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray());
        for (int i = 16; i < 32; i++) bytes[i] = (byte)(i * 7);
        using var document = Open(bytes);
        Assert.Equal(bytes, document.Header.Raw.ToArray());
    }

    [Fact]
    public void UnusualVersionsAreNotRejected()
    {
        using var document = Open(Synthetic.Header(0xFFFF, 0, 0));
        Assert.Equal(0xFFFF, document.Header.LayoutVersion);
        Assert.Equal(0, document.Header.FormatVersion);
    }

    [Fact]
    public void ShortFilesKeepWhatTheyHave()
    {
        using (var forty = Open(Synthetic.Header(tail: new byte[8])))
        {
            Assert.Equal(40, forty.Header.Raw.Length);
            Assert.Empty(forty.Blocks);
        }
        using var minimal = Open(Synthetic.Header());
        Assert.Equal(32, minimal.Header.Raw.Length);
        Assert.Empty(minimal.Blocks);
    }

    [Fact]
    public void ThirtyOneBytesIsTooShort()
    {
        var e = Assert.Throws<InvalidDataException>(() => Open(Synthetic.Header()[..31]));
        Assert.Contains("32", e.Message);
    }

    [Fact]
    public void WrongSignatureIsRejected()
    {
        var bytes = Synthetic.Header();
        bytes[4] = (byte)'X';
        var e = Assert.Throws<InvalidDataException>(() => Open(bytes));
        Assert.Contains("Unknown", e.Message);
    }

    [Fact]
    public void BimxBytesAreRejectedNamingTheKind()
    {
        var e = Assert.Throws<InvalidDataException>(() => Open(Synthetic.Bimx(Synthetic.SimplePng(), new byte[40])));
        Assert.Contains("Bimx", e.Message);
    }
}
