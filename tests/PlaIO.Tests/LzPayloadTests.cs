using PlaIO.Model;
using System.Buffers.Binary;
using Xunit;

namespace PlaIO.Tests;

/// <summary>The mode 2 (LZ) payload decode on synthetic containers: literal runs, back-references across
/// blocks (the global window), overlapping copies, RLE zeros, the length byte conventions and the
/// trailing slack. The byte sequences are the model verified against real files (see the format spec).</summary>
public class LzPayloadTests
{
    [Fact]
    public void LiteralsAndBackReferencesAcrossBlocks()
    {
        // Block A: raw, provides the window. Block B: mode 2, references it.
        byte[] a = "ABCDEFGH"u8.ToArray();
        // seq1: token 0x40 (lit 4) "wxyz"; dist 8 -> copies "EFG" (source at 12-8=4)
        // seq2: token 0x00 (lit 0); dist 0; len 4 -> four zeros (window is now 19 bytes)
        // seq3: token 0x00 (lit 0); dist 15 -> copies "EFGH" (source at 19-15=4, block A's bytes:
        // the back-reference reaches across the block boundary into the raw block)
        // trailing slack: FF FE
        var lz = Synthetic.Concat(
            [0x40], "wxyz"u8.ToArray(), [0x00, 0x08], [0x03],
            [0x00], [0x00, 0x00], [0x04],
            [0x00], [0x00, 0x0F], [0x04],
            [0xFF, 0xFE]);
        byte[] expected = "wxyzEFG\0\0\0\0EFGH"u8.ToArray();

        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Raw, a),
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: (uint)expected.Length))));

        var payload = document.ReadPayload(document.Blocks[1]);
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void LengthByteZeroMeans256AndHighNibbleExtendsTheLength()
    {
        // seq1: token 0x40 (lit 4) "ABCD"; dist 4, lenByte 4 -> len 4: copies the literals back,
        // giving "ABCDABCD". seq2: token 0x01 (lit 0, lenHi 1): dist 4, lenByte 0 -> len 256; the
        // overlapping copy repeats "ABCD" 64 times. seq3: token 0x10 (lit 1) "Z".
        var lz = Synthetic.Concat(
            [0x40], "ABCD"u8.ToArray(), [0x00, 0x04], [0x04],
            [0x01], [0x00, 0x04], [0x00],
            [0x10], "Z"u8.ToArray());
        byte[] expected = "ABCDABCD"u8.ToArray();
        for (int i = 0; i < 64; i++) expected = expected.Concat("ABCD"u8.ToArray()).ToArray();
        expected = expected.Concat("Z"u8.ToArray()).ToArray();

        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: (uint)expected.Length))));

        var payload = document.ReadPayload(document.Blocks.Single());
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void OverlappingCopyReplicatesARepeatingPattern()
    {
        // window tail "ABCD"; token 0x01 (lit 0, lenHi 1): dist 4, lenByte 45 -> len 0x12D = 301:
        // the 4-byte pattern repeats with a 1-byte overlap offset... the copy replicates the
        // pattern; the output length is clipped at the declared size.
        var lz = Synthetic.Concat(
            [0x40], "ABCD"u8.ToArray(), [0x00, 0x04], [0x04],
            [0x01], [0x00, 0x04], [0x2D]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 301))));

        var payload = document.ReadPayload(document.Blocks.Single());
        Assert.Equal(301, payload.Length);
        for (int i = 0; i < 301; i++) Assert.Equal("ABCD"[i % 4], (char)payload[i]);
    }

    [Fact]
    public void TrailingLiteralsEndTheBlockAndBodyTailIsSlack()
    {
        // token 0xF5: litLen 15 + ext byte 5 = 20 literals; no match follows; the body tail (2 bytes) is slack.
        var lz = Synthetic.Concat(
            [0xF5, 0x05], "ABCDEFGHIJKLMNOPQRST"u8.ToArray(),
            [0xDE, 0xAD]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 20))));

        var payload = document.ReadPayload(document.Blocks.Single());
        Assert.Equal("ABCDEFGHIJKLMNOPQRST"u8.ToArray(), payload);
    }

    [Fact]
    public void BackReferenceBeyondTheWindowIsRejected()
    {
        // token 0x00 (lit 0); dist 0x0064 = 100 > the window (0 bytes before the first match in this block)
        var lz = Synthetic.Concat([0x00], [0x00, 0x64], [0x04]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 64))));

        var ex = Assert.Throws<InvalidDataException>(() => document.ReadPayload(document.Blocks.Single()));
        Assert.Contains("back-reference distance 100", ex.Message);
    }

    [Fact]
    public void AStreamEndingWithTheBodyIsABufferBlockEvenWithoutAStreamHeader()
    {
        // token 0x40 declares 4 literals; the payload carries only 2 and ends: the stream ends with the
        // body, so the block decodes as a buffer block and the payload holds the declared buffer.
        var lz = Synthetic.Concat([0x40], [0x77, 0x78]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 4))));

        var payload = document.ReadPayload(document.Blocks.Single());
        Assert.Equal(new byte[] { 0x77, 0x78, 0, 0 }, payload);
    }

    [Theory]
    [InlineData(0x46)]
    [InlineData(0x47)]
    public void StreamHeaderIsSkippedWhenItsMarkerAndSizesMatch(byte marker)
    {
        // [u8][u32 body length][u32 unpacked][token stream]: the u32 at offset 1 equals the whole payload length and the
        // u32 at offset 5 equals the declared size, so the token stream starts after the 9-byte header.
        var tail = Synthetic.Concat([0x40], "ABCD"u8.ToArray(), [0xFF, 0xFE]);
        var header = Synthetic.Concat([marker], BitConverter.GetBytes((uint)(9 + tail.Length)), BitConverter.GetBytes(4u));
        var lz = Synthetic.Concat(header, tail);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 4))));

        Assert.Equal("ABCD"u8.ToArray(), document.ReadPayload(document.Blocks.Single()));
    }

    [Fact]
    public void LiteralRunWithBothSizesDoesNotBecomeAHeader()
    {
        byte[] expected = [10, 0, 0, 0, 9, 0, 0, 0, 42];
        byte[] lz = Synthetic.Concat([0x90], expected);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: (uint)expected.Length))));

        Assert.Equal(expected, document.ReadPayload(document.Blocks.Single()));
    }

    [Fact]
    public void ShortZeroMaskPredecessorIsNotUsedAsMatchHistory()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.ZeroMask, [1, 9], unpacked: 16),
            Synthetic.Block(PlaBlockMode.Lz, [0, 0, 8, 1], unpacked: 1))));

        Assert.Throws<InvalidDataException>(() => document.ReadPayload(document.Blocks[0]));
        Assert.Throws<InvalidDataException>(() => document.ReadPayload(document.Blocks[1]));
    }

    [Fact]
    public void ReadingLzDoesNotReadZeroMaskWriterSlack()
    {
        byte[] data = Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.ZeroMask, new byte[1024 * 1024], unpacked: 1),
            Synthetic.Block(PlaBlockMode.Lz, [0, 0, 1, 1], unpacked: 1));
        using var stream = new CountingStream(data);
        using var document = PlaDocument.Open(stream, leaveOpen: true);
        stream.BytesRead = 0;

        Assert.Equal(new byte[] { 0 }, document.ReadPayload(document.Blocks[1]));
        Assert.Equal(6, stream.BytesRead);
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int BytesRead { get; set; }

        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            BytesRead += count;
            return count;
        }
    }

    [Fact]
    public void FinalMatchWithoutLengthByteClipsToTheDeclaredSize()
    {
        // token 0x40 (lit 4) "ABCD"; dist 4 and the body ends: the match length is the remainder
        // of the output, so the copy fills exactly the declared 8 bytes.
        var lz = Synthetic.Concat([0x40], "ABCD"u8.ToArray(), [0x00, 0x04]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 8))));

        Assert.Equal("ABCDABCD"u8.ToArray(), document.ReadPayload(document.Blocks.Single()));
    }

    [Fact]
    public void ABufferBlockProducesItsActualStreamAndZeroTail()
    {
        // the token stream produces 4 bytes, but the block declares a 16-byte buffer: the tail of the
        // buffer stays zero and the payload returns the whole buffer.
        var lz = Synthetic.Concat([0x40], "ABCD"u8.ToArray());
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 16))));

        var payload = document.ReadPayload(document.Blocks.Single());
        Assert.Equal(16, payload.Length);
        Assert.Equal("ABCD"u8.ToArray(), payload[..4]);
        Assert.Equal(new byte[12], payload[4..]);
    }

    [Fact]
    public void TheWindowHoldsTheBufferSpaceOfAPrecedingBufferBlock()
    {
        // block A is a buffer block (actual "ABCD", declared 16): the window receives the buffer space
        // (ABCD + 12 zeros). Block B references the window tail: dist 6 lands in the zero padding.
        var a = Synthetic.Concat([0x40], "ABCD"u8.ToArray());
        var b = Synthetic.Concat([0x40], "wxyz"u8.ToArray(), [0x00, 0x06], [0x04]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, a, unpacked: 16),
            Synthetic.Block(PlaBlockMode.Lz, b, unpacked: 8))));

        var payload = document.ReadPayload(document.Blocks[1]);
        Assert.Equal(new byte[] { (byte)'w', (byte)'x', (byte)'y', (byte)'z', 0, 0, (byte)'w', (byte)'x' }, payload);
    }
}
