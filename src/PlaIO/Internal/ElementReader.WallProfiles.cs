using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static PlaWallProfile? ReadWallProfile(ReadOnlySpan<byte> wall, ReadOnlySpan<byte> settings,
        Guid wallId, ModelGeometry geometry)
    {
        if (settings.Length < 229 || BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) != 3 ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1 ||
            ReadWallProfileRecord(geometry, wallId, wall) is not { Length: >= 57 } profile ||
            BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(49)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(55)) != 2)
            return null;

        int index = BinaryPrimitives.ReadInt32LittleEndian(profile.AsSpan(51));
        if (index <= 0 || ReadProfileComponents(ReadSectionVector(profile, 394)) is not { } components) return null;
        return new PlaWallProfile(index, Array.AsReadOnly(components.Select(c =>
            new PlaWallProfileComponent(c.BuildingMaterialIndex, c.Function, c.CrossSectionArea, c.Rings, c.Arcs)).ToArray()));
    }

    private static PlaMemberProfile? ReadMemberProfile(ReadOnlySpan<byte> member, Guid owner, bool beam, ModelGeometry geometry)
    {
        Guid ownerRelation = beam ? BeamMemoRelation : ColumnMemoRelation;
        Guid memoId = ReadSingleRelation(member, ownerRelation, 0x11, requireSection: true);
        if (geometry.Read(memoId, beam ? BeamMemoClass : ColumnMemoClass) is not { } memo ||
            ReadSingleRelation(memo, ownerRelation, requireSection: true) != owner)
            return null;

        Guid sectionId = ReadSingleRelation(memo, SectionProfileRelation, 0x11, requireSection: true);
        if (ReadComplexSectionProfile(geometry, memoId, sectionId) is not { Length: >= 57 } profile ||
            BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(47)) != 1 ||
            !profile.AsSpan(53).StartsWith(SectionVectorFrame))
            return null;

        int index = BinaryPrimitives.ReadInt32LittleEndian(profile.AsSpan(49));
        return index > 0 && ReadProfileComponents(ReadSectionVector(profile, 392)) is { } components
            ? new PlaMemberProfile(index, components) : null;
    }

    private static IReadOnlyList<PlaProfileComponent>? ReadProfileComponents(ReadOnlySpan<byte> vector,
        bool allowUnassignedMaterial = false)
    {
        var items = ReadFirstWallHatch(vector, out uint count);
        if (items.IsEmpty) return null;
        var components = new List<PlaProfileComponent>();
        for (int i = 0; i < count; i++)
        {
            int size = ReadHatchSize(items, 2);
            if (size == 0 || ReadProfileComponent(items[..size], allowUnassignedMaterial) is not { } component) return null;
            components.Add(component);
            items = items[size..];
            if (i + 1 == count) break;
            if (items.Length < 16) return null;
            items = items[16..];
        }
        return items.IsEmpty ? components.AsReadOnly() : null;
    }

    private static PlaProfileComponent? ReadProfileComponent(ReadOnlySpan<byte> hatch, bool allowUnassignedMaterial)
    {
        const int pointOffset = 59, pointSize = 16, arcSize = 16, tableHeader = 6, roleOffset = 108;
        int material = BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]);
        int pointCount = BinaryPrimitives.ReadInt32LittleEndian(hatch[55..]);
        int arcTable = pointOffset + pointCount * pointSize;
        if (material < 0 || (material == 0 && !allowUnassignedMaterial) || pointCount > 4096 ||
            hatch.Length - arcTable < tableHeader ||
            !IsCorner(hatch[pointOffset..], 0, 0) || BinaryPrimitives.ReadUInt16LittleEndian(hatch[arcTable..]) != 1)
            return null;

        int arcCount = BinaryPrimitives.ReadInt32LittleEndian(hatch[(arcTable + 2)..]);
        int arcStart = arcTable + tableHeader;
        if (arcCount < 0 || arcCount > pointCount || arcCount > (hatch.Length - arcStart) / arcSize) return null;
        int ringTable = arcStart + arcCount * arcSize;
        if (hatch.Length - ringTable < tableHeader || BinaryPrimitives.ReadUInt16LittleEndian(hatch[ringTable..]) != 1)
            return null;
        int ringEndCount = BinaryPrimitives.ReadInt32LittleEndian(hatch[(ringTable + 2)..]);
        int ringStart = ringTable + tableHeader;
        if (ringEndCount < 2 || ringEndCount > (pointCount - 1) / 4 + 1 ||
            ringEndCount > (hatch.Length - ringStart) / sizeof(int) ||
            BinaryPrimitives.ReadInt32LittleEndian(hatch[ringStart..]) != 0)
            return null;
        int footer = ringStart + ringEndCount * sizeof(int);
        if (hatch.Length - footer <= roleOffset ||
            BinaryPrimitives.ReadInt32LittleEndian(hatch[(footer - sizeof(int))..]) != pointCount - 1)
            return null;

        var sweeps = new Dictionary<int, double>(arcCount);
        for (int p = arcStart; p < ringTable; p += arcSize)
        {
            int start = BinaryPrimitives.ReadInt32LittleEndian(hatch[p..]);
            int end = BinaryPrimitives.ReadInt32LittleEndian(hatch[(p + 4)..]);
            double sweep = ReadDouble(hatch, p + 8);
            if (start <= 0 || start >= pointCount - 1 || end != start + 1 ||
                !(Math.Abs(sweep) is > 0 and < Math.Tau) || !sweeps.TryAdd(start, sweep)) return null;
        }

        var planRings = new List<IReadOnlyList<PlaPlanPoint>>();
        var boundaries = new List<SlabEdge[]>();
        var rings = new List<IReadOnlyList<PlaProfilePoint>>();
        var arcs = new List<PlaProfileArc>();
        double area = 0;
        int first = 1;
        for (int i = 1; i < ringEndCount; i++)
        {
            int last = BinaryPrimitives.ReadInt32LittleEndian(hatch[(ringStart + i * sizeof(int))..]);
            if (last < first + 3 || last >= pointCount) return null;
            var points = new PlaPlanPoint[last - first];
            var local = new PlaProfilePoint[points.Length];
            for (int j = 0; j < points.Length; j++)
            {
                int p = pointOffset + (first + j) * pointSize;
                double u = ReadDouble(hatch, p), z = ReadDouble(hatch, p + 8);
                if (!double.IsFinite(u) || !double.IsFinite(z)) return null;
                points[j] = new(u, z);
                local[j] = new(u, z);
            }
            if (!IsCorner(hatch[(pointOffset + last * pointSize)..], points[0].X, points[0].Y)) return null;

            double signedArea = SignedRingArea(points);
            var edges = new SlabEdge[points.Length];
            for (int j = 0; j < points.Length; j++)
            {
                var p = points[j];
                var q = points[(j + 1) % points.Length];
                if (p == q) return null;
                if (sweeps.Remove(first + j, out double sweep))
                {
                    if (ReadCircularEdge(p, q, sweep, out double segmentArea) is not { } edge) return null;
                    edges[j] = edge;
                    signedArea += segmentArea;
                    arcs.Add(new PlaProfileArc(i - 1, j, sweep));
                }
                else edges[j] = new SlabEdge(p, q, 0);
            }
            if (!double.IsFinite(signedArea) || signedArea == 0 || !SlabRingIsSimple(edges) ||
                (i > 1 && !CurvedHoleFits(points, edges, planRings, boundaries))) return null;
            area += (i == 1 ? 1 : -1) * Math.Abs(signedArea);
            planRings.Add(points);
            boundaries.Add(edges);
            rings.Add(Array.AsReadOnly(local));
            first = last + 1;
        }
        if (sweeps.Count != 0 || !double.IsFinite(area) || area <= 0) return null;
        string? function = hatch[footer + roleOffset] switch { 0 => "Other", 1 => "Core", _ => null };
        return new PlaProfileComponent(material, function, area, rings.AsReadOnly(),
            arcs.Count == 0 ? null : arcs.AsReadOnly());
    }

    private static bool SameProfile(int? firstIndex, int? secondIndex,
        IReadOnlyList<PlaProfileComponent>? a, IReadOnlyList<PlaProfileComponent>? b)
    {
        if (a is null || b is null) return a == b;
        if (firstIndex != secondIndex || a.Count != b.Count) return false;
        return a.Zip(b).All(pair =>
        {
            var (first, second) = pair;
            return (first.BuildingMaterialIndex, first.Function, first.CrossSectionArea) ==
                (second.BuildingMaterialIndex, second.Function, second.CrossSectionArea) &&
                first.Rings.Count == second.Rings.Count &&
                first.Rings.Zip(second.Rings).All(rings => rings.First.SequenceEqual(rings.Second)) &&
                (first.Arcs ?? []).SequenceEqual(second.Arcs ?? []);
        });
    }
}
