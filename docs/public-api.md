# Public API

The `PlaIO` package is the supported surface listed here; every public type is in the namespace `PlaIO`. Everything else
(the block scanner, the header parser, the payload decoder, the directory search, the PNG chunk walker) is `internal` in
`PlaIO.Internal`; it can change in any release and is visible only to the test project through `InternalsVisibleTo`.

## File kind

| Member | Use |
|---|---|
| `PlaFileKind` | `Unknown` (0), `Fdb` (1): bytes 0..7 are `ROF FDB `, the container that `.pla` and `.pln` files share (the two are not told apart), `Bimx` (2): bytes 0..3 are `BIMx`. |
| `PlaFile.SignatureLength` | `8`, the number of leading bytes needed to recognise every kind. |
| `PlaFile.DetectKind(ReadOnlySpan<byte> head)` | The kind of a file whose first bytes are `head`. |
| `PlaFile.DetectKind(string path)` | Reads at most 8 bytes of the file; I/O errors propagate. |

## FDB container (`.pla`, `.pln`)

| Member | Use |
|---|---|
| `PlaDocument.Open(string path)`, `PlaDocument.Open(Stream stream, bool leaveOpen = false)` | Opens a container and builds its block index. The stream must be readable and seekable (`ArgumentException` otherwise); data that is not an FDB container or is shorter than 32 bytes throws `InvalidDataException`. The document owns the stream unless `leaveOpen` is true; when `Open` throws, the stream stays with the caller. |
| `PlaDocument.Length` | Length of the file in bytes. |
| `PlaDocument.Header` | The `FdbHeader`. |
| `PlaDocument.Blocks` | The block index in file order, found by scanning from offset 32. No on-disk block map is known, so completeness is not claimed. The scan can take a byte run for a block header, so the index may contain false entries. In real files a small share of mode 1 blocks does not decode to its declared size, and `ReadPayload` throws `InvalidDataException` for them. |
| `PlaDocument.ReadPayload(PlaBlock block)` | The decoded payload of a block of this document. Decodes mode 0 and mode 1. Throws `NotSupportedException` for mode 2 (LZ), `InvalidDataException` when a mode 1 payload does not decode to the declared size or the file is cut short, and `ArgumentException` when the block is not from this document. |
| `PlaDocument.FindDirectories()` | Searches every raw (mode 0) block large enough for 64 records of 30 bytes for an object directory, at most one per block. Recomputed on each call; mode 1 payloads are not searched. |
| `PlaDocument.Dispose()` | Releases the stream unless the document was opened with `leaveOpen`. |
| `FdbHeader` | `Size` (64), `LayoutVersion` (u16 at offset 8), `FormatVersion` (u16 at offset 10), `Field12` (u32 at offset 12; meaning not documented), `Raw` (the first min(64, file length) bytes; bytes 32..63 overlap the first block). |
| `PlaBlockMode` | `Raw` (0), `ZeroMask` (1, flag 0x100), `Lz` (2, flag 0x200; recognised, not decoded). |
| `PlaBlock` | One indexed block: `Offset`, `Tag` (`BL` or `DY`), `Id` and `Kind` (raw), `Version` (0 or 1), `Flags` (raw), `SizeClass` (`Flags & 0xF`), `Mode`, `Guid` (present when flag 0x20 is set), `PackedSize`, `UnpackedSize`, `Extra` (present when flag 0x400 is set; raw), `PayloadOffset`, `HeaderLength`, `End`. |
| `ObjectDirectory` | A run of directory records inside a raw block: `Block`, `RecordOffset` (payload offset of the first record), `Phase` (`RecordOffset % 30`), `Records`, `AbuttingPairs`, `OverlappingPairs`, `DuplicatePositions`. The run is an estimate and can be shorter than the real directory: it stops after more than 8 consecutive records without an abutting neighbour. |
| `ObjectDirectoryRecord` | The 30-byte record `[u16 class][u64 pos][u32 count][GUID16]` as `ClassNumber`, `Position`, `Count`, `ObjectId`. The units of position and count are not documented, and the class number is not mapped to a class name. |

## BIMx file (`.bimx`)

| Member | Use |
|---|---|
| `BimxDocument.Open(string path)`, `BimxDocument.Open(Stream stream, bool leaveOpen = false)` | Opens a `.bimx` file and measures its cover image without reading it. Same exceptions as `PlaDocument.Open`; a cover cut short also throws `InvalidDataException`. |
| `BimxDocument.Length` | Length of the file in bytes. |
| `BimxDocument.CoverOffset` | Offset of the PNG cover image: always 4, right after the signature. |
| `BimxDocument.CoverLength` | Length of the cover, from the PNG signature through the IEND chunk and its CRC. |
| `BimxDocument.ContentOffset` | `CoverOffset + CoverLength`. The content after the cover is not interpreted. |
| `BimxDocument.ReadCover()` | The PNG cover image. Throws `InvalidDataException` when the cover is larger than 64 MiB. |
| `BimxDocument.Dispose()` | Releases the stream unless the document was opened with `leaveOpen`. |

## Resources and threads

`PlaDocument` and `BimxDocument` never load the whole file into memory: the block index is built by scanning through the
stream once, at `Open`, and payloads and the cover are read on request. A document is not thread-safe; use one per thread.
After `Dispose`, `ReadPayload`, `FindDirectories` and `ReadCover` throw `ObjectDisposedException`.

## Diagnostics

The library never writes to the console and has no diagnostic writer. Every failure is reported by an exception. The
`plaio` command line tool turns those exceptions into one-line messages on standard error and an exit code; see
[Command line and exit codes](cli.md).
