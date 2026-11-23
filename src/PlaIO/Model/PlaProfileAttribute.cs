namespace PlaIO.Model;

/// <summary>A saved complex profile definition.</summary>
/// <param name="Guid">The saved attribute GUID.</param>
/// <param name="Index">The positive project-local profile index.</param>
/// <param name="Name">The saved attribute name.</param>
/// <param name="UseWith">The enabled element types, or null for unsupported flags.</param>
/// <param name="Width">The maximum width of the undeformed section in metres, or null if unavailable.</param>
/// <param name="Height">The maximum height of the undeformed section in metres, or null if unavailable.</param>
/// <param name="HasCoreSkin">Whether a component is marked as core, or null if the section is unavailable.</param>
/// <param name="Components">The undeformed material fills in saved drawing order, or null if unavailable.</param>
public sealed record PlaProfileAttribute(Guid Guid, int Index, string Name, IReadOnlyList<string>? UseWith,
    double? Width, double? Height, bool? HasCoreSkin, IReadOnlyList<PlaProfileComponent>? Components)
{
    /// <summary>The saved width stretch setting, or null if unavailable.</summary>
    public PlaProfileStretch? WidthStretch { get; init; }

    /// <summary>The saved height stretch setting, or null if unavailable.</summary>
    public PlaProfileStretch? HeightStretch { get; init; }

    /// <summary>Verified named offset modifiers in saved order, or null if the list is unavailable.</summary>
    public IReadOnlyList<PlaProfileModifier>? Modifiers { get; init; }
}

/// <summary>Cross-section stretch settings for one local axis.</summary>
/// <param name="Enabled">Whether the saved profile can stretch along this axis.</param>
/// <param name="Start">The lower boundary in local metres, or null when disabled.</param>
/// <param name="End">The upper boundary in local metres, or null when disabled.</param>
/// <param name="MinimumSize">The section size in metres after contracting the entire stretch zone, or null if unavailable.</param>
public readonly record struct PlaProfileStretch(bool Enabled, double? Start, double? End, double? MinimumSize);

/// <summary>The identity and saved name of a profile's offset modifier.</summary>
/// <param name="Guid">The GUID of the modifier in this profile.</param>
/// <param name="DefinitionGuid">The GUID of the shared named definition.</param>
/// <param name="Name">The saved modifier name.</param>
public sealed record PlaProfileModifier(Guid Guid, Guid DefinitionGuid, string Name)
{
    /// <summary>The signed default offset in metres measured on the undeformed profile, or null if unavailable.</summary>
    public double? DefaultValue { get; init; }
}
