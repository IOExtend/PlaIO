using PlaIO.Internal;
using Xunit;
using static PlaIO.Internal.DirectoryFinder;

namespace PlaIO.Tests;

public class DirectoryFinderTests
{
    private static readonly PlaBlock AnyBlock = Parse(Synthetic.Block(PlaBlockMode.Raw, new byte[4]));

    private static PlaBlock Parse(byte[] bytes)
    {
        Assert.True(BlockHeaderParser.TryParse(bytes, 0, bytes.Length, out var block));
        return block;
    }

    [Fact]
    public void RecordFieldsAreClassPosCountGuidInThatOrder()
    {
        var bytes = new byte[30];
        bytes[0] = 0x34; bytes[1] = 0x12;                            // class at 0
        for (int i = 0; i < 8; i++) bytes[2 + i] = (byte)(0x10 + i);  // pos at 2
        bytes[10] = 0x78; bytes[11] = 0x56; bytes[12] = 0x34; bytes[13] = 0x12; // count at 10
        var guid = Guid.NewGuid();
        guid.ToByteArray().CopyTo(bytes, 14);                         // GUID at 14..29
        var record = ReadRecord(bytes);
        Assert.Equal(0x1234, record.ClassNumber);
        Assert.Equal(0x1716151413121110ul, record.Position);
        Assert.Equal(0x12345678u, record.Count);
        Assert.Equal(guid, record.ObjectId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(29)]
    public void ContiguousExtentsAreFoundAtTheirPhase(int phase)
    {
        var records = Synthetic.Extents(200, new Random(phase));
        var directory = FindInPayload(Synthetic.DirectoryPayload(phase, records, tail: 300), AnyBlock);
        Assert.NotNull(directory);
        Assert.Equal(phase, directory.Phase);
        Assert.Equal(phase, directory.RecordOffset);
        Assert.Same(AnyBlock, directory.Block);
        Assert.Equal(records, directory.Records);
        Assert.Equal((199, 0, 0), (directory.AbuttingPairs, directory.OverlappingPairs, directory.DuplicatePositions));
    }

    [Fact]
    public void JunkRecordsAtTheEdgesAreLeftOutOfTheRun()
    {
        var random = new Random(11);
        var junk = Synthetic.RandomBytes(random, 30);
        var records = Synthetic.Extents(150, random);
        var payload = Synthetic.Concat(new byte[7], junk, Synthetic.DirectoryPayload(0, records), Synthetic.RandomBytes(random, 30));
        var directory = FindInPayload(payload, AnyBlock);
        Assert.NotNull(directory);
        Assert.Equal(7 + 30, directory.RecordOffset);
        Assert.Equal(7, directory.Phase);
        Assert.Equal(records, directory.Records);
    }

    [Fact]
    public void FewGapsBetweenExtentsAreAccepted()
    {
        var records = Synthetic.Extents(400, new Random(5), gapRate: 0.05);
        var directory = FindInPayload(Synthetic.DirectoryPayload(3, records), AnyBlock);
        Assert.NotNull(directory);
        Assert.Equal(records, directory.Records);
        var sorted = records.OrderBy(r => r.Position).ToArray();
        int abutting = sorted.Zip(sorted.Skip(1)).Count(p => p.First.Position + p.First.Count == p.Second.Position);
        Assert.Equal(abutting, directory.AbuttingPairs);
        Assert.InRange(abutting, 360, 398);
        Assert.Equal(0, directory.OverlappingPairs);
    }

    private static (ObjectDirectoryRecord[] First, ObjectDirectoryRecord[] Second) TwoAbuttingHalves(Random random)
    {
        var first = Synthetic.Extents(100, random);
        ulong end = first.Max(r => r.Position + r.Count);
        return (first, Synthetic.Extents(100, random, start: end));
    }

    [Fact]
    public void ZeroCountRecordsWithObjectIdsDoNotBreakTheRun()
    {
        var random = new Random(21);
        var (first, second) = TwoAbuttingHalves(random);
        var zeroCount = Enumerable.Range(0, MaxGap + 4)
            .Select(i => new ObjectDirectoryRecord(7, 50_000_000 + (ulong)i * 7, 0, Synthetic.RandomGuid(random))).ToArray();
        var records = first.Concat(zeroCount).Concat(second).ToArray();
        var directory = FindInPayload(Synthetic.DirectoryPayload(0, records), AnyBlock);
        Assert.NotNull(directory);
        Assert.Equal(records, directory.Records);
        Assert.Equal((199, 0, 0), (directory.AbuttingPairs, directory.OverlappingPairs, directory.DuplicatePositions));
    }

    [Fact]
    public void AllZeroRecordsStillBreakTheRun()
    {
        var (first, second) = TwoAbuttingHalves(new Random(22));
        var padding = Enumerable.Repeat(new ObjectDirectoryRecord(0, 0, 0, Guid.Empty), MaxGap + 1);
        var directory = FindInPayload(Synthetic.DirectoryPayload(0, first.Concat(padding).Concat(second)), AnyBlock);
        Assert.NotNull(directory);
        Assert.Equal(first, directory.Records);
    }

    [Fact]
    public void AbuttingBoundIsExactForTiledExtentsAndRejectsNoise()
    {
        var bits = new ulong[1024];
        var targets = new Dictionary<ulong, int>();
        Assert.Equal(199, AbuttingBound(Synthetic.DirectoryPayload(3, Synthetic.Extents(200, new Random(23))), 3, bits, targets));
        Assert.Equal(0, AbuttingBound(new byte[30 * 500], 0, bits, targets));

        // Below MinRunAbutting at every phase, so FindInPayload rejects the payload before it sorts anything.
        var noise = Synthetic.RandomBytes(new Random(24), 1 << 20);
        var noiseBits = new ulong[1 << 13];
        for (int phase = 0; phase < RecordSize; phase++)
            Assert.InRange(AbuttingBound(noise, phase, noiseBits, targets), 0, MinRunAbutting - 1);
        Assert.Null(FindInPayload(noise, AnyBlock));
    }

    [Fact]
    public void MinRunAbuttingMatchesTheRatioGate() =>
        Assert.Equal(MinRunAbutting, (int)Math.Ceiling(MinAbuttingRatio * (MinRecords - 1)));

    [Fact]
    public void ZeroFilledPayloadIsRejected() => Assert.Null(FindInPayload(new byte[30 * 200], AnyBlock));

    [Fact]
    public void RandomBytesAreRejected() => Assert.Null(FindInPayload(Synthetic.RandomBytes(new Random(1), 30 * 500), AnyBlock));

    [Fact]
    public void SixtyThreeRecordsAreTooFew()
    {
        Assert.Null(FindInPayload(Synthetic.DirectoryPayload(0, Synthetic.Extents(63, new Random(2))), AnyBlock));
        Assert.NotNull(FindInPayload(Synthetic.DirectoryPayload(0, Synthetic.Extents(64, new Random(2))), AnyBlock));
    }

    [Fact]
    public void OverlappingExtentsAreRejected()
    {
        var random = new Random(4);
        var records = Enumerable.Range(0, 200)
            .Select(i => new ObjectDirectoryRecord(1, 1000 + (ulong)i * 10, 20, Synthetic.RandomGuid(random))).ToArray();
        random.Shuffle(records);
        Assert.Null(FindInPayload(Synthetic.DirectoryPayload(0, records), AnyBlock));
    }

    [Fact]
    public void PositionsNearTheTopOfTheRangeDoNotOverflow()
    {
        var random = new Random(9);
        var records = Synthetic.Extents(100, random, start: ulong.MaxValue - 200_000);
        var directory = FindInPayload(Synthetic.DirectoryPayload(0, records), AnyBlock);
        Assert.NotNull(directory);
        Assert.Equal(99, directory.AbuttingPairs);

        // A huge count reaching past the top of the range overlaps its successor instead of wrapping around.
        var pair = Synthetic.Concat(
            Synthetic.Record(new ObjectDirectoryRecord(1, ulong.MaxValue - 10, uint.MaxValue, Guid.Empty)),
            Synthetic.Record(new ObjectDirectoryRecord(1, ulong.MaxValue, 1, Guid.Empty)));
        var score = Score(pair, 0, new ulong[2], new int[2]);
        Assert.Equal(new PhaseScore(2, 0, 1, 0), score);
    }

    [Fact]
    public void PairClassesAreExclusive()
    {
        var records = new[]
        {
            new ObjectDirectoryRecord(1, 50, 0, Guid.Empty),   // duplicate of the next; gap 0 == count 0 must not count as abutting
            new ObjectDirectoryRecord(1, 50, 0, Guid.Empty),   // gap to 100
            new ObjectDirectoryRecord(1, 100, 10, Guid.Empty), // abuts 110
            new ObjectDirectoryRecord(1, 110, 5, Guid.Empty),  // duplicate of the next
            new ObjectDirectoryRecord(1, 110, 5, Guid.Empty),  // overlaps 112
            new ObjectDirectoryRecord(1, 112, 1, Guid.Empty),  // gap to 200
            new ObjectDirectoryRecord(1, 200, 1, Guid.Empty),
        };
        var score = Score(Synthetic.DirectoryPayload(0, Enumerable.Reverse(records)), 0, new ulong[7], new int[7]);
        Assert.Equal(new PhaseScore(7, Abutting: 1, Overlapping: 1, Duplicates: 2), score);
    }

    [Fact]
    public void RecordsWithTheSamePosKeepFileOrder()
    {
        // The pair after a repeated pos is judged by the last of the repeats in file order.
        var zeroThenFive = new[]
        {
            new ObjectDirectoryRecord(1, 110, 0, Guid.Empty),
            new ObjectDirectoryRecord(1, 110, 5, Guid.Empty),
            new ObjectDirectoryRecord(1, 112, 1, Guid.Empty),
        };
        Assert.Equal(new PhaseScore(3, 0, 1, 1), Score(Synthetic.DirectoryPayload(0, zeroThenFive), 0, new ulong[3], new int[3]));
        var fiveThenZero = new[] { zeroThenFive[1], zeroThenFive[0], zeroThenFive[2] };
        Assert.Equal(new PhaseScore(3, 0, 0, 1), Score(Synthetic.DirectoryPayload(0, fiveThenZero), 0, new ulong[3], new int[3]));
    }

    [Fact]
    public void BetterPhaseHasMoreAbuttingPairsThenFewerBadPairsAndTiesKeepTheLowestPhase()
    {
        Assert.True(IsBetter(new PhaseScore(100, 5, 9, 9), new PhaseScore(100, 4, 0, 0)));
        Assert.True(IsBetter(new PhaseScore(100, 5, 1, 0), new PhaseScore(100, 5, 1, 1)));
        Assert.False(IsBetter(new PhaseScore(100, 5, 1, 1), new PhaseScore(100, 5, 0, 2)));
        Assert.False(IsBetter(new PhaseScore(100, 4, 0, 0), new PhaseScore(100, 5, 50, 50)));

        // Every phase of a zero payload scores the same (30 * k + 29 bytes give k records at each phase): the lowest wins.
        var best = BestPhase(new byte[30 * 100 + 29]);
        Assert.NotNull(best);
        Assert.Equal(0, best.Value.Phase);
        Assert.Equal(new PhaseScore(100, 0, 0, 99), best.Value.Score);
    }

    [Fact]
    public void BestPhaseFindsTheRecordAlignment()
    {
        var best = BestPhase(Synthetic.DirectoryPayload(17, Synthetic.Extents(120, new Random(8)), tail: 13));
        Assert.NotNull(best);
        Assert.Equal(17, best.Value.Phase);
        Assert.Equal(119, best.Value.Score.Abutting);
    }
}
