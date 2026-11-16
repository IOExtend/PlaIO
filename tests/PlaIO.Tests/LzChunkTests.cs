using Xunit;

namespace PlaIO.Tests;

/// <summary>The chunk chain of mode-2 (LZ) blocks, read by <see cref="PlaDocument.ReadChunks"/> on synthetic payloads:
/// stored and compressed chunks, short and long headers, and the invalid chains the reader must reject.</summary>
public class LzChunkTests
{
    [Fact]
    public void ChainOfStoredAndCompressedChunks()
    {
        var body1 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var body2 = new byte[] { 11, 12, 13, 14 };
        var payload = Synthetic.LzPayload(
        [
            Synthetic.LzChunk(compressed: false, longHeader: true, bodySize: body1.Length, unpacked: body1.Length, body1),
            Synthetic.LzChunk(compressed: true, longHeader: true, bodySize: body2.Length, unpacked: 40, body2),
            Synthetic.LzChunk(compressed: false, longHeader: false, bodySize: 6, unpacked: 6),
        ]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 56))));

        var chunks = document.ReadChunks(document.Blocks.Single());

        Assert.Equal(3, chunks.Count);
        Assert.Equal(0, chunks[0].Offset);
        Assert.Equal(9, chunks[0].HeaderLength);
        Assert.Equal(body1.Length, chunks[0].PackedSize);
        Assert.Equal(body1.Length, chunks[0].UnpackedSize);
        Assert.False(chunks[0].IsCompressed);
        Assert.Equal(9 + body1.Length, chunks[1].Offset);
        Assert.True(chunks[1].IsCompressed);
        Assert.Equal(body2.Length, chunks[1].PackedSize);
        Assert.Equal(40, chunks[1].UnpackedSize);
        Assert.Equal(9 + body1.Length + 9 + body2.Length, chunks[2].Offset);
        Assert.Equal(3, chunks[2].HeaderLength);
        Assert.Equal(6, chunks[2].PackedSize);
        Assert.Equal(6, chunks[2].UnpackedSize);
        Assert.False(chunks[2].IsCompressed);
    }

    [Fact]
    public void AChainWithTrailingPaddingIsRejected()
    {
        var payload = Synthetic.LzPayload(
        [
            Synthetic.LzChunk(compressed: false, longHeader: true, bodySize: 10, unpacked: 10),
        ], tail: 4);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 10))));

        Assert.Throws<InvalidDataException>(() => document.ReadChunks(document.Blocks.Single()));
    }

    [Fact]
    public void AChainThatDoesNotAddUpIsRejected()
    {
        var payload = Synthetic.LzPayload(
        [
            Synthetic.LzChunk(compressed: false, longHeader: true, bodySize: 10, unpacked: 10),
            Synthetic.LzChunk(compressed: false, longHeader: true, bodySize: 4, unpacked: 4),
        ]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 20))));

        Assert.Throws<InvalidDataException>(() => document.ReadChunks(document.Blocks.Single()));
    }

    [Fact]
    public void AChunkThatRunsPastThePayloadIsRejected()
    {
        var payload = Synthetic.LzPayload(
        [
            Synthetic.LzChunk(compressed: false, longHeader: true, bodySize: 10, unpacked: 10),
        ]);
        // Cut the payload short: the header says 19 bytes, only 19 - 4 remain.
        payload = payload[..^4];
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 10))));

        Assert.Throws<InvalidDataException>(() => document.ReadChunks(document.Blocks.Single()));
    }

    [Fact]
    public void ABlockOfAnotherModeIsRejected()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, [1, 2, 3]))));

        Assert.Throws<NotSupportedException>(() => document.ReadChunks(document.Blocks.Single()));
    }

    [Fact]
    public void ABlockOutsideTheIndexIsRejected()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Lz, Synthetic.LzPayload([Synthetic.LzChunk(compressed: false, longHeader: true, bodySize: 10, unpacked: 10)]), unpacked: 10))));

        var stranger = new PlaBlock(0x1234, "BL", 1, 0xFDEA, 0, 0x8080, PlaBlockMode.Lz, null, 10, 10, null, document.Blocks.Single().PayloadOffset);
        Assert.Throws<ArgumentException>(() => document.ReadChunks(stranger));
    }
}
