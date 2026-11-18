using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace PlaIO.Tests;

public class PoolSubgroupTests
{
    private static byte[] PoolOf(params byte[][] subgroups) => Synthetic.Fdb(Synthetic.Pool([subgroups.SelectMany(s => s).ToArray()]));

    private static byte[] TextPayload(string text) =>
        BitConverter.GetBytes((uint)text.Length).Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    [Fact]
    public void RawSubgroupIsParsedWithKindGuidAndData()
    {
        var guid = Guid.NewGuid();
        var payload = Synthetic.RandomBytes(new Random(1), 100);
        using var document = PlaDocument.Open(new MemoryStream(PoolOf(Synthetic.Subgroup(0x009B, payload, PlaBlockMode.Raw, guid))));
        var subgroup = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups());
        Assert.Equal(0x009B, subgroup.Kind);
        Assert.Equal(1, subgroup.Version);
        Assert.Equal(guid, subgroup.ObjectId);
        Assert.Equal(PlaBlockMode.Raw, subgroup.Mode);
        Assert.Equal(payload, subgroup.Data);
        Assert.Equal(0, subgroup.Offset);
    }

    [Fact]
    public void ZeroMaskSubgroupIsDecodedToItsData()
    {
        var payload = Synthetic.ZeroMask(Synthetic.RandomBytes(new Random(2), 200));
        using var document = PlaDocument.Open(new MemoryStream(PoolOf(Synthetic.Subgroup(0x0045, payload, PlaBlockMode.ZeroMask, unpacked: 200))));
        var subgroup = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups());
        Assert.Equal(PlaBlockMode.ZeroMask, subgroup.Mode);
        Assert.Equal(200, subgroup.Data.Length);
        Assert.Equal(Synthetic.RandomBytes(new Random(2), 200), subgroup.Data);
    }

    [Fact]
    public void AZeroMaskStreamShorterThanTheBufferLeavesTheTailZero()
    {
        // The declared unpacked size is the buffer size; the stream fills the head and stops.
        var payload = Synthetic.ZeroMask([1, 2, 3, 4]);
        using var document = PlaDocument.Open(new MemoryStream(PoolOf(Synthetic.Subgroup(0x0016, payload, PlaBlockMode.ZeroMask, unpacked: 64))));
        var subgroup = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups());
        Assert.Equal(64, subgroup.Data.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, subgroup.Data[..4]);
        Assert.True(subgroup.Data[4..].All(b => b == 0));
        Assert.False(subgroup.Partial);
    }

    [Fact]
    public void ATruncatedLiteralIsMarkedPartialAndKeepsTheProducedBytes()
    {
        // The last mask demands more literals than the stream holds: the record is damaged. The subgroup keeps the
        // bytes the stream produced, the tail is reconstructed with zeros, and Partial marks it.
        var plain = new byte[] { 1, 2, 3, 4, 5, 6 };
        var full = Synthetic.ZeroMask(plain); // mask 0x3F, six data bytes, ends at a group boundary
        // Cut the last two literals off: the mask still demands six, the stream only holds four.
        var truncated = full[..(full.Length - 2)];
        using var document = PlaDocument.Open(new MemoryStream(PoolOf(Synthetic.Subgroup(0x0016, truncated, PlaBlockMode.ZeroMask, unpacked: 6))));
        var subgroup = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups());
        Assert.True(subgroup.Partial);
        Assert.Equal(6, subgroup.Data.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0 }, subgroup.Data);
    }

    [Fact]
    public void TextPayloadIsReadAsALengthPrefixedUtf16String()
    {
        using var document = PlaDocument.Open(new MemoryStream(PoolOf(Synthetic.Subgroup(0x009B, TextPayload("Sample text"), PlaBlockMode.Raw))));
        var data = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups()).Data;
        int len = BitConverter.ToInt32(data, 0);
        Assert.Equal("Sample text", Encoding.Unicode.GetString(data, 4, len * 2));
    }

    [Fact]
    public void SubgroupsAreParsedInOrderAndOffsetsArePoolRelative()
    {
        var first = Synthetic.RandomBytes(new Random(3), 60);
        var second = Synthetic.RandomBytes(new Random(4), 30);
        var bytes = PoolOf(
            Synthetic.Subgroup(0x0003, first, PlaBlockMode.Raw),
            Synthetic.Subgroup(0x005D, second, PlaBlockMode.ZeroMask, unpacked: 30));
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var subgroups = document.FindCompressedPools()[0].ReadSubgroups();
        Assert.Equal(2, subgroups.Count);
        Assert.Equal(0x0003, subgroups[0].Kind);
        Assert.Equal(first, subgroups[0].Data);
        Assert.Equal(0x005D, subgroups[1].Kind);
        Assert.True(subgroups[1].Offset > subgroups[0].Offset);
    }

    [Fact]
    public void AMode2SubgroupIsReportedWithoutData()
    {
        var bytes = PoolOf(Synthetic.Subgroup(0x0045, Synthetic.RandomBytes(new Random(5), 40), PlaBlockMode.Lz, unpacked: 80));
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var subgroup = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups());
        Assert.Equal(PlaBlockMode.Lz, subgroup.Mode);
        Assert.Empty(subgroup.Data);
    }

    [Fact]
    public void JunkBetweenSubgroupsIsSkipped()
    {
        var filler = Synthetic.RandomBytes(new Random(6), 50);
        // Junk that starts with "BL" but fails the header checks, right before a real subgroup, in one block.
        var bytes = PoolOf(new byte[] { 0x42, 0x4C, 0x99 }.Concat(Synthetic.Subgroup(0x0006, filler, PlaBlockMode.Raw)).ToArray());
        using var document = PlaDocument.Open(new MemoryStream(bytes));
        var subgroup = Assert.Single(document.FindCompressedPools()[0].ReadSubgroups());
        Assert.Equal(0x0006, subgroup.Kind);
        Assert.Equal(filler, subgroup.Data);
    }

    [Fact]
    public void ReadSubgroupsAfterDisposeThrows()
    {
        var document = PlaDocument.Open(new MemoryStream(PoolOf(Synthetic.Subgroup(0x0006, [1, 2, 3], PlaBlockMode.Raw))));
        var pool = Assert.Single(document.FindCompressedPools());
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(pool.ReadSubgroups);
    }

    [SkippableFact]
    public void EverySamplePoolParsesItsSubgroups()
    {
        var corpus = new SampleCorpus();
        try
        {
            foreach (var sample in corpus.Fdb)
            foreach (var pool in sample.Document.FindCompressedPools())
            {
                var subgroups = pool.ReadSubgroups();
                Assert.True(subgroups.Count > 0 || pool.TotalDecompressed == 0,
                    $"{sample.Name}: pool at 0x{pool.Offset:x} of {pool.TotalDecompressed} bytes has no subgroups");
            }
        }
        finally
        {
            corpus.Dispose();
        }
    }

    [SkippableFact]
    public void SomeSampleSubgroupHoldsALengthPrefixedUtf16String()
    {
        // The payloads store strings as [u32 len][utf16le data]; no sample name or content is asserted, only that the
        // corpus contains at least one such string inside a decoded subgroup payload.
        var corpus = new SampleCorpus();
        try
        {
            foreach (var sample in corpus.Fdb)
                foreach (var pool in sample.Document.FindCompressedPools())
                    foreach (var subgroup in pool.ReadSubgroups())
                    {
                        var data = subgroup.Data;
                        for (int i = 0; i + 8 <= data.Length; i++)
                        {
                            int len = BitConverter.ToInt32(data, i);
                            if (len is < 1 or > 500 || i + 4 + len * 2 > data.Length) continue;
                            bool printable = true;
                            for (int k = 0; k < len * 2 && printable; k += 2)
                            {
                                char ch = (char)(data[i + 4 + k] | data[i + 5 + k] << 8);
                                if (ch != 0 && (ch < 0x20 || ch > 0x7E) && (ch < 0x400 || ch > 0x4FF)) printable = false;
                            }
                            if (printable && len >= 2)
                            {
                                Assert.True(true, $"found a length-prefixed utf16 string in {sample.Name}");
                                return;
                            }
                        }
                    }
            Assert.Fail("no length-prefixed utf16 string found in any pool subgroup of any sample");
        }
        finally
        {
            corpus.Dispose();
        }
    }
}
