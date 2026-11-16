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
    public void TruncatedTokenStreamIsRejected()
    {
        // token 0x40 declares 4 literals; the payload carries only 2
        var lz = Synthetic.Concat([0x40], [0x77, 0x78]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
            Synthetic.Block(PlaBlockMode.Lz, lz, unpacked: 4))));

        Assert.Throws<InvalidDataException>(() => document.ReadPayload(document.Blocks.Single()));
    }
}
