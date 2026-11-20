using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public class PoolSubgroupTests
{
    [Fact]
    public void RawSubgroupUsesThePayloadImmediatelyAfterTheSizes()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] subgroup = Synthetic.Block(PlaBlockMode.Raw, "sample"u8.ToArray(), ver: 1, kind: 7, guid: guid);
        subgroup[10] |= 4; // In a pool this flag adds no file-level extra field.
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal(0, record.Offset);
        Assert.Equal(1, record.Version);
        Assert.Equal(7, record.Kind);
        Assert.Equal(guid, record.ObjectId);
        Assert.Equal("sample"u8.ToArray(), record.Data);
        Assert.False(record.Partial);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public void ReportsTruncatedLiteralsSeparatelyFromValidBufferPadding(byte mask, bool partial)
    {
        byte[] subgroup = Synthetic.Block(PlaBlockMode.ZeroMask, [mask, 9], unpacked: 16);
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        byte[] expected = new byte[16];
        expected[0] = 9;
        Assert.Equal(expected, record.Data);
        Assert.Equal(partial, record.Partial);
    }

    [Fact]
    public void LzSubgroupReportsPartialWithEmptyData()
    {
        byte[] subgroup = Synthetic.Block(PlaBlockMode.Lz, [1, 2, 3, 4], unpacked: 64);
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal(PlaBlockMode.Lz, record.Mode);
        Assert.Empty(record.Data);
        Assert.True(record.Partial);
    }

    [Fact]
    public void ShortHeaderCandidateDoesNotHideLaterRecord()
    {
        byte[] badHeader = [0x42, 0x4C, 0, 0, 0, 0, 1, 0, 1, 0xA0, 0x80];
        byte[] good = Synthetic.Block(PlaBlockMode.Raw, [42]);
        using var doc = OpenPool(Synthetic.Concat(badHeader, good));

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal(badHeader.Length, record.Offset);
        Assert.Equal(new byte[] { 42 }, record.Data);
    }

    [Fact]
    public void ReadSubgroupsAfterDisposeThrows()
    {
        var doc = OpenPool([]);
        var pool = Assert.Single(doc.FindCompressedPools());
        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(pool.ReadSubgroups);
    }

    [SkippableFact]
    public void ReadsSubgroupsFromSavedFiles()
    {
        using var corpus = new SampleCorpus();
        int pools = 0;
        foreach (var sample in corpus.Fdb)
        {
            foreach (var pool in sample.Document.FindCompressedPools())
            {
                if (pool.TotalDecompressed == 0) continue;
                Assert.NotEmpty(pool.ReadSubgroups());
                pools++;
            }
        }
        if (pools == 0) throw TestFiles.Missing("FDB fixture with nonempty QuickLZ pools");
    }

    private static PlaDocument OpenPool(byte[] data) => PlaDocument.Open(new MemoryStream(Synthetic.Fdb(
        Synthetic.QuickLzPool(((uint)data.Length, Synthetic.QuickLzFrame(data, data.Length, flags: 0x46))))));
}
