namespace PlaIO.Model;

/// <summary>A catalog of the supported embedded GSM library parts in an FDB archive.</summary>
/// <param name="Parts">The verified parts in file order.</param>
/// <param name="UnreadableAssetOffsets">Physical offsets of standalone asset blocks or asset pools with unreadable data.</param>
public sealed record PlaEmbeddedLibraryCatalog(
    IReadOnlyList<PlaEmbeddedLibraryPart> Parts, IReadOnlyList<long> UnreadableAssetOffsets);

/// <summary>A GSM library part preserved in the archive.</summary>
/// <param name="AssetGuid">The archive record GUID.</param>
/// <param name="Name">The saved file name.</param>
/// <param name="LibraryPartGuid">The main GUID in the GSM header.</param>
/// <param name="RevisionGuid">The revision GUID in the archived GSM header.</param>
/// <param name="Data">The original GSM bytes, including its header and sections.</param>
public sealed record PlaEmbeddedLibraryPart(
    Guid AssetGuid, string Name, Guid LibraryPartGuid, Guid RevisionGuid, ReadOnlyMemory<byte> Data);

/// <summary>The identity stored in a placed element's library part descriptor.</summary>
/// <param name="LibraryPartGuid">The main library part GUID.</param>
/// <param name="RevisionGuid">The revision GUID referenced by the element.</param>
public readonly record struct PlaLibraryPartReference(Guid LibraryPartGuid, Guid RevisionGuid);
