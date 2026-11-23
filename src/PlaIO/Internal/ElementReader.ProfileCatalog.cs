using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ProfileAttributeClass = new("31ab5131-81ae-4181-a714-7e4d3dd1a91d");

    internal static IReadOnlyList<PlaProfileAttribute> ReadProfileCatalog(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var kinds = classes.Where(c => c.Guid == ProfileAttributeClass).ToDictionary(c => c.Number, c => c.Guid);
        if (kinds.Count == 0) return [];
        var modifierRecords = new Lazy<Dictionary<Guid, byte[]?>>(() => ReadProfileModifierRecords(document, classes));
        var profiles = new Dictionary<int, PlaProfileAttribute>();
        var seen = new HashSet<Guid>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || block.Guid is not { } id || id == Guid.Empty ||
                !kinds.ContainsKey(block.Kind)) continue;
            var payload = block.Mode == PlaBlockMode.Lz ? document.ReadStandalonePayload(block) :
                document.PayloadStartsWithClass(block, ProfileAttributeClass) ? document.ReadPayload(block) : null;
            if (payload is not null) Add(id, payload);
        }

        foreach (var (record, block) in FindUnindexedAttributeBlocks(document, kinds, seen))
            if (document.ReadDirectoryPayload(block) is { } payload)
                Add(record.ObjectId, payload);

        return profiles.Values.OrderBy(p => p.Index).ToList().AsReadOnly();

        void Add(Guid id, byte[] payload)
        {
            if (payload.Length < 16 || new Guid(payload.AsSpan(0, 16)) != ProfileAttributeClass) return;
            var data = ReadNamedAttribute(payload, out var name, out int index, frame: 51);
            if (data.IsEmpty) return;
            var useWith = data.Length >= 37 ? ReadProfileUsage(BinaryPrimitives.ReadUInt16LittleEndian(data[35..])) : null;
            var section = ReadSectionVector(data, 37);
            var components = ReadProfileComponents(section, allowUnassignedMaterial: true);
            var bounds = components is not null ? ReadProfileBounds(components) : default;
            var rest = section.IsEmpty ? default : data[(45 + section.Length)..];
            var stretch = ReadProfileStretchSettings(rest);
            var profile = new PlaProfileAttribute(id, index, name!, useWith, bounds?.X1 - bounds?.X0, bounds?.Y1 - bounds?.Y0,
                components?.Any(c => c.Function == "Core"), components)
            {
                WidthStretch = stretch.IsEmpty ? null : ReadProfileStretch(stretch[26..], bounds?.X0, bounds?.X1),
                HeightStretch = stretch.IsEmpty ? null : ReadProfileStretch(stretch[43..], bounds?.Y0, bounds?.Y1),
                Modifiers = ReadProfileModifiers(payload, id, rest, section, components, modifierRecords),
            };
            if (!profiles.TryAdd(index, profile))
            {
                var prior = profiles[index];
                if (prior.Guid != id || prior.Name != name || (prior.UseWith is null) != (useWith is null) ||
                    !(prior.UseWith ?? []).SequenceEqual(useWith ?? []) || prior.WidthStretch != profile.WidthStretch ||
                    prior.HeightStretch != profile.HeightStretch || (prior.Modifiers is null) != (profile.Modifiers is null) ||
                    !(prior.Modifiers ?? []).SequenceEqual(profile.Modifiers ?? []) ||
                    !SameProfile(index, index, prior.Components, components))
                    throw new InvalidDataException($"Profile attribute {index} has conflicting records.");
                return;
            }
            if (!seen.Add(id)) throw new InvalidDataException($"Profile attribute {id:D} has conflicting indices.");
        }
    }

    private static IReadOnlyList<string>? ReadProfileUsage(ushort flags)
    {
        if ((flags & ~31) != 0) return null;
        var types = new List<string>(5);
        if ((flags & 1) != 0) types.Add("Wall");
        if ((flags & 4) != 0) types.Add("Beam");
        if ((flags & 2) != 0) types.Add("Column");
        if ((flags & 8) != 0) types.Add("Railing");
        if ((flags & 16) != 0) types.Add("Object");
        return types.AsReadOnly();
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadProfileBounds(IReadOnlyList<PlaProfileComponent> components)
    {
        double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        foreach (var component in components)
        {
            foreach (var ring in component.Rings)
                foreach (var vertex in ring)
                    Include(vertex.U, vertex.Z);
            foreach (var arc in component.Arcs ?? [])
            {
                var ring = component.Rings[arc.RingIndex];
                var start = ring[arc.StartVertexIndex];
                var end = ring[(arc.StartVertexIndex + 1) % ring.Count];
                if (ReadCircularEdge(new(start.U, start.Z), new(end.U, end.Z), arc.SweepRadians, out _) is not { } edge)
                    return null;
                for (int axis = 0; axis < 4; axis++)
                {
                    double angle = axis * (Math.PI / 2);
                    if (edge.IncludesAngle(angle))
                    {
                        var point = edge.AtAngle(angle);
                        Include(point.X, point.Y);
                    }
                }
            }
        }
        double width = xMax - xMin, height = yMax - yMin;
        return width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height) ? (xMin, yMin, xMax, yMax) : null;

        void Include(double x, double y)
        {
            xMin = Math.Min(x, xMin);
            xMax = Math.Max(x, xMax);
            yMin = Math.Min(y, yMin);
            yMax = Math.Max(y, yMax);
        }
    }
}
