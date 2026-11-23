namespace PlaIO.Model;

/// <summary>The saved layout book.</summary>
/// <param name="Guid">The navigator GUID of the book root.</param>
/// <param name="Items">Items in depth-first order, with siblings in saved order.</param>
public sealed record PlaLayoutBook(Guid Guid, IReadOnlyList<PlaLayoutBookItem> Items);

/// <summary>An item in the layout book.</summary>
/// <param name="Guid">The navigator item GUID.</param>
/// <param name="ParentGuid">The parent item's GUID, or the book root GUID for a top-level item.</param>
/// <param name="Type">The Archicad navigator type label.</param>
/// <param name="Name">The saved name, or null if unavailable.</param>
/// <param name="Id">The item identifier displayed in the navigator, or null if unavailable.</param>
/// <param name="SourceGuid">The source GUID for a layout, master layout or drawing, or null if unavailable.</param>
/// <param name="DrawingSourcePath">The saved source path of a placed drawing, or null if unavailable or inapplicable.</param>
/// <param name="PaperSize">The saved paper size of a master layout or the size inherited by a layout.</param>
public sealed record PlaLayoutBookItem(Guid Guid, Guid ParentGuid, string Type, string? Name, string? Id,
    Guid? SourceGuid, string? DrawingSourcePath, PlaLayoutPaperSize? PaperSize)
{
    /// <summary>The GUID of the verified layout or master layout database, or null if unavailable.</summary>
    public Guid? DatabaseGuid { get; init; }

    /// <summary>The navigator GUID of the master assigned to this layout, or null if unavailable or inapplicable.</summary>
    public Guid? MasterLayoutGuid { get; init; }

    /// <summary>The saved revision status of a layout, or null if unavailable or inapplicable.</summary>
    public PlaLayoutRevision? Revision { get; init; }
}

/// <summary>The current document revision of a layout.</summary>
/// <param name="CurrentRevisionId">The revision ID, an empty string when there is no revision, or null if unavailable.</param>
/// <param name="CurrentFinalRevisionId">The display ID, an empty string when there is no revision, or null if unavailable.</param>
/// <param name="HasIssuedRevision">Whether the layout has an issued revision.</param>
/// <param name="HasActualRevision">Whether the layout has an open revision.</param>
public sealed record PlaLayoutRevision(string? CurrentRevisionId, string? CurrentFinalRevisionId,
    bool HasIssuedRevision, bool HasActualRevision)
{
    /// <summary>Issued revisions in saved order, oldest first.</summary>
    public IReadOnlyList<PlaIssuedLayoutRevision> IssuedRevisions { get; init; } = [];

    /// <summary>The transmittal set of an open revision, or null if unavailable or inapplicable.</summary>
    public PlaLayoutTransmittal? OpenTransmittal { get; init; }
}

/// <summary>An issued revision in a layout's history.</summary>
/// <param name="Guid">The GUID of the revision record.</param>
/// <param name="Id">The revision ID.</param>
/// <param name="Transmittal">The transmittal set that issued this revision.</param>
public sealed record PlaIssuedLayoutRevision(Guid Guid, string Id, PlaLayoutTransmittal Transmittal);

/// <summary>A layout revision's transmittal set.</summary>
/// <param name="Guid">The GUID of the transmittal set.</param>
/// <param name="Id">The saved transmittal ID.</param>
/// <param name="Name">The saved transmittal name.</param>
/// <param name="IssuedAtUtc">The UTC issue time, or null while it is open.</param>
/// <param name="IssuedBy">The saved issuer, or null while it is open.</param>
public sealed record PlaLayoutTransmittal(Guid Guid, string Id, string Name, DateTimeOffset? IssuedAtUtc, string? IssuedBy)
{
    /// <summary>Whether the transmittal uses its own ID for the revisions of every included layout.</summary>
    public bool OverridesRevisionIds { get; init; }
}

/// <summary>The dimensions of a master layout.</summary>
/// <param name="Width">The paper width in metres.</param>
/// <param name="Height">The paper height in metres.</param>
public readonly record struct PlaLayoutPaperSize(double Width, double Height);
