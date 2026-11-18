using System.Buffers.Binary;
using Xunit;

namespace PlaIO.Tests;

public class CompressedPoolTests
{
    private static byte[] PoolOf(params byte[][] payloads) => Synthetic.Fdb(Synthetic.Pool(payloads));

    [Fact]
    public void AFileWithoutSignsFindsNoPools()
    {
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(Synthetic.RandomBytes(new Random(1), 4096))));
        Assert.Empty(document.FindCompressedPools());
    }

    [Fact]
    public void OneBigMemCpyBlockDecodesToItsPayload()
    {
        var payload = Enumerable.Range(0, 500).Select(i => (byte)(i * 7)).ToArray();
        using var document = PlaDocument.Open(new MemoryStream(PoolOf(payload)));
        var pools = document.FindCompressedPools();
        Assert.Equal([1], pools.Select(p => p.BlockCount).ToArray());
        var pool = Assert.Single(pools);
        Assert.Equal(payload.Length, pool.TotalDecompressed);
        Assert.Equal(payload, pool.Decode());
    }

    [Fact]
    public void SmallAndBigBlocksDecodeToTheirPayloadsConcatenated()
    {
        var first = Synthetic.RandomBytes(new Random(2), 100);
        var second = Synthetic.RandomBytes(new Random(3), 300);
        var bytes = Synthetic.Fdb(Synthetic.Pool([first], small: true), Synthetic.Pool([second]));
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var pools = document.FindCompressedPools();
        Assert.Equal(2, pools.Count);
        Assert.Equal(first, pools[0].Decode());
        Assert.Equal(second, pools[1].Decode());
        // The file offsets do not overlap: the second pool starts past the first one's last block.
        Assert.True(pools[0].End <= pools[1].Offset);
    }

    [Fact]
    public void AZeroBlockPoolIsValidAndDecodesToNothing()
    {
        var bytes = Synthetic.Fdb(Synthetic.PoolSign(), new byte[16]);
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var pool = Assert.Single(document.FindCompressedPools());
        Assert.Equal(0, pool.TotalDecompressed);
        Assert.Empty(pool.Decode());
    }

    [Fact]
    public void ATruncatedPoolIsNotFound()
    {
        var bytes = Synthetic.Pool([Synthetic.RandomBytes(new Random(4), 200)]);
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(bytes[..^32])));
        Assert.Empty(document.FindCompressedPools());
    }

    [Fact]
    public void ABlockSizeMismatchIsNotFound()
    {
        // Flip one byte of the block's declared packed size (the pool holds the sign, the two u64 sizes, the u32
        // compressed size and the flag byte): it stops matching the compressed size the pool lists.
        var pool = Synthetic.Pool([Synthetic.RandomBytes(new Random(5), 64)]);
        var corrupt = pool.ToArray();
        corrupt[16 + 16 + 4 + 1] ^= 0xFF;
        using var document = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(corrupt)));
        Assert.Empty(document.FindCompressedPools());
    }

    [Fact]
    public void LZBlockThatDoesNotDecodeFindsButDecodeThrows()
    {
        // A big block that declares LZ77 coding (bit 0 set, flag 0x47) with an impossible token stream: Find accepts the
        // shape (the block headers tile the total), Decode fails.
        var block = new byte[9 + 16];
        block[0] = 0x47;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(1), (uint)block.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(5), 16);
        0x00FF00FFu.CopyToBlock(block, 9);
        var bytes = Synthetic.Fdb(Synthetic.PoolSign(), Le64(16), Le64(1), Le32((uint)block.Length), block);
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var pool = Assert.Single(document.FindCompressedPools());
        Assert.Throws<InvalidDataException>(pool.Decode);
    }

    [Fact]
    public void DecodeAfterDisposeThrows()
    {
        var document = PlaDocument.Open(new MemoryStream(PoolOf([1, 2, 3])));
        var pool = Assert.Single(document.FindCompressedPools());
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(pool.Decode);
        Assert.Throws<ObjectDisposedException>(document.FindCompressedPools);
    }

    [Fact]
    public void ASignAcrossTheWindowBoundaryIsStillFound()
    {
        // Put the sign where the 1 MiB scan window ends eight bytes after its start: the next window must overlap it.
        var filler = Synthetic.RandomBytes(new Random(6), (1 << 20) - 8);
        var bytes = Synthetic.Fdb(filler, Synthetic.Pool([Synthetic.RandomBytes(new Random(7), 300)]));
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var pool = Assert.Single(document.FindCompressedPools());
        Assert.Equal(300, pool.TotalDecompressed);
        Assert.Equal(300, pool.Decode().Length);
    }

    [SkippableFact]
    public void EverySampleFindsPoolsAndThePoolsOfTheSmallerOnesDecode()
    {
        var corpus = new SampleCorpus();
        try
        {
            foreach (var sample in corpus.Fdb)
            {
                var pools = sample.Document.FindCompressedPools();
                bool decodeAll = sample.Document.Length < 32 << 20;
                foreach (var pool in decodeAll ? pools : pools.Take(8))
                    Assert.True(pool.Decode().Length == pool.TotalDecompressed,
                        $"{sample.Name}: pool at 0x{pool.Offset:x} does not decode to {pool.TotalDecompressed}");
            }
        }
        finally
        {
            corpus.Dispose();
        }
    }

    [SkippableFact]
    public void TheTextAnnotationSampleHasPoolsAndTheyAllDecode()
    {
        string path = TestFiles.TryFind("TEXSTS.pln") ?? throw TestFiles.Missing("TEXSTS.pln");
        using var document = PlaDocument.Open(path);
        var pools = document.FindCompressedPools();
        Assert.True(pools.Count > 0, $"no pools in TEXSTS.pln; {document.Length} bytes");
        foreach (var pool in pools)
            Assert.True(pool.Decode().Length == pool.TotalDecompressed,
                $"pool at 0x{pool.Offset:x} does not decode to {pool.TotalDecompressed}");
    }

    private static byte[] Le32(uint value)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le64(ulong value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }
}

file static class CopyExtensions
{
    public static void CopyToBlock(this uint value, byte[] block, int at) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), value);
}
