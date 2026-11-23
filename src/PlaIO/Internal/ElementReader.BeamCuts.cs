using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid BeamMemoRelation = new("d5762ca7-d920-455d-a373-28ba6d47152a");
    private static readonly Guid PlanContourRelation = new("e6e5e667-9577-41ca-bf4a-da75b917513f");
    private static readonly Guid BeamBeamJunctionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
    private static readonly Guid FirstBeamCollisionRelation = new("89d11bdb-3a78-48ad-96e7-5b69836c591d");
    private static readonly Guid SecondBeamCollisionRelation = new("1d381463-2edc-4aad-91b0-027291e8da94");
    private static readonly Guid FirstBeamEndRelation = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73");
    private static readonly Guid SecondBeamEndRelation = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
    private const double JunctionTolerance = 1e-8;

    private readonly record struct BeamBand(double Bottom, double Top, double X0, double Y0, double X1, double Y1);
    private readonly record struct BeamMember(BeamPrism Beam, BeamBand Band, byte[] Memo, Guid TargetJunction);

    private static Elevations? ClipByBeamUnion(ModelGeometry geometry, List<(Guid Memo, Guid Junction)> contacts,
        Guid targetRelation, ElementLocation target, double home, (double X0, double Y0, double X1, double Y1) footprint, Elevations bounds,
        double lowerMax, double upperMin, bool joinedSlabWall = false)
    {
        if (joinedSlabWall && (footprint.X1 - footprint.X0 <= 2 * JunctionTolerance ||
            footprint.Y1 - footprint.Y0 <= 2 * JunctionTolerance))
            return null;
        var bands = new List<BeamBand>();
        var members = new Dictionary<Guid, BeamMember>();
        foreach (var (id, junction) in contacts)
        {
            if (geometry.Read(id, BeamMemoClass) is not { } memo ||
                ReadBeamPrism(geometry, id, memo, home) is not { } beam ||
                !SameIntersectionGroup(geometry, beam.Layer, target.LayerIndex))
                return null;
            var hatch = ReadBeamHatch(geometry, id, memo);
            if (BeamSectionElevations(beam, hatch) is not { } height) return null;
            if (height.Bottom >= bounds.Top || height.Top <= bounds.Bottom) continue;
            if (lowerMax != bounds.Bottom && height.Bottom < lowerMax || upperMin != bounds.Top && height.Top > upperMin)
                return null;
            if (beam.SinRoll != 0 || BeamPlanBand(beam, hatch) is not { } band ||
                !members.TryAdd(id, new BeamMember(beam, band, memo, junction)))
                return null;
        }
        foreach (var (id, member) in members)
        {
            BeamBand? extended = null;
            if (!HasOnlyBeamTargetContact(member.Memo, targetRelation, member.TargetJunction, member.Beam.Owner,
                (key, joint) => IsInternalParallelBeamCollision(geometry, members, id, key, joint) ||
                    KeepsTransverseBeamUnion(geometry, members, id, key, joint, home, footprint,
                        targetRelation == BeamColumnRelation, joinedSlabWall, ref extended) ||
                    joinedSlabWall && (key == BeamWallRelation || key == BeamSlabRelation)))
                return null;
            var band = extended ?? member.Band;
            if (band.X0 < footprint.X1 && band.X1 > footprint.X0 && band.Y0 < footprint.Y1 && band.Y1 > footprint.Y0)
                bands.Add(band);
        }

        // Coverage is constant between successive beam faces.
        var levels = bands.SelectMany(b => new[] { b.Bottom, b.Top })
            .Where(z => z > bounds.Bottom && z < bounds.Top)
            .Append(bounds.Bottom).Append(bounds.Top).Distinct().Order().ToArray();
        double? bottom = null, top = null;
        for (int i = 1; i < levels.Length; i++)
        {
            double low = levels[i - 1], high = levels[i];
            var active = bands.Where(b => b.Bottom <= low && b.Top >= high).ToArray();
            if (CoversSection(footprint, active, joinedSlabWall ? JunctionTolerance : 0)) continue;
            bottom ??= low;
            top = high;
        }
        return bottom is { } lo && top is { } hi ? CheckedElevations(lo, hi) : null;
    }

    private static bool IsInternalParallelBeamCollision(ModelGeometry geometry,
        Dictionary<Guid, BeamMember> members, Guid id, Guid key, Guid junction)
    {
        if (key != FirstBeamCollisionRelation && key != SecondBeamCollisionRelation) return false;
        if (geometry.Read(junction, BeamBeamJunctionClass) is not { } contact ||
            ReadSingleRelation(contact, key, requireSection: true) != id) return false;
        Guid reciprocal = key == FirstBeamCollisionRelation ? SecondBeamCollisionRelation : FirstBeamCollisionRelation;
        Guid otherId = ReadSingleRelation(contact, reciprocal, requireSection: true);
        if (!members.TryGetValue(otherId, out var other) || !HasGeometryRelation(other.Memo, reciprocal, junction)) return false;
        var (beam, band, _, _) = members[id];
        if (band.Bottom != other.Band.Bottom || band.Top != other.Band.Top) return false;
        // With parallel, separate axes and matching ends, cleanup only partitions the shared cross-section.
        return beam.Dx == 0 && other.Beam.Dx == 0 && beam.X != other.Beam.X &&
                Math.Abs(band.Y0 - other.Band.Y0) <= JunctionTolerance && Math.Abs(band.Y1 - other.Band.Y1) <= JunctionTolerance ||
            beam.Dy == 0 && other.Beam.Dy == 0 && beam.Y != other.Beam.Y &&
                Math.Abs(band.X0 - other.Band.X0) <= JunctionTolerance && Math.Abs(band.X1 - other.Band.X1) <= JunctionTolerance;
    }

    private static bool KeepsTransverseBeamUnion(ModelGeometry geometry, Dictionary<Guid, BeamMember> members, Guid id,
        Guid relation, Guid junction, double home, (double X0, double Y0, double X1, double Y1) footprint,
        bool columnTarget, bool joinedSlabWall, ref BeamBand? extended)
    {
        if (ReadBeamAxisJunction(geometry, id, relation, junction) is not { } joint) return false;
        var (neighbor, reciprocal, x, y) = joint;
        var (beam, band, ownMemo, targetJunction) = members[id];
        if (geometry.Read(neighbor, BeamMemoClass) is not { } memo ||
            !HasGeometryRelation(memo, reciprocal, junction) ||
            ReadBeamPrism(geometry, neighbor, memo, home) is not { } other || !SameIntersectionGroup(geometry, other.Layer, beam.Layer) ||
            other.SinRoll != 0 ||
            !(beam.Dx == 0 && other.Dy == 0 || beam.Dy == 0 && other.Dx == 0))
            return false;
        var otherHatch = ReadBeamHatch(geometry, neighbor, memo);
        if (BeamPlanBand(other, otherHatch) is not { } otherBand) return false;
        bool ownEnd = IsBeamEndRelation(relation), otherEnd = IsBeamEndRelation(reciprocal);
        if (!IsOnBeamJoint(beam, x, y, ownEnd) || !IsOnBeamJoint(other, x, y, otherEnd))
            return false;
        var (left, right) = ProjectRectangle(footprint, other.X, other.Y, other.Dy, -other.Dx);
        double halfWidth = other.Dx == 0 ? (otherBand.X1 - otherBand.X0) / 2 : (otherBand.Y1 - otherBand.Y0) / 2;
        if (right < -halfWidth - JunctionTolerance || left > halfWidth + JunctionTolerance)
            return true;
        if (band.Bottom != otherBand.Bottom || band.Top != otherBand.Top) return false;
        if (ownEnd && otherEnd)
        {
            if (!joinedSlabWall || !members.ContainsKey(neighbor)) return false;
            var miterHatch = ReadBeamHatch(geometry, id, ownMemo);
            if (ReadBeamRectangle(miterHatch) is not { } ownSection ||
                beam.Length <= halfWidth + JunctionTolerance || other.Length <= ownSection.HalfWidth + JunctionTolerance)
                return false;
            var horizontal = beam.Dy == 0 ? beam : other;
            var vertical = beam.Dx == 0 ? beam : other;
            bool outsideX = horizontal.X + horizontal.Dx * horizontal.Length / 2 < x
                ? footprint.X1 > x + JunctionTolerance : footprint.X0 < x - JunctionTolerance;
            bool outsideY = vertical.Y + vertical.Dy * vertical.Length / 2 < y
                ? footprint.Y1 > y + JunctionTolerance : footprint.Y0 < y - JunctionTolerance;
            // The miter can extend the nominal union outside both arms at the corner.
            if (outsideX && outsideY) return false;
            return geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(miterHatch[18..])) is { } priority &&
                priority == geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(otherHatch[18..]));
        }
        if (ownEnd == otherEnd) return false;
        var host = ownEnd ? other : beam;
        var branch = ownEnd ? band : otherBand;
        var (start, stop) = ProjectRectangle((branch.X0, branch.Y0, branch.X1, branch.Y1), host.X, host.Y, host.Dx, host.Dy);
        if (start <= JunctionTolerance || stop >= host.Length - JunctionTolerance) return false;
        // With two cutters, the through beam contains any change to the joined end.
        if (members.ContainsKey(neighbor)) return true;
        if (!columnTarget || members.Count != 1 || extended is not null ||
            ReadBeamHatch(geometry, id, ownMemo) is not { Length: >= 22 } ownHatch ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(ownHatch[18..])) is not { } ownPriority ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(otherHatch[18..])) is not { } otherPriority ||
            ownPriority <= otherPriority ||
            geometry.Read(targetJunction, BeamColumnJunctionClass) is not { } target)
            return false;
        Guid column = ReadSingleRelation(target, ColumnBeamRelation, requireSection: true);
        if (column == Guid.Empty ||
            !HasOnlyBeamTargetContact(memo, reciprocal, junction, other.Owner, (key, joint) =>
                key == BeamColumnRelation && geometry.Read(joint, BeamColumnJunctionClass) is { } beamColumn &&
                ReadSingleRelation(beamColumn, BeamColumnRelation, requireSection: true) == neighbor &&
                ReadSingleRelation(beamColumn, ColumnBeamRelation, requireSection: true) == column))
            return false;
        if (!ownEnd) return true;
        // The stronger branch occupies its strip to the far face of the through beam.
        extended = beam.Dx == 0
            ? band with { Y0 = Math.Min(band.Y0, otherBand.Y0), Y1 = Math.Max(band.Y1, otherBand.Y1) }
            : band with { X0 = Math.Min(band.X0, otherBand.X0), X1 = Math.Max(band.X1, otherBand.X1) };
        return true;
    }

    private static (Guid Peer, Guid Reciprocal, double X, double Y)? ReadBeamAxisJunction(ModelGeometry geometry, Guid id,
        Guid relation, Guid junction, bool withoutPoint = false)
    {
        if (relation != FirstBeamCollisionRelation && relation != SecondBeamCollisionRelation && !IsBeamEndRelation(relation) ||
            geometry.Read(junction, BeamBeamJunctionClass) is not { Length: >= 67 } contact ||
            !contact.AsSpan(35).StartsWith(new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }) ||
            contact[66] != (withoutPoint ? 0 : 1) || ReadSingleRelation(contact, relation, requireSection: true) != id)
            return null;
        bool first = relation == FirstBeamEndRelation || relation == SecondBeamCollisionRelation;
        Guid end = first ? SecondBeamEndRelation : FirstBeamEndRelation;
        Guid interior = first ? FirstBeamCollisionRelation : SecondBeamCollisionRelation;
        Guid endNeighbor = ReadSingleRelation(contact, end, requireSection: true);
        Guid interiorNeighbor = ReadSingleRelation(contact, interior, requireSection: true);
        if ((endNeighbor == Guid.Empty) == (interiorNeighbor == Guid.Empty)) return null;
        Guid peer = endNeighbor == Guid.Empty ? interiorNeighbor : endNeighbor;
        Guid reciprocal = endNeighbor == Guid.Empty ? interior : end;
        double x = ReadDouble(contact, 50), y = ReadDouble(contact, 58);
        if (peer == id || withoutPoint && (IsBeamEndRelation(relation) || IsBeamEndRelation(reciprocal) || x != 0 || y != 0))
            return null;
        return (peer, reciprocal, x, y);
    }

    private static bool IsOnBeamJoint(BeamPrism beam, double x, double y, bool end)
    {
        double along = (x - beam.X) * beam.Dx + (y - beam.Y) * beam.Dy;
        double across = (x - beam.X) * beam.Dy - (y - beam.Y) * beam.Dx;
        return Math.Abs(across) <= JunctionTolerance && (end
            ? Math.Abs(along) <= JunctionTolerance || Math.Abs(along - beam.Length) <= JunctionTolerance
            : along > JunctionTolerance && along < beam.Length - JunctionTolerance);
    }

    private static bool IsBeamEndRelation(Guid relation) => relation == FirstBeamEndRelation || relation == SecondBeamEndRelation;

    private static BeamBand? BeamPlanBand(BeamPrism beam, ReadOnlySpan<byte> hatch)
    {
        if (ReadBeamRectangle(hatch) is not { } section) return null;
        double half = section.HalfWidth;
        double along = beam.Length * beam.Dx, across = beam.Length * beam.Dy;
        // The plan sweep below is for beams whose section is parallel to a project axis.
        if (along == 0)
            return new BeamBand(beam.Z - section.Depth, beam.Z, beam.X - half, beam.Y + Math.Min(0, across),
                beam.X + half, beam.Y + Math.Max(0, across));
        if (across == 0)
            return new BeamBand(beam.Z - section.Depth, beam.Z, beam.X + Math.Min(0, along), beam.Y - half,
                beam.X + Math.Max(0, along), beam.Y + half);
        return null;
    }

    private static bool CoversSection((double X0, double Y0, double X1, double Y1) footprint, BeamBand[] active, double tolerance)
    {
        if (active.Length == 0) return false;
        var edges = active.SelectMany(b => new[] { b.X0, b.X1 }).Where(x => x > footprint.X0 && x < footprint.X1)
            .Append(footprint.X0).Append(footprint.X1).Distinct().Order().ToArray();
        Array.Sort(active, (a, b) => a.Y0.CompareTo(b.Y0));
        for (int i = 1; i < edges.Length; i++)
        {
            double y = footprint.Y0;
            foreach (var b in active)
            {
                if (b.Y0 > y + tolerance) break;
                if (b.X0 <= edges[i - 1] + tolerance && b.X1 >= edges[i] - tolerance)
                    y = Math.Max(y, b.Y1);
            }
            if (y < footprint.Y1 - tolerance) return false;
        }
        return true;
    }

    private static Elevations? ClipByBeam(ModelGeometry geometry, Guid beamId, Guid junction, Guid targetRelation, ElementLocation target,
        double home, (double X0, double Y0, double X1, double Y1) footprint, Elevations bounds, double lowerMax, double upperMin)
    {
        if (geometry.Read(beamId, BeamMemoClass) is not { } memo ||
            ReadBeamPrism(geometry, beamId, memo, home) is not { } prism || !SameIntersectionGroup(geometry, prism.Layer, target.LayerIndex) ||
            targetRelation == BeamWallRelation && prism.SinRoll != 0)
            return null;
        var hatch = ReadBeamHatch(geometry, beamId, memo);
        if (ReadBeamRectangle(hatch) is not { } section) return null;
        var (owner, _, x, y, z, ux, uy, length, sine, cosine) = prism;
        double zSpan = Math.Abs(sine) * section.HalfWidth;
        double tSpan = Math.Abs(cosine) * section.HalfWidth;
        double tOffset = sine * section.Depth;
        var (t0, t1) = ProjectRectangle(footprint, x, y, uy, -ux);
        double prismBottom = z - cosine * section.Depth - zSpan, prismTop = z + zSpan;
        if (prismTop <= bounds.Bottom + JunctionTolerance || prismBottom >= bounds.Top - JunctionTolerance ||
            t1 <= Math.Min(0, tOffset) - tSpan || t0 >= Math.Max(0, tOffset) + tSpan)
            return bounds;

        if (prismBottom > lowerMax + JunctionTolerance && prismTop < upperMin - JunctionTolerance) return bounds;
        double tiltedLow = z + Math.Min(sine * t0, sine * t1) / cosine;
        double tiltedHigh = z + Math.Max(sine * t0, sine * t1) / cosine;
        double depth = section.Depth / cosine;

        var (s0, s1) = ProjectRectangle(footprint, x, y, ux, uy);
        if (s0 <= JunctionTolerance || s1 >= length - JunctionTolerance ||
            !HasOnlyBeamTargetContact(memo, targetRelation, junction, owner))
            return null;
        if (t0 >= -tSpan && t1 <= tSpan && FaceFits(bounds.Bottom, lowerMax) && tiltedLow >= lowerMax &&
            tiltedHigh < upperMin)
            return CheckedElevations(tiltedLow, bounds.Top);
        if (t0 >= tOffset - tSpan && t1 <= tOffset + tSpan && FaceFits(upperMin, bounds.Top) && tiltedHigh - depth <= upperMin &&
            tiltedLow - depth > lowerMax)
            return CheckedElevations(bounds.Bottom, tiltedHigh - depth);
        return null;

        bool FaceFits(double bottom, double top) =>
            Fits(t0, bottom) && Fits(t0, top) && Fits(t1, bottom) && Fits(t1, top);

        bool Fits(double across, double elevation)
        {
            double tz = elevation - z;
            double localX = cosine * across + sine * tz, localZ = cosine * tz - sine * across;
            return Math.Abs(localX) <= section.HalfWidth && localZ <= JunctionTolerance &&
                localZ >= -section.Depth - JunctionTolerance;
        }
    }

    private readonly record struct BeamPrism(Guid Owner, int? Layer, double X, double Y, double Z, double Dx, double Dy,
        double Length, double SinRoll, double CosRoll);

    private readonly record struct BeamEndPlanes(double Rise, BeamVector StartNormal, BeamVector EndNormal)
    {
        public bool HasSquareHorizontalEnds(BeamPrism axis) => Rise == 0 &&
            AlongAxis(StartNormal, -axis.Dx, -axis.Dy) && AlongAxis(EndNormal, axis.Dx, axis.Dy);
    }

    private static BeamPrism? ReadBeamPrism(ModelGeometry geometry, Guid beamId, ReadOnlySpan<byte> memo, double home) =>
        ReadStraightBeamAxis(geometry, beamId, memo, home, out var planes) is { } axis && planes.HasSquareHorizontalEnds(axis)
            ? axis : null;

    private static Guid ReadBeamPrismMemo(ReadOnlySpan<byte> beam, Guid owner, ModelGeometry geometry, bool readAnnotations = false) =>
        ReadPrismMemo(beam, BeamMemoRelation,
            acceptsReference: (role, target) => IsPrismAnnotation(geometry, owner, role, target, readAnnotations));

    private static BeamPrism? ReadStraightBeamAxis(ModelGeometry geometry, Guid beamId, ReadOnlySpan<byte> memo,
        double home, out BeamEndPlanes planes, bool readAnnotations = false)
    {
        planes = default;
        Guid owner = ReadSingleRelation(memo, BeamMemoRelation, requireSection: true);
        if (geometry.Read(owner, BeamClass) is not { } body) return null;
        var beam = body.AsSpan(FindClassFrame(body, BeamClass));
        if (ReadBeamPrismMemo(beam, owner, geometry, readAnnotations) != beamId ||
            ReadBeamAxisFrame(beam, owner, geometry) is not { Sweep: 0 } frame)
            return null;
        planes = frame.Planes;
        double z = frame.Axis.Z + (frame.Story - home);
        return double.IsFinite(z) && double.IsFinite(z + planes.Rise) ? frame.Axis with { Z = z } : null;
    }

    private readonly record struct BeamAxisFrame(BeamPrism Axis, BeamEndPlanes Planes, double EndX, double EndY,
        double EndZ, double Story, double Sweep);

    private static BeamAxisFrame? ReadBeamAxisFrame(ReadOnlySpan<byte> beam, Guid owner, ModelGeometry geometry)
    {
        if (beam.Length < 735 || !beam[721..].StartsWith(ElementIdFrame) ||
            !beam.Slice(304, ColumnEndPlaneFrame.Length).SequenceEqual(ColumnEndPlaneFrame) ||
            BinaryPrimitives.ReadUInt16LittleEndian(beam[368..]) != 1)
            return null;

        var location = ReadElementLocation(beam, "Beam", 0);
        if (geometry.StoryElevation(location.HomeStoryIndex) is not { } level) return null;
        var settings = AfterCountedString(beam, 735);
        if (settings.Length < 254 || ReadDouble(settings, 1) != 0 ||
            ReadDouble(settings, 133) != 1 || ReadDouble(settings, 238) != 1)
            return null;
        double x = ReadDouble(beam, 312), y = ReadDouble(beam, 320), z = ReadDouble(beam, 328);
        double x1 = ReadDouble(beam, 370), y1 = ReadDouble(beam, 378), endZ = ReadDouble(beam, 386);
        double dx = x1 - x, dy = y1 - y, length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || !(length > 0) || !double.IsFinite(z) || !double.IsFinite(endZ) ||
            !MatchesBeamAxisPoint(settings[17..], x, y) || !MatchesBeamAxisPoint(settings[33..], x1, y1))
            return null;
        double ux = dx / length, uy = dy / length;
        double roll = ReadDouble(settings, 155), cosine = Math.Cos(roll);
        if (roll != ReadDouble(settings, 246) || !(cosine > JunctionTolerance)) return null;
        double rise = endZ - z, sweep = ReadDouble(settings, 49);
        if (!double.IsFinite(rise) || !(Math.Abs(sweep) < Math.Tau)) return null;
        var planes = new BeamEndPlanes(rise, ReadBeamVector(beam, 336), ReadBeamVector(beam, 394));
        var axis = new BeamPrism(owner, location.LayerIndex, x, y, z, ux, uy, length, Math.Sin(roll), cosine);
        return new BeamAxisFrame(axis, planes, x1, y1, endZ, level, sweep);
    }

    private static PlaBeamReferenceAxis? ReadBeamReferenceAxis(ReadOnlySpan<byte> beam, Guid owner, ModelGeometry geometry)
    {
        if (ReadBeamAxisFrame(beam, owner, geometry) is not { } frame) return null;
        Guid memoId = ReadSingleRelation(beam, BeamMemoRelation, 0x11, requireSection: true);
        if (geometry.Read(memoId, BeamMemoClass) is not { } memo ||
            ReadSingleRelation(memo, BeamMemoRelation, requireSection: true) != owner)
            return null;

        var axis = frame.Axis;
        double sweep = frame.Sweep;
        if (sweep != 0)
        {
            if (frame.Planes.Rise != 0) return null;
            // Radial cap planes independently confirm the tangents of the saved circular axis.
            double c = Math.Cos(sweep / 2), s = Math.Sin(sweep / 2);
            if (!MatchesTangent(frame.Planes.StartNormal, -axis.Dx * c - axis.Dy * s, -axis.Dy * c + axis.Dx * s) ||
                !MatchesTangent(frame.Planes.EndNormal, axis.Dx * c - axis.Dy * s, axis.Dy * c + axis.Dx * s))
                return null;
        }
        double start = axis.Z + frame.Story, end = frame.EndZ + frame.Story;
        var reference = new PlaBeamReferenceAxis(new(axis.X, axis.Y), new(frame.EndX, frame.EndY), start, end, sweep);
        return double.IsFinite(start) && double.IsFinite(end) && double.IsFinite(reference.Length) ? reference : null;

        static bool MatchesTangent(BeamVector normal, double x, double y) =>
            Math.Abs(normal.Z) <= JunctionTolerance && AlongAxis(normal with { Z = 0 }, x, y);
    }

    private static bool BeamLeavesWallSection(ModelGeometry geometry, Guid beamId, ReadOnlySpan<byte> memo,
        ReadOnlySpan<byte> hatch, ElementLocation wall, Elevations bounds,
        WallStrip? strip, List<WallCapObstacle> blocked)
    {
        if (geometry.StoryElevation(wall.HomeStoryIndex) is not { } home ||
            ReadBeamPrism(geometry, beamId, memo, home) is not { } beam ||
            BeamSectionExtents(beam, hatch) is not { } section)
            return false;
        if (MissesEndFaces(section.Height.Bottom, section.Height.Top, bounds, bounds.Bottom, bounds.Top))
            return true;
        if (strip is not { } s) return false;
        double along = s.AlongX ? beam.Dy : -beam.Dx;
        double across = s.AlongX ? -beam.Dx : beam.Dy;
        double originAlong = s.AlongX ? beam.X : beam.Y, originAcross = s.AlongX ? beam.Y : beam.X;
        double a = (s.AcrossMin - originAcross) * across, b = (s.AcrossMax - originAcross) * across;
        if (along == 0)
            return Math.Max(a, b) <= section.Left || Math.Min(a, b) >= section.Right;
        double start = originAlong + (section.Left - Math.Max(a, b)) / along;
        double end = originAlong + (section.Right - Math.Min(a, b)) / along;
        if (!double.IsFinite(start) || !double.IsFinite(end)) return false;
        blocked.Add(new((Math.Min(start, end), Math.Max(start, end)), section.Height));
        return true;
    }

    private static Elevations? BeamSectionElevations(BeamPrism beam, ReadOnlySpan<byte> hatch) =>
        BeamSectionExtents(beam, hatch)?.Height;

    private static (Elevations Height, double Left, double Right)? BeamSectionExtents(BeamPrism beam, ReadOnlySpan<byte> hatch)
    {
        var points = StraightSectionVertices(hatch);
        if (points.IsEmpty) return null;
        double z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
        double t0 = double.PositiveInfinity, t1 = double.NegativeInfinity;
        for (int i = 0; i < points.Length; i += 16)
        {
            double x = ReadDouble(points, i), z = ReadDouble(points, i + 8);
            double elevation = beam.Z + beam.SinRoll * x + beam.CosRoll * z;
            double across = beam.CosRoll * x - beam.SinRoll * z;
            z0 = Math.Min(z0, elevation);
            z1 = Math.Max(z1, elevation);
            t0 = Math.Min(t0, across);
            t1 = Math.Max(t1, across);
        }
        return z1 > z0 && t1 > t0 && double.IsFinite(t0) && double.IsFinite(t1) && CheckedElevations(z0, z1) is { } height
            ? (height, t0, t1) : null;
    }

    private static bool BeamCannotCutColumnEnds(ModelGeometry geometry, Guid beamId, double home,
        Elevations bounds, double lowerMax, double upperMin)
    {
        return geometry.Read(beamId, BeamMemoClass) is { } memo &&
            ReadBeamPrism(geometry, beamId, memo, home) is { } beam &&
            BeamSectionElevations(beam, ReadBeamHatch(geometry, beamId, memo)) is { } section &&
            MissesEndFaces(section.Bottom, section.Top, bounds, lowerMax, upperMin);
    }

    private static double ReadDouble(ReadOnlySpan<byte> record, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(record[offset..]);

    private static bool AlongAxis(BeamVector normal, double x, double y) =>
        Math.Abs(normal.X - x) <= JunctionTolerance && Math.Abs(normal.Y - y) <= JunctionTolerance && normal.Z == 0;

    private static bool MatchesBeamAxisPoint(ReadOnlySpan<byte> setting, double x, double y) =>
        Math.Abs(ReadDouble(setting, 0) - x) <= JunctionTolerance &&
        Math.Abs(ReadDouble(setting, 8) - y) <= JunctionTolerance;

    private static (double Min, double Max) ProjectRectangle((double X0, double Y0, double X1, double Y1) xy,
        double x, double y, double dx, double dy)
    {
        double x0 = (xy.X0 - x) * dx, x1 = (xy.X1 - x) * dx;
        double y0 = (xy.Y0 - y) * dy, y1 = (xy.Y1 - y) * dy;
        return (Math.Min(x0, x1) + Math.Min(y0, y1), Math.Max(x0, x1) + Math.Max(y0, y1));
    }

    private static (double HalfWidth, double Depth)? ReadBeamRectangle(ReadOnlySpan<byte> hatch)
    {
        if (hatch.Length < 161 || BinaryPrimitives.ReadUInt32LittleEndian(hatch[55..]) != 6) return null;
        var points = hatch[59..];
        if (BinaryPrimitives.ReadUInt16LittleEndian(points[96..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(points[98..]) != 0 ||
            points[..16].IndexOfAnyExcept((byte)0) >= 0 || !points.Slice(16, 16).SequenceEqual(points.Slice(80, 16)))
            return null;
        double halfWidth = Math.Abs(ReadDouble(points, 16));
        double depth = -Math.Min(ReadDouble(points, 24), Math.Min(ReadDouble(points, 40), ReadDouble(points, 56)));
        if (!(halfWidth > 0 && double.IsFinite(halfWidth) && depth > 0 && double.IsFinite(depth))) return null;
        uint corners = 0;
        for (int i = 16; i < 80; i += 16)
        {
            double x = ReadDouble(points, i), y = ReadDouble(points, i + 8);
            if (Math.Abs(x) != halfWidth || (y != 0 && y != -depth)) return null;
            corners |= 1u << ((x < 0 ? 0 : 1) + (y == 0 ? 0 : 2));
            if (i != 16 && ReadDouble(points, i - 16) != x && ReadDouble(points, i - 8) != y) return null;
        }
        return corners == 15 && (ReadDouble(points, 64) == ReadDouble(points, 16) ||
            ReadDouble(points, 72) == ReadDouble(points, 24)) ? (halfWidth, depth) : null;
    }

    private static bool HasOnlyBeamTargetContact(ReadOnlySpan<byte> memo, Guid targetRelation, Guid junction, Guid beam,
        Func<Guid, Guid, bool>? acceptsExtraContact = null)
    {
        int start = FindRelationshipSection(memo, memo.Length);
        if (start < 0) return false;
        var data = memo[start..];
        bool found = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid target)) return false;
            if ((tag is 1 or 0x11) && key == DrawingRelation ||
                tag == 0x11 && (key == SectionProfileRelation || key == PlanContourRelation) ||
                tag == 0x12 && key == BeamMemoRelation && target == beam ||
                tag == 0x11 && (key == FirstColumnPartRelation || key == SecondColumnPartRelation))
                continue;
            if (tag is not (1 or 0x11)) return false;
            var ids = ReadRelationIds(entry, data, tag);
            if (ids.IsEmpty) return false;
            for (int i = 0; i < ids.Length; i += 16)
            {
                var other = new Guid(ids.Slice(i, 16));
                if (other == Guid.Empty) return false;
                if (key == targetRelation && other == junction && !found)
                    found = true;
                else if (other == junction || acceptsExtraContact is null || !acceptsExtraContact(key, other))
                    return false;
            }
        }
        // shortcut: other beam contacts can alter the cutter, resolve their geometry before accepting it.
        return (found || targetRelation == Guid.Empty) && data is [0];
    }
}
