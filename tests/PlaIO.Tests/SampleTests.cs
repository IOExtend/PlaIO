using PlaIO.Internal;
using Xunit;

namespace PlaIO.Tests;

/// <summary>The private sample files (PLAIO_TEST_FILES or test_files), found and opened once per test class. Each FDB file is
/// opened once, so its block index is built once. The FDB and BIMx lists skip independently when empty.</summary>
public sealed class SampleCorpus : IDisposable
{
    private readonly Lazy<IReadOnlyList<FdbSample>> _fdb = new(OpenAll);

    private readonly Lazy<IReadOnlyList<string>> _bimx = new(() => TestFiles.FindAll(".bimx"));

    public IReadOnlyList<FdbSample> Fdb => _fdb.Value.Count > 0 ? _fdb.Value : throw TestFiles.Missing("*.pla or *.pln");

    public IReadOnlyList<string> Bimx => _bimx.Value.Count > 0 ? _bimx.Value : throw TestFiles.Missing("*.bimx");

    public void Dispose()
    {
        if (!_fdb.IsValueCreated) return;
        foreach (var sample in _fdb.Value) sample.Document.Dispose();
    }

    /// <summary>Opens every FDB sample; when one fails, the ones already opened are closed before the exception propagates.</summary>
    private static List<FdbSample> OpenAll()
    {
        var samples = new List<FdbSample>();
        try
        {
            foreach (string path in TestFiles.FindAll(".pla", ".pln")) samples.Add(new FdbSample(path, PlaDocument.Open(path)));
            return samples;
        }
        catch
        {
            foreach (var sample in samples) sample.Document.Dispose();
            throw;
        }
    }
}

public sealed record FdbSample(string Path, PlaDocument Document)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Properties every sample must have. No counts tied to particular samples are asserted.</summary>
public class SampleTests(SampleCorpus corpus) : IClassFixture<SampleCorpus>
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendChunk = [0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    [SkippableFact]
    public void EverySampleIsDetectedAsTheKindOfItsExtension()
    {
        var files = TestFiles.FindAll(".pla", ".pln", ".bimx");
        if (files.Count == 0) throw TestFiles.Missing("*.pla, *.pln or *.bimx");
        foreach (string file in files)
        {
            var expected = Path.GetExtension(file).Equals(".bimx", StringComparison.OrdinalIgnoreCase) ? PlaFileKind.Bimx : PlaFileKind.Fdb;
            Assert.True(PlaFile.DetectKind(file) == expected, $"{Path.GetFileName(file)}: not detected as {expected}");
        }
    }

    [SkippableFact]
    public void EveryFdbSampleHasTheConfirmedHeader()
    {
        foreach (var sample in corpus.Fdb)
        {
            var header = sample.Document.Header;
            Assert.True(header.LayoutVersion == 0x0010 && header.FormatVersion == 0x0002 && header.Field12 == 0x01010000,
                $"{sample.Name}: header fields {header.LayoutVersion:x} {header.FormatVersion:x} {header.Field12:x}");
            Assert.True(header.Raw.Length == FdbHeader.Size, $"{sample.Name}: raw header length {header.Raw.Length}");
            Assert.True(header.Raw.Span[16..32].IndexOfAnyExcept((byte)0) < 0, $"{sample.Name}: bytes 16..31 are not zero");
        }
    }

    [SkippableFact]
    public void EveryFdbIndexIsOrderedAndInsideTheFile()
    {
        foreach (var sample in corpus.Fdb)
        {
            var blocks = sample.Document.Blocks;
            Assert.True(blocks.Count > 0, $"{sample.Name}: no blocks");
            Assert.True(blocks[0].Offset == 32, $"{sample.Name}: first block at 0x{blocks[0].Offset:x}");
            for (int i = 0; i < blocks.Count; i++)
            {
                Assert.True(blocks[i].End <= sample.Document.Length, $"{sample.Name}: block at 0x{blocks[i].Offset:x} ends past the file");
                if (i + 1 < blocks.Count)
                    Assert.True(blocks[i].End <= blocks[i + 1].Offset, $"{sample.Name}: block at 0x{blocks[i].Offset:x} overlaps the next");
            }
        }
    }

    [SkippableFact]
    public void FdbSamplesHaveRawAndZeroMaskBlocksAndSomeHaveLzBlocks()
    {
        bool anyLz = false;
        foreach (var sample in corpus.Fdb)
        {
            var modes = sample.Document.Blocks.Select(b => b.Mode).ToHashSet();
            Assert.True(modes.Contains(PlaBlockMode.Raw), $"{sample.Name}: no raw blocks");
            Assert.True(modes.Contains(PlaBlockMode.ZeroMask), $"{sample.Name}: no mode-1 blocks");
            anyLz |= modes.Contains(PlaBlockMode.Lz);
        }
        Assert.True(anyLz, "No sample has a mode-2 block.");
    }

    [SkippableFact]
    public void EveryRawBlockDecodesToItsDeclaredSize()
    {
        foreach (var sample in corpus.Fdb)
            foreach (var block in sample.Document.Blocks.Where(b => b.Mode == PlaBlockMode.Raw))
                Assert.True(sample.Document.ReadPayload(block).Length == block.UnpackedSize,
                    $"{sample.Name}: raw block at 0x{block.Offset:x}");
    }

    [SkippableFact]
    public void NearlyEveryZeroMaskBlockDecodesToItsDeclaredSize()
    {
        foreach (var sample in corpus.Fdb)
        {
            int total = 0, decoded = 0;
            foreach (var block in sample.Document.Blocks.Where(b => b.Mode == PlaBlockMode.ZeroMask))
            {
                total++;
                try
                {
                    if (sample.Document.ReadPayload(block).Length == block.UnpackedSize) decoded++;
                }
                catch (InvalidDataException)
                {
                    // Counted as not decoded; any other exception fails the test.
                }
            }
            Assert.True(total > 0 && decoded >= 0.99 * total, $"{sample.Name}: {decoded} of {total} mode-1 blocks decode");
        }
    }

    [SkippableFact]
    public void ReadingAnLzBlockThrowsNotSupported()
    {
        var (sample, block) = corpus.Fdb
            .SelectMany(s => s.Document.Blocks.Where(b => b.Mode == PlaBlockMode.Lz).Select(b => (s, b)))
            .FirstOrDefault();
        Assert.True(block != null, "No sample has a mode-2 block.");
        var e = Assert.Throws<NotSupportedException>(() => sample.Document.ReadPayload(block));
        Assert.Contains("mode 2", e.Message);
    }

    [SkippableFact]
    public void EveryFdbSampleHasADirectoryOfMostlyAbuttingExtents()
    {
        foreach (var sample in corpus.Fdb)
        {
            var directories = sample.Document.FindDirectories();
            Assert.True(directories.Count > 0, $"{sample.Name}: no object directory found");
            foreach (var d in directories)
            {
                string where = $"{sample.Name}: directory in block 0x{d.Block.Offset:x}";
                Assert.True(d.Block.Mode == PlaBlockMode.Raw, where);
                Assert.True(d.Records.Count >= DirectoryFinder.MinRecords, $"{where}: {d.Records.Count} records");
                var sorted = d.Records.OrderBy(r => r.Position).ToArray();
                int abutting = 0, overlapping = 0, duplicates = 0;
                for (int i = 0; i + 1 < sorted.Length; i++)
                {
                    ulong gap = sorted[i + 1].Position - sorted[i].Position;
                    if (gap == 0) duplicates++;
                    else if (gap < sorted[i].Count) overlapping++;
                    else if (gap == sorted[i].Count) abutting++;
                }
                int pairs = sorted.Length - 1;
                Assert.True(abutting >= 0.90 * pairs, $"{where}: {abutting} of {pairs} pairs abut");
                Assert.True(overlapping + duplicates <= 0.01 * pairs, $"{where}: {overlapping} overlapping, {duplicates} duplicate pairs of {pairs}");
                Assert.True(abutting == d.AbuttingPairs, $"{where}: abutting {d.AbuttingPairs} reported, {abutting} counted");
                Assert.True(duplicates == d.DuplicatePositions, $"{where}: duplicates {d.DuplicatePositions} reported, {duplicates} counted");
                Assert.True(overlapping == d.OverlappingPairs,
                    $"{where}: overlapping {d.OverlappingPairs} reported, {overlapping} counted");
            }
        }
    }

    [SkippableFact]
    public void EveryBimxCoverIsACompletePng()
    {
        foreach (string path in corpus.Bimx)
        {
            string name = Path.GetFileName(path);
            using (var document = BimxDocument.Open(path))
            {
                Assert.True(document.CoverOffset == 4, name);
                byte[] cover = document.ReadCover();
                Assert.True(cover.AsSpan().StartsWith(PngSignature), $"{name}: cover does not start with the PNG signature");
                Assert.True(cover.AsSpan().EndsWith(IendChunk), $"{name}: cover does not end with the IEND chunk");
                Assert.True(document.ContentOffset <= document.Length, $"{name}: cover ends past the file");
            }
            Assert.Throws<InvalidDataException>(() => PlaDocument.Open(path));
        }
    }
}
