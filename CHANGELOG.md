# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

### Added

- `PlaDocument.ReadPayload` decodes mode 2 (LZ) payloads. A payload may start with a 9-byte stream header holding the
  packed and/or unpacked size. The token stream produces the block's declared unpacked size and may reference the
  decompressed output of any preceding block of the file, so `ReadPayload` for a mode 2 block decodes the file from the
  start into one global window; the cost of the call grows with the size of the file. Preceding blocks that do not
  decode (false entries of the scan) are skipped and leave the window unchanged. A final match may omit the length byte
  (the match length is the remainder of the output). Some blocks declare an output buffer larger than the stream
  produces: the stream simply ends with the body, the produced bytes fill the head of the buffer and the tail stays
  zero — the global window holds the buffer space, so directory positions keep addressing it. A back-reference beyond
  the decoded stream throws `InvalidDataException`.

### Changed

- `PlaDocument.ReadPayload` no longer throws `NotSupportedException` for mode 2 blocks: all three storage modes
  (raw, zero mask, LZ) decode now.

### Added

- `PlaDocument.OpenStream` and `PlaStream`: the decoded global stream of the document (every block's payload decoded in
  block order) and the extents of it. A directory record's position and count name a byte extent of the stream — the
  object's data — so `PlaStream.ReadExtent(ObjectDirectoryRecord)` returns an object's bytes; extents can also be read
  by position and count. The stream is a snapshot that holds the whole decoded file in memory and the document may be
  disposed afterwards.
- The position and count of `ObjectDirectoryRecord` are now documented: a byte extent of the decoded global stream,
  confirmed against the object directories of real files.

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
