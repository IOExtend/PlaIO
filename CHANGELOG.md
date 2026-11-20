# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

### Added

- The `PlaIO.Scene` adapter project (namespace `PlaIO.Scene`): `PlaSceneReader.Read(path)` turns a `.pla` or `.pln`
  container into an `IOScene` `SceneDocument` — one model per file with the frozen `Key` derivation (the
  `PlaSceneOptions.ModelKey`, else the file name), no elements yet (the container reader does not decode element
  contents), and the `container-only` diagnostic. The project is `IsPackable=false` and is not published; it ships on a
  `PlaIO` release tag when the reader decodes elements and geometry. See `docs/scene.md`.

- `PlaDocument.ReadPayload` decodes mode 2 (LZ) payloads. A payload may start with a 9-byte stream header with a
  0x46 or 0x47 marker and matching packed and unpacked sizes. The token stream produces the block's declared unpacked size and may reference the
  decompressed output of any preceding block of the file, so `ReadPayload` for a mode 2 block decodes the file from the
  start into one global window; the cost of the call grows with the size of the file. Preceding blocks that do not
  decode (false entries of the scan) are skipped and leave the window unchanged. A final match may omit the length byte
  (the match length is the remainder of the output). Some blocks declare an output buffer larger than the stream
  produces: the stream simply ends with the body, the produced bytes fill the head of the buffer and the tail stays
  zero — the global window holds the buffer space, so directory positions keep addressing it. A back-reference beyond
  the decoded stream throws `InvalidDataException`.

- `PlaDocument.OpenStream` and `PlaStream`: the decoded global stream of the document (every block's payload decoded in
  block order) and the extents of it. A directory record's position and count name a byte extent of the stream — the
  object's data — so `PlaStream.ReadExtent(ObjectDirectoryRecord)` returns an object's bytes; extents can also be read
  by position and count. The stream is a snapshot that holds the whole decoded file in memory and the document may be
  disposed afterwards.
- The position and count of `ObjectDirectoryRecord` are now documented: a byte extent of the decoded global stream,
  confirmed against the object directories of real files.

- `PlaDocument.FindCompressedPools()` finds pools outside indexed block payloads. `CompressedPool.Decode()` reads
  stored and QuickLZ level 1 frames using the public format description and saved-file verification.

- `CompressedPool.ReadSubgroups()` reads BL-tagged records from decoded pools and reports incomplete payloads.

- `PlaReaderOptions` (with `Default`) caps what the readers decode into memory: `MaxGlobalStreamBytes` bounds the
  decoded global stream and the window a mode-2 `ReadPayload` builds (default: the most one in-memory stream can
  hold), `MaxDecodedPoolBytes` bounds one decoded pool (default: 512 MiB) and `MaxCoverBytes` bounds the `.bimx`
  cover image (default: 64 MiB, previously fixed). The options are immutable — build a new instance to change a
  limit — and come through added overloads (`Open(string, PlaReaderOptions?)`, `Open(Stream, bool,
  PlaReaderOptions?)`; the 0.1.0 entry-point signatures are unchanged). A read past a cap throws
  `InvalidDataException` naming it. The caps are reader limits, not facts of the format.

- `PlaStream.SkippedBlocks`: the indexed blocks that failed to decode to their declared size (false entries of the
  scan), in file order — the blocks behind `UnreliableFrom`.

- `CompressedPool.ReadSubgroups()` decodes mode-2 pool subgroups. A mode-2 payload is an LZ token stream like a
  mode-2 block payload; it may start with a 13-byte pool header (a u32, the 0x47 marker, the packed payload length
  and the unpacked size) or with the file block's 9-byte stream header. Its back-references reach into the pool
  stream — the reconstruction of every preceding pool of the document and of this pool before the record — so a read
  decodes the pools before the named one; reading the pools in file order advances one shared window, so every pool
  decodes once, and a preceding pool that does not decode is skipped. The window keeps only its 64 KiB tail (a
  back-reference distance is a u16), so the memory stays constant in the size of the file. A payload that does not
  decode is reported with empty data and `Partial`, as before. Verified against the saved-file corpus: every mode-2
  subgroup decodes. `FindCompressedPools` now caches its list after the first scan, so the pool objects stay
  identical between calls.

### Changed

- The core library follows the IOExtend reference layout (RvtIO, NwcIO): the entry points (`PlaDocument`,
  `BimxDocument`, `PlaFile`, `PlaReaderOptions`) stay in `PlaIO`, while the document types moved to the namespace
  `PlaIO.Model` (`FdbHeader`, `PlaBlock` + `PlaBlockMode`, `ObjectDirectory` + `ObjectDirectoryRecord`,
  `CompressedPool`, `PoolSubgroup`, `PlaStream`). The mode-2 global-window decode moved out of `PlaDocument` into
  internal machinery; the public surface and behavior are unchanged apart from the namespaces, and the entry-point
  signatures of the 0.1.0 surface keep their forms.

- The release workflow follows the IOExtend release order: the NuGet packages are pushed before the GitHub release is
  created (a refused first publication can be fixed and the job re-run, because `--skip-duplicate` makes the push
  repeatable), and a tag whose version contains a hyphen is published as a prerelease. The changelog section for the
  version is checked before anything is published.

- `PlaDocument.ReadPayload` no longer throws `NotSupportedException` for mode 2 blocks: all three storage modes
  (raw, zero mask, LZ) decode now.

## [0.1.0] - 2026-11-12

### Added

- `PlaFile.DetectKind` tells the container shared by `.pla` and `.pln` files (`ROF FDB `) and `.bimx` files (`BIMx`)
  apart by their first bytes.
- `PlaDocument` opens a `.pla` or `.pln` file or a seekable stream without loading the whole file. It exposes the length,
  the header (`FdbHeader`: layout version, format version, the raw field at offset 12 and the first 64 raw bytes) and the
  `BL` and `DY` blocks found by scanning from offset 32 (`PlaBlock`: offset, tag, id, kind, version, flags, size class,
  mode, GUID, packed and unpacked size, extra field, payload offset).
- `PlaDocument.ReadPayload` decodes payloads stored raw (mode 0) or with zero masks (mode 1). Mode 2 (LZ) is recognised
  and reported with `NotSupportedException`; a payload that does not decode to its declared size, or a cut file, is
  reported with `InvalidDataException`.
- `PlaDocument.FindDirectories` searches raw blocks for object directories of 30-byte records (`ObjectDirectoryRecord`:
  class number, position, count, object GUID) and reports how the records abut, overlap or repeat a position
  (`ObjectDirectory`). The meaning of position and count and the names of the classes are not known.
- `BimxDocument` opens a `.bimx` file or a seekable stream, measures the PNG cover image that follows the signature,
  reads it with `ReadCover` (up to 64 MiB) and reports where the rest of the content begins. That content is not
  interpreted.
- The `plaio` command line tool (package `PlaIO.Cli`) with the commands `info`, `blocks` and `directory`, JSON output of
  the directory records with `directory --out`, and the exit codes 0 (done), 1 (usage error) and 2 (input or output
  cannot be used).
- Targets .NET 8 and .NET 10, with no package dependencies. The library never writes to the console.
