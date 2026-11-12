using PlaIO.Internal;
using Xunit;

namespace PlaIO.Tests;

public class PayloadDecoderTests
{
    private static (byte[] Output, int Consumed) Decode(byte[] source, int unpacked)
    {
        var output = new byte[unpacked];
        int consumed = PayloadDecoder.DecodeZeroMask(source, output);
        return (output, consumed);
    }

    [Fact]
    public void FullMaskCopiesEightBytes()
    {
        var (output, consumed) = Decode([0xFF, 1, 2, 3, 4, 5, 6, 7, 8], 8);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], output);
        Assert.Equal(9, consumed);
    }

    [Fact]
    public void EmptyMaskWritesEightZeros()
    {
        var (output, consumed) = Decode([0x00], 8);
        Assert.Equal(new byte[8], output);
        Assert.Equal(1, consumed);
    }

    [Fact]
    public void MixedMaskTakesBytesForSetBitsAndZerosForClearBits()
    {
        var (output, consumed) = Decode([0b1010_0101, 0xA1, 0xA2, 0xA3, 0xA4], 8);
        Assert.Equal([0xA1, 0, 0xA2, 0, 0, 0xA3, 0, 0xA4], output);
        Assert.Equal(5, consumed);
    }

    [Fact]
    public void BitsAreReadFromTheLeastSignificant()
    {
        Assert.Equal([0x55, 0, 0, 0, 0, 0, 0, 0], Decode([0x01, 0x55], 8).Output);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0x55], Decode([0x80, 0x55], 8).Output);
    }

    [Fact]
    public void PartialLastGroupConsumesOnlyItsBits()
    {
        // Ten bytes: a full group, then bits 0..1 of a second mask; its set bits 2..7 consume nothing.
        var (output, consumed) = Decode([0x00, 0xFE, 0x77], 10);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0, 0x77], output);
        Assert.Equal(3, consumed);

        var (eleven, consumedEleven) = Decode([0x00, 0b1111_1101, 0x61, 0x62], 11);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0x61, 0, 0x62], eleven);
        Assert.Equal(4, consumedEleven);
    }

    [Fact]
    public void ZeroLengthOutputConsumesNothing()
    {
        var (output, consumed) = Decode([0xFF, 1], 0);
        Assert.Empty(output);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void StreamEndingBeforeAMaskThrows()
    {
        var e = Assert.Throws<InvalidDataException>(() => Decode([0x00], 16));
        Assert.Contains("8 of 16", e.Message);
    }

    [Fact]
    public void StreamEndingBeforeADataByteThrows()
    {
        var e = Assert.Throws<InvalidDataException>(() => Decode([0x03, 0x41], 8));
        Assert.Contains("1 of 8", e.Message);
    }

    [Fact]
    public void LeftoverInputIsAllowedAndReported()
    {
        var (output, consumed) = Decode([0x00, 0x99, 0x99], 8);
        Assert.Equal(new byte[8], output);
        Assert.Equal(1, consumed);
    }

    [Fact]
    public void EncoderRoundTrips()
    {
        var random = new Random(3);
        var plain = Enumerable.Range(0, 1001).Select(_ => random.Next(3) == 0 ? (byte)random.Next(1, 256) : (byte)0).ToArray();
        var encoded = Synthetic.ZeroMask(plain);
        var (output, consumed) = Decode(encoded, plain.Length);
        Assert.Equal(plain, output);
        Assert.Equal(encoded.Length, consumed);
    }
}
