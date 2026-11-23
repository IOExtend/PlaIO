using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static ReadOnlySpan<byte> TopCenterOpeningFrame => [2, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0, 0, 0];

    private static Elevations? ReadSlabOpeningElevations(ReadOnlySpan<byte> settings, Guid opening, PlaOpeningProfile profile,
        OpeningLinks links, ModelGeometry geometry, out PlaOpeningGeometry? cutGeometry)
    {
        cutGeometry = null;
        var host = ReadOpeningSlab(geometry, links.Host);
        if (host.IsEmpty || geometry.StoryElevation(ReadElementLocation(host, "Slab", 0).HomeStoryIndex) is not { } home ||
            ReadSlabOpeningLimits(settings, host, 0) is not { } limits ||
            ReadSlabOpeningPlan(settings, profile) is not { } footprint)
            return null;

        var origin = ReadSlabOpeningOrigin(host);
        double area = 0;
        if (origin is { } hostOrigin)
            cutGeometry = ReadPlacedOpening(settings, profile, hostOrigin, footprint, out area);
        var cutPlan = OpeningCutPlan(footprint, profile);
        bool closed = cutGeometry is not null;
        var cuts = new List<PlaOpeningCut>();
        double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
        foreach (Guid target in links.Targets!)
        {
            var slab = target == links.Host ? host : ReadOpeningSlab(geometry, target);
            if (slab.IsEmpty || geometry.StoryElevation(ReadElementLocation(slab, "Slab", 0).HomeStoryIndex) is not { } level ||
                ReadElevations(slab, "Slab") is not { } planes)
                return null;
            var polygon = ReadSlabOutline(AfterCountedString(slab, ClassElementIdOffset("Slab")));
            if (ReadOutlineBounds(polygon) is not { } plan || ReadAfterVerticalSlabEdges(polygon).IsEmpty) return null;

            bool fits = false;
            if (cutGeometry is not null && origin is { } projectOrigin)
                fits = OpeningFitsSlab(polygon, cutGeometry, profile, TranslateOpeningPlan(footprint, projectOrigin));
            if (target != links.Host)
            {
                if (origin is not { } secondaryOrigin) return null;
                var projected = TranslateOpeningPlan(footprint, secondaryOrigin);
                // An authored cut target can remain linked after its slab outline moves out of reach.
                if (OpeningPlansDisjoint(projected, plan)) continue;
                if (!fits && (BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) != 0 ||
                    !SlabPolygonContainsFootprint(polygon, projected, allowBoundary: false))) return null;
            }
            double shift = level - home;
            var band = new Elevations(Math.Max(planes.Bottom + shift, limits.Bottom), Math.Min(planes.Top + shift, limits.Top));
            if (band.Top <= band.Bottom + JunctionTolerance) continue;
            if (!TryClipSlabOpening(slab, target, opening, links.Host, footprint, cutPlan, geometry, home, band, origin,
                double.IsNegativeInfinity(limits.Bottom) && double.IsPositiveInfinity(limits.Top),
                out var intervals, ref closed))
                return null;
            if (intervals.Count == 0) continue;
            bottom = Math.Min(bottom, intervals[0].Bottom);
            top = Math.Max(top, intervals[^1].Top);
            closed &= fits;
            if (closed)
                cuts.AddRange(intervals.Select(i => new PlaOpeningCut(target, i.Bottom + home, i.Top + home)));
        }
        if (closed && cutGeometry is not null && cuts.Count > 0)
        {
            var ordered = cuts.OrderBy(c => c.Bottom).ThenBy(c => c.Top).ToArray();
            if (ordered.All(c => double.IsFinite(c.Bottom) && double.IsFinite(c.Top) && c.Top > c.Bottom) &&
                ordered.Zip(ordered.Skip(1)).All(pair => pair.First.Top <= pair.Second.Bottom + JunctionTolerance))
            {
                double volume = ordered.Sum(c => c.Top - c.Bottom) * area;
                cutGeometry = cutGeometry with
                {
                    Cuts = Array.AsReadOnly(ordered),
                    CutVolume = double.IsFinite(volume) && volume > 0 ? volume : null,
                };
            }
        }
        return CheckedElevations(bottom, top);
    }

    private static ReadOnlySpan<byte> ReadOpeningSlab(ModelGeometry geometry, Guid id) =>
        geometry.Read(id, SlabClass) is { } record ? record.AsSpan(FindClassFrame(record, SlabClass)) : default;

    private static bool SlabOpeningMissesStrip(ModelGeometry geometry, Guid slab, Guid role, Guid relation,
        (double X0, double Y0, double X1, double Y1) strip, double home, Elevations band, out bool verifiedCut)
    {
        verifiedCut = false;
        var (opening, target) = role == CutElementRelation ? ReadOpeningCut(geometry, relation) : (relation, slab);
        if (target != slab || geometry.Read(opening, OpeningClass) is not { } record) return false;
        var hostIds = ReadOpeningRelation(record, OpeningHostRelation, owner: true);
        if (hostIds.Length != 16) return false;
        var host = new Guid(hostIds);
        if (role == OpeningHostRelation && host != slab) return false;
        var source = ReadOpeningSlab(geometry, host);
        if (source.IsEmpty || !HasGeometryRelation(source, OpeningHostRelation, opening) ||
            geometry.StoryElevation(ReadElementLocation(source, "Slab", 0).HomeStoryIndex) is not { } story)
            return false;
        var cuts = ReadOpeningRelation(record, OpeningCutRelation);
        int reciprocal = 0;
        for (int i = 0; i < cuts.Length; i += 16)
        {
            var id = new Guid(cuts.Slice(i, 16));
            if (role == CutElementRelation ? id == relation : ReadOpeningCut(geometry, id) == (opening, slab) &&
                HasGeometryRelation(source, CutElementRelation, id)) reciprocal++;
        }
        if (reciprocal != 1) return false;

        var settings = AfterCountedString(record.AsSpan(FindClassFrame(record, OpeningClass)), ClassElementIdOffset("Opening"));
        if (ReadSlabOpeningLimits(settings, source, story - home) is not { } limits) return false;
        verifiedCut = true;
        if (limits.Top <= band.Bottom + JunctionTolerance || limits.Bottom >= band.Top - JunctionTolerance) return true;
        if (ReadSlabOpeningOrigin(source) is not { } origin ||
            ReadOpeningProfile(settings) is not { } profile || ReadSlabOpeningPlan(settings, profile) is not { } plan)
            return false;
        if (profile.Shape == "Circle" && settings[256..].StartsWith(CenteredOpeningFrame))
            plan = OpeningCutPlan(plan, profile);
        else if (profile.Shape is not ("Rectangle" or "Square" or "Custom")) return false;
        return OpeningPlansDisjoint(strip, TranslateOpeningPlan(plan, origin));
    }

    private static ReadOnlySpan<byte> ReadAfterVerticalSlabEdges(ReadOnlySpan<byte> polygon)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]) -
            (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]);
        var edges = polygon[(int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..])..];
        return HasVerticalSlabEdges(edges, count) ? edges[(16 + count * 27)..] : default;
    }

    private static PlaPlanPoint? ReadSlabOpeningOrigin(ReadOnlySpan<byte> host)
    {
        var polygon = ReadSlabOutline(AfterCountedString(host, ClassElementIdOffset("Slab")));
        if (ReadOutlineBounds(polygon) is null) return null;
        var tail = ReadAfterVerticalSlabEdges(polygon);
        if (tail.Length < 87 || BinaryPrimitives.ReadUInt32LittleEndian(tail) != 2 ||
            ReadDouble(tail, 4) != Math.PI / 2 ||
            !tail[23..].StartsWith(new byte[] { 1, 0, 1, 0 }))
            return null;
        uint entries = BinaryPrimitives.ReadUInt32LittleEndian(tail[27..]);
        if (entries is not (1 or 3)) return null;
        int offset = 55 + (int)entries * 8;
        if (tail.Length < offset + 24 || ReadDouble(tail, offset + 16) != 0) return null;
        double x = ReadDouble(tail, offset), y = ReadDouble(tail, offset + 8);
        return double.IsFinite(x) && double.IsFinite(y) ? new PlaPlanPoint(x, y) : null;
    }

    private static Elevations? ReadSlabOpeningLimits(ReadOnlySpan<byte> settings, ReadOnlySpan<byte> host, double storyShift)
    {
        if (settings.Length < 290 ||
            ReadDouble(settings, 158) != 1 || ReadDouble(settings, 166) != 0 || ReadDouble(settings, 174) != 0 ||
            ReadDouble(settings, 182) != 0 || ReadDouble(settings, 190) != 1 || ReadDouble(settings, 198) != 0 ||
            ReadDouble(settings, 206) != 0 || ReadDouble(settings, 214) != 0 ||
            ReadDouble(settings, 222) is not (1 or -1))
            return null;

        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(settings[278..]);
        if (!settings[256..].StartsWith(CenteredOpeningFrame) &&
            !(mode == 0 && ReadDouble(settings, 222) == -1 && settings[256..].StartsWith(TopCenterOpeningFrame)))
            return null;
        if (mode == 0) return new Elevations(double.NegativeInfinity, double.PositiveInfinity);
        if (mode != 2) return null;

        var planes = AfterCountedString(host, ClassElementIdOffset("Slab"));
        if (!HasSupportedSlabReferencePlane(planes)) return null;
        double normal = ReadDouble(settings, 222);
        double cap = ReadDouble(planes, 0) + storyShift + ReadDouble(settings, 150) + normal * ReadDouble(settings, 282);
        if (!double.IsFinite(cap)) return null;
        return normal > 0 ? new Elevations(cap, double.PositiveInfinity) : new Elevations(double.NegativeInfinity, cap);
    }

    private static bool TryClipSlabOpening(ReadOnlySpan<byte> slab, Guid target, Guid opening, Guid host,
        (double X0, double Y0, double X1, double Y1) footprint, (double X0, double Y0, double X1, double Y1) cutPlan,
        ModelGeometry geometry, double home, Elevations band,
        PlaPlanPoint? origin, bool through, out List<Elevations> intervals, ref bool closed)
    {
        intervals = [];
        var cuts = ReadOpeningRelation(slab, CutElementRelation);
        if (cuts.IsEmpty) return false;
        var covered = new List<Elevations>();
        for (int i = 0; i < cuts.Length; i += 16)
        {
            var cut = new Guid(cuts.Slice(i, 16));
            var (peer, peerTarget) = ReadOpeningCut(geometry, cut);
            if (peerTarget != target || peer == Guid.Empty) return false;
            if (peer == opening) continue;
            if (geometry.Read(peer, OpeningClass) is not { } record || !HasGeometryRelation(record, OpeningCutRelation, cut))
                return false;
            var owner = ReadOpeningRelation(record, OpeningHostRelation, owner: true);
            if (owner.IsEmpty) return false;
            var peerHost = new Guid(owner);
            var associated = ReadOpeningSlab(geometry, peerHost);
            if (associated.IsEmpty || !HasGeometryRelation(associated, OpeningHostRelation, peer) ||
                geometry.StoryElevation(ReadElementLocation(associated, "Slab", 0).HomeStoryIndex) is not { } level)
                return false;
            var settings = AfterCountedString(record.AsSpan(FindClassFrame(record, OpeningClass)), ClassElementIdOffset("Opening"));
            if (ReadSlabOpeningLimits(settings, associated, level - home) is not { } limits) return false;

            // A through opening adds no plane. Overlapping through profiles need a plan Boolean for the closed cut.
            if (double.IsNegativeInfinity(limits.Bottom) && double.IsPositiveInfinity(limits.Top))
            {
                if (closed && through)
                {
                    var otherOrigin = peerHost == host ? origin : ReadSlabOpeningOrigin(associated);
                    closed = origin is { } a && otherOrigin is { } b &&
                        ReadOpeningProfile(settings) is { } otherProfile &&
                        ReadSlabOpeningPlan(settings, otherProfile) is { } other &&
                        OpeningPlansDisjoint(TranslateOpeningPlan(cutPlan, a), TranslateOpeningPlan(OpeningCutPlan(other, otherProfile), b));
                }
                continue;
            }
            double low = Math.Max(band.Bottom, limits.Bottom), high = Math.Min(band.Top, limits.Top);
            if (high <= low + JunctionTolerance) continue;
            if (peerHost != host || ReadOpeningProfile(settings) is not { } profile ||
                ReadSlabOpeningPlan(settings, profile) is not { } peerPlan)
                return false;
            if (OpeningPlansDisjoint(footprint, peerPlan))
            {
                closed &= OpeningPlansDisjoint(cutPlan, OpeningCutPlan(peerPlan, profile));
                continue;
            }
            // shortcut: partial coverage needs a projected polygon union and the slab's placement transform.
            if (footprint.X0 < peerPlan.X0 || footprint.X1 > peerPlan.X1 ||
                footprint.Y0 < peerPlan.Y0 || footprint.Y1 > peerPlan.Y1 ||
                !OpeningCoversFootprint(settings, profile, footprint))
                return false;
            closed &= cutPlan.X0 >= peerPlan.X0 && cutPlan.X1 <= peerPlan.X1 &&
                cutPlan.Y0 >= peerPlan.Y0 && cutPlan.Y1 <= peerPlan.Y1 && OpeningCoversFootprint(settings, profile, cutPlan);
            covered.Add(new Elevations(low, high));
        }
        double start = band.Bottom;
        foreach (var removed in covered.OrderBy(r => r.Bottom))
        {
            if (removed.Bottom > start + JunctionTolerance)
                intervals.Add(new Elevations(start, removed.Bottom));
            start = Math.Max(start, removed.Top);
        }
        if (band.Top > start + JunctionTolerance) intervals.Add(new Elevations(start, band.Top));
        return true;
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadSlabOpeningPlan(ReadOnlySpan<byte> settings,
        PlaOpeningProfile profile)
    {
        if (profile.Shape == "Custom" && settings[256..].StartsWith(TopCenterOpeningFrame)) return null;
        var (x, y) = SlabOpeningProfileOrigin(settings, profile);
        var plan = profile.Shape == "Custom" ? ReadOutlineBounds(settings[304..]) :
            (-profile.Width / 2, -profile.Height / 2, profile.Width / 2, profile.Height / 2);
        if (plan is not { } local) return null;
        var (x0, y0, x1, y1) = (x + local.X0, y + local.Y0, x + local.X1, y + local.Y1);
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1) ||
            x1 <= x0 || y1 <= y0) return null;
        return (x0, y0, x1, y1);
    }

    private static PlaPlanPoint SlabOpeningProfileOrigin(ReadOnlySpan<byte> settings, PlaOpeningProfile profile)
    {
        double y = ReadDouble(settings, 142);
        if (settings[256..].StartsWith(TopCenterOpeningFrame)) y -= profile.Height / 2;
        return new PlaPlanPoint(ReadDouble(settings, 134), y);
    }

    private static bool OpeningCoversFootprint(ReadOnlySpan<byte> settings, PlaOpeningProfile profile,
        (double X0, double Y0, double X1, double Y1) footprint)
    {
        var (x, y) = SlabOpeningProfileOrigin(settings, profile);
        var local = (footprint.X0 - x, footprint.Y0 - y, footprint.X1 - x, footprint.Y1 - y);
        if (profile.Shape is "Circle" or "Ellipse")
        {
            double dx = Math.Max(Math.Abs(local.Item1), Math.Abs(local.Item3)) / (profile.Width / 2);
            double dy = Math.Max(Math.Abs(local.Item2), Math.Abs(local.Item4)) / (profile.Height / 2);
            return dx * dx + dy * dy <= 1;
        }
        if (profile.Shape != "Custom") return true;
        var polygon = settings[304..];
        return BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) == 0 &&
            SlabPolygonContainsFootprint(polygon, local, allowBoundary: false);
    }
}
