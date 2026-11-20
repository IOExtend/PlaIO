# Public API

The `PlaIO` package is the supported surface listed here. The entry points (`PlaDocument`, `BimxDocument`, `PlaFile`,
`PlaReaderOptions`) are in the namespace `PlaIO`; the types that describe the opened document (`PlaBlock`,
`ObjectDirectory`, `CompressedPool`, `PoolSubgroup`, `PlaStream`, `FdbHeader`) are in `PlaIO.Model`. Everything else
(the block scanner, the header parser, the payload decoders, the directory search, the PNG chunk walker) is `internal`
in `PlaIO.Internal`; it can change in any release and is visible only to the test project through `InternalsVisibleTo`.

## File kind

| Member | Use |
|---|---|
| `PlaFileKind` | `Unknown` (0), `Fdb` (1): bytes 0..7 are `ROF FDB `, the container that `.pla` and `.pln` files share (the two are not told apart), `Bimx` (2): bytes 0..3 are `BIMx`. |
| `PlaFile.SignatureLength` | `8`, the number of leading bytes needed to recognise every kind. |
| `PlaFile.DetectKind(ReadOnlySpan<byte> head)` | The kind of a file whose first bytes are `head`. |
| `PlaFile.DetectKind(string path)` | Reads at most 8 bytes of the file; I/O errors propagate. |

## Reader options

| Member | Use |
|---|---|
| `PlaReaderOptions.Default` | The options a reader uses when the caller passes none. |
| `PlaReaderOptions.MaxGlobalStreamBytes` | Cap on the decoded global stream (`OpenStream`) and on the window a mode-2 `ReadPayload` builds (default: the most one in-memory stream can hold). A block whose declared size grows the window past the cap throws `InvalidDataException` before it is decoded. |
| `PlaReaderOptions.MaxDecodedPoolBytes` | Cap on the decoded size of one compressed pool (default: 512 MiB). A pool that declares more makes `Decode` throw `InvalidDataException`. |
| `PlaReaderOptions.MaxCoverBytes` | Cap on the `.bimx` cover image read into memory (default: 64 MiB). A larger cover makes `ReadCover` throw `InvalidDataException`. |

The caps are reader limits, not facts of the format. `PlaDocument.Open`, `BimxDocument.Open` and their stream
overloads take the options as an optional last parameter.

## FDB container (`.pla`, `.pln`)

| Member | Use |
|---|---|
| `PlaDocument.Open(string path, PlaReaderOptions? options = null)`, `PlaDocument.Open(Stream stream, bool leaveOpen = false, PlaReaderOptions? options = null)` | Opens a container and builds its block index. The stream must be readable and seekable (`ArgumentException` otherwise); data that is not an FDB container or is shorter than 32 bytes throws `InvalidDataException`. The document owns the stream unless `leaveOpen` is true; when `Open` throws, the stream stays with the caller. |
| `PlaDocument.Length` | Length of the file in bytes. |
| `PlaDocument.Header` | The `FdbHeader`. |
| `PlaDocument.Blocks` | The block index in file order, found by scanning from offset 32. No on-disk block map is known, so completeness is not claimed. The scan can take a byte run for a block header, so the index may contain false entries. In real files a small share of mode 1 blocks does not decode to its declared size, and `ReadPayload` throws `InvalidDataException` for them. |
| `PlaDocument.ReadPayload(PlaBlock block)` | The decoded payload of a block of this document. Decodes all three storage modes: raw (mode 0), zero masks (mode 1) and the LZ token stream (mode 2). A mode 2 payload may start with a 9-byte stream header with a 0x46 or 0x47 marker and matching packed and unpacked sizes. The token stream produces the block's declared unpacked size — some blocks declare a buffer larger than the stream produces, the tail of the buffer stays zero — and may reference the output of any preceding block, so the payload is produced by decoding every preceding block of the file in order into one global window; the cost of the call grows with the size of the file. Preceding blocks that do not decode (false entries of the scan) are skipped and leave the window unchanged. Throws `InvalidDataException` when a back-reference reaches beyond the decoded stream or the file is cut short, and `ArgumentException` when the block is not from this document. |
| `PlaDocument.FindDirectories()` | Searches every raw (mode 0) block large enough for 64 records of 30 bytes for an object directory, at most one per block. Recomputed on each call; mode 1 payloads are not searched. |
| `PlaDocument.OpenStream()` | The document's decoded global stream (`PlaStream`): every block's payload decoded in block order. Blocks that fail to decode are skipped and mark the remaining offsets as unreliable. The stream holds the decoded bytes in memory and can outlive the document. |
| `PlaDocument.Dispose()` | Releases the stream unless the document was opened with `leaveOpen`. |
| `FdbHeader` | `Size` (64), `LayoutVersion` (u16 at offset 8), `FormatVersion` (u16 at offset 10), `Field12` (u32 at offset 12; meaning not documented), `Raw` (the first min(64, file length) bytes; bytes 32..63 overlap the first block). |
| `PlaBlockMode` | `Raw` (0), `ZeroMask` (1, flag 0x100), `Lz` (2, flag 0x200): an LZ token stream decoded against the global window. |
| `PlaBlock` | One indexed block: `Offset`, `Tag` (`BL` or `DY`), `Id` and `Kind` (raw), `Version` (0 or 1), `Flags` (raw), `SizeClass` (`Flags & 0xF`), `Mode`, `Guid` (present when flag 0x20 is set), `PackedSize`, `UnpackedSize`, `Extra` (present when flag 0x400 is set; raw), `PayloadOffset`, `HeaderLength`, `End`. |
| `ObjectDirectory` | A run of directory records inside a raw block: `Block`, `RecordOffset` (payload offset of the first record), `Phase` (`RecordOffset % 30`), `Records`, `AbuttingPairs`, `OverlappingPairs`, `DuplicatePositions`. The run is an estimate and can be shorter than the real directory: it stops after more than 8 consecutive records without an abutting neighbour. |
| `ObjectDirectoryRecord` | The 30-byte record `[u16 class][u64 pos][u32 count][GUID16]` as `ClassNumber`, `Position`, `Count`, `ObjectId`. The position and count name a byte extent of the decoded global stream — the object's data (see `PlaDocument.OpenStream`). The class number is not mapped to a class name. |
| `PlaStream.Length` | Length of the decoded stream in bytes, including the bytes beyond the unreliable boundary. |
| `PlaStream.UnreliableFrom` | The offset of the first indexed block that failed to decode, or null when every indexed block decoded. |
| `PlaStream.SkippedBlocks` | The indexed blocks that failed to decode to their declared size (false entries of the scan), in file order. |
| `PlaStream.ReadExtent(long position, long count)` | The bytes of one extent. Throws `ArgumentOutOfRangeException` for negative or out-of-bounds extents and `InvalidDataException` for extents that reach the unreliable range. |
| `PlaStream.ReadExtent(ObjectDirectoryRecord record)` | The bytes addressed by a directory record. Throws `InvalidDataException` if the extent exceeds the stream or reaches the unreliable range. |

## Compressed pools

Pool framing was verified using saved container bytes. A 16-byte marker is followed by the decoded total and frame count
as little-endian u64 values. Each frame has a u32 size followed by a standalone QuickLZ level 1 frame. The decoder
implements the [public QuickLZ 1.5.0 format](https://github.com/ReSpeak/quicklz/blob/d905415a88aed6dae685bae500025ccc81de3822/Format.md);
independent decoding of saved-file frames verified the correspondence. PlaIO has no runtime dependency on QuickLZ.

| Member | Use |
|---|---|
| `PlaDocument.FindCompressedPools()` | Finds pool markers outside indexed block payloads and validates frame headers and sizes. Computed on each call. |
| `CompressedPool.Offset` and `End` | File offsets of the pool marker and of the end of the pool. |
| `CompressedPool.TotalDecompressed` and `BlockCount` | Decoded byte count and number of frames in the pool. |
| `CompressedPool.Decode()` | Decoded frame payloads in order. Throws `InvalidDataException` on invalid framing or data (a pool past `PlaReaderOptions.MaxDecodedPoolBytes` included) and `ObjectDisposedException` after disposing the document. |

### Pool subgroups

The subgroup header uses the FDB size field widths and may carry a GUID. It does not contain the file-level extra field.
Offsets are relative to the decoded pool. The scanner retries an unmatched BL candidate at the next byte.

| Member | Use |
|---|---|
| `CompressedPool.ReadSubgroups()` | Decodes the pool and returns the recognised BL-tagged records in pool order. |
| `PoolSubgroup.Offset` | Start of the subgroup header in the decoded pool. |
| `PoolSubgroup.Kind`, `Version`, `Flags` and `ObjectId` | Raw header metadata and the optional GUID. |
| `PoolSubgroup.Mode` and `Data` | Raw or zero-mask payload bytes. Mode 2 is reported with empty data. |
| `PoolSubgroup.Partial` | True for unavailable mode 2 data or an incomplete zero-mask literal. A zero-mask stream ending at a group boundary can leave valid zero padding. |

## BIMx file (`.bimx`)

| Member | Use |
|---|---|
| `BimxDocument.Open(string path, PlaReaderOptions? options = null)`, `BimxDocument.Open(Stream stream, bool leaveOpen = false, PlaReaderOptions? options = null)` | Opens a `.bimx` file and measures its cover image without reading it. Same exceptions as `PlaDocument.Open`; a cover cut short also throws `InvalidDataException`. |
| `BimxDocument.Length` | Length of the file in bytes. |
| `BimxDocument.CoverOffset` | Offset of the PNG cover image: always 4, right after the signature. |
| `BimxDocument.CoverLength` | Length of the cover, from the PNG signature through the IEND chunk and its CRC. |
| `BimxDocument.ContentOffset` | `CoverOffset + CoverLength`. The content after the cover is not interpreted. |
| `BimxDocument.ReadCover()` | The PNG cover image. Throws `InvalidDataException` when the cover is larger than `PlaReaderOptions.MaxCoverBytes` (64 MiB by default). |
| `BimxDocument.Dispose()` | Releases the stream unless the document was opened with `leaveOpen`. |

## Resources and threads

`PlaDocument` and `BimxDocument` never load the whole file into memory: the block index is built by scanning through the
stream once, at `Open`, and payloads and the cover are read on request. The exception is `OpenStream`: its decoded global
stream holds the whole decoded file in memory by design, bounded by `PlaReaderOptions.MaxGlobalStreamBytes`. A document
is not thread-safe; use one per thread.
After `Dispose`, `ReadPayload`, `FindDirectories`, `OpenStream` and `ReadCover` throw `ObjectDisposedException`.

## Diagnostics

The library never writes to the console and has no diagnostic writer. Every failure is reported by an exception. The
`plaio` command line tool turns those exceptions into one-line messages on standard error and an exit code; see
[Command line and exit codes](cli.md).
