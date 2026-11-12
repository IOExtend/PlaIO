using System.Buffers.Binary;
using System.Numerics;

namespace PlaIO.Internal;

/// <summary>Searches a decoded payload for an object directory: consecutive 30-byte records
/// [u16 class][u64 pos][u32 count][GUID16] whose extents, sorted by pos, mostly abut and do not overlap.</summary>
/// <remarks>The constants below are heuristics of this reader, not facts of the format. Where a directory starts and ends
/// is not known, so the reported run is an estimate.</remarks>
internal static class DirectoryFinder
{
    public const int RecordSize = 30;
    public const int MinRecords = 64;

    /// <summary>The most consecutive records without an abutting neighbour that a run may contain.</summary>
    public const int MaxGap = 8;

    public const double MinAbuttingRatio = 0.90;
    public const double MaxOverlapRatio = 0.01;

    /// <summary>The fewest abutting pairs an accepted run can have: MinAbuttingRatio of the pairs of MinRecords records.</summary>
    public const int MinRunAbutting = 57;

    /// <summary>Counts over the adjacent pairs of records sorted by pos; each pair is in at most one class.</summary>
    internal readonly record struct PhaseScore(int Records, int Abutting, int Overlapping, int Duplicates);

    public static ObjectDirectoryRecord ReadRecord(ReadOnlySpan<byte> record) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(record),
        BinaryPrimitives.ReadUInt64LittleEndian(record[2..]),
        BinaryPrimitives.ReadUInt32LittleEndian(record[10..]),
        new Guid(record.Slice(14, 16)));

    public static ObjectDirectoryRecord[] ReadRecords(ReadOnlySpan<byte> data, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((long)offset + (long)count * RecordSize > data.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "The records extend past the end of the data.");
        var records = new ObjectDirectoryRecord[count];
        for (int i = 0; i < count; i++) records[i] = ReadRecord(data.Slice(offset + i * RecordSize, RecordSize));
        return records;
    }

    /// <summary>Scores every whole record from byte <paramref name="phase"/> to the end of the data. The scratch arrays must
    /// hold at least that many records.</summary>
    public static PhaseScore Score(ReadOnlySpan<byte> data, int phase, ulong[] keys, int[] idx) =>
        ScoreRange(data, phase, (data.Length - phase) / RecordSize, keys, idx, null);

    public static (int Phase, PhaseScore Score)? BestPhase(ReadOnlySpan<byte> data)
    {
        int capacity = data.Length / RecordSize;
        return capacity < MinRecords ? null : BestPhase(data, new ulong[capacity], new int[capacity], null);
    }

    /// <summary>True when <paramref name="candidate"/> beats <paramref name="best"/>: more abutting pairs, then fewer
    /// overlapping plus duplicate pairs. Equal scores keep the earlier (lower) phase.</summary>
    public static bool IsBetter(PhaseScore candidate, PhaseScore best) =>
        candidate.Abutting > best.Abutting
        || (candidate.Abutting == best.Abutting
            && candidate.Overlapping + candidate.Duplicates < best.Overlapping + best.Duplicates);

    public static ObjectDirectory? FindInPayload(ReadOnlySpan<byte> data, PlaBlock block)
    {
        int capacity = data.Length / RecordSize;
        if (capacity < MinRecords) return null;
        // A phase whose bound is below what an accepted run needs cannot yield one and is never the best phase that does,
        // so skipping it does not change the result; noise and zero fill are rejected here without any sort.
        var eligible = new bool[RecordSize];
        var bits = new ulong[BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, capacity / 4))]; // about 16 bits per record
        var targets = new Dictionary<ulong, int>();
        bool any = false;
        for (int p = 0; p < RecordSize; p++)
            any |= eligible[p] = (data.Length - p) / RecordSize >= MinRecords && AbuttingBound(data, p, bits, targets) >= MinRunAbutting;
        if (!any) return null;

        var keys = new ulong[capacity];
        var idx = new int[capacity];
        if (BestPhase(data, keys, idx, eligible) is not var (phase, score)) return null;

        int n = score.Records;
        var linked = new bool[n];
        ScoreRange(data, phase, n, keys, idx, linked);

        // Longest run in file order that starts and ends on a linked record, with at most MaxGap unlinked records in a row.
        // A record with count 0 and an object GUID has no extent to abut, so it neither breaks nor ends a run; an all-zero
        // record (padding) still counts as unlinked.
        int bestStart = 0, bestLength = 0, runStart = -1, unlinked = 0;
        for (int i = 0; i < n; i++)
        {
            if (!linked[i])
            {
                var record = data.Slice(phase + i * RecordSize, RecordSize);
                if (BinaryPrimitives.ReadUInt32LittleEndian(record[10..]) != 0 || record[14..].IndexOfAnyExcept((byte)0) < 0) unlinked++;
                continue;
            }
            if (runStart < 0 || unlinked > MaxGap) runStart = i;
            unlinked = 0;
            if (i - runStart + 1 > bestLength)
            {
                bestStart = runStart;
                bestLength = i - runStart + 1;
            }
        }
        if (bestLength < MinRecords) return null;

        int recordOffset = phase + bestStart * RecordSize;
        var run = ScoreRange(data, recordOffset, bestLength, keys, idx, null);
        int pairs = bestLength - 1;
        if (run.Abutting < MinAbuttingRatio * pairs || run.Overlapping + run.Duplicates > MaxOverlapRatio * pairs) return null;

        return new ObjectDirectory(block, recordOffset, ReadRecords(data, recordOffset, bestLength),
            run.Abutting, run.Overlapping, run.Duplicates);
    }

    /// <summary>An upper bound on the abutting pairs of any subset of the records at <paramref name="phase"/>: the number of
    /// records with count &gt; 0 whose pos + count is the pos of a record (each abutting pair is such a record and its
    /// successor). Linear time. <paramref name="bits"/> (a power-of-two length) is a filter, first of the positions and then of
    /// the candidate targets; a false hit costs a lookup, never a wrong bound.</summary>
    public static int AbuttingBound(ReadOnlySpan<byte> data, int phase, ulong[] bits, Dictionary<ulong, int> targets)
    {
        int n = (data.Length - phase) / RecordSize;
        int shift = 64 - BitOperations.Log2((ulong)bits.Length * 64);
        Array.Clear(bits);
        targets.Clear();
        for (int i = 0; i < n; i++)
        {
            ulong h = BinaryPrimitives.ReadUInt64LittleEndian(data[(phase + i * RecordSize + 2)..]) * 0x9E3779B97F4A7C15ul >> shift;
            bits[h >> 6] |= 1ul << (int)h;
        }
        for (int i = 0; i < n; i++)
        {
            var record = data.Slice(phase + i * RecordSize, RecordSize);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
            if (count == 0) continue;
            ulong target = BinaryPrimitives.ReadUInt64LittleEndian(record[2..]) + count; // a wrapped sum can only add a hit
            ulong h = target * 0x9E3779B97F4A7C15ul >> shift;
            if ((bits[h >> 6] & (1ul << (int)h)) != 0) targets[target] = targets.GetValueOrDefault(target) + 1;
        }
        if (targets.Count == 0) return 0;
        Array.Clear(bits);
        foreach (ulong target in targets.Keys)
        {
            ulong h = target * 0x9E3779B97F4A7C15ul >> shift;
            bits[h >> 6] |= 1ul << (int)h;
        }
        int bound = 0;
        for (int i = 0; i < n && targets.Count > 0; i++)
        {
            ulong pos = BinaryPrimitives.ReadUInt64LittleEndian(data[(phase + i * RecordSize + 2)..]);
            ulong h = pos * 0x9E3779B97F4A7C15ul >> shift;
            if ((bits[h >> 6] & (1ul << (int)h)) != 0 && targets.Remove(pos, out int records)) bound += records;
        }
        return bound;
    }

    private static (int Phase, PhaseScore Score)? BestPhase(ReadOnlySpan<byte> data, ulong[] keys, int[] idx, bool[]? eligible)
    {
        (int Phase, PhaseScore Score)? best = null;
        for (int phase = 0; phase < RecordSize; phase++)
        {
            if ((data.Length - phase) / RecordSize < MinRecords || eligible?[phase] == false) continue;
            var score = Score(data, phase, keys, idx);
            if (best is null || IsBetter(score, best.Value.Score)) best = (phase, score);
        }
        return best;
    }

    private static PhaseScore ScoreRange(ReadOnlySpan<byte> data, int start, int n, ulong[] keys, int[] idx, bool[]? linked)
    {
        for (int i = 0; i < n; i++)
        {
            keys[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(start + i * RecordSize + 2)..]);
            idx[i] = i;
        }
        Array.Sort(keys, idx, 0, n);
        // Records with the same pos keep file order, so the pair after them is judged the same way on every run.
        for (int i = 0, j; i < n; i = j)
        {
            for (j = i + 1; j < n && keys[j] == keys[i]; j++) { }
            if (j - i > 1) Array.Sort(idx, i, j - i);
        }

        int abutting = 0, overlapping = 0, duplicates = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            ulong gap = keys[i + 1] - keys[i]; // sorted, so no wrap-around
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[(start + idx[i] * RecordSize + 10)..]);
            if (gap == 0) duplicates++;
            else if (gap < count) overlapping++;
            else if (gap == count)
            {
                abutting++;
                if (linked != null) linked[idx[i]] = linked[idx[i + 1]] = true;
            }
        }
        return new PhaseScore(n, abutting, overlapping, duplicates);
    }
}
