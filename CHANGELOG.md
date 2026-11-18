# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

### Added

- `CompressedPool.ReadSubgroups` and `PoolSubgroup`: the object records the object database keeps inside a pool —
  BL-tagged subgroups with the object's class id (`Kind`), `Version`, `Flags`, `ObjectId` (a GUID when the header
  carries one), the payload's storage (`Mode`: raw or zero mask) and the decoded payload (`Data`). The headers follow
  the file's block layout (a u16 packed size for size classes 0..6, a u32 for 7..14); a zero-mask stream that ends
  before its declared unpacked size leaves the tail of the buffer zero. Mode 2 (LZ) subgroups have not been met inside
  the pools examined so far and are reported with an empty payload. The records are found by scanning for the BL tag
  and accepting the ones whose header parses; a rejected candidate is retried one byte on. Observed payloads store text
  as a u32 length followed by UTF-16 little-endian data and numbers as little-endian doubles and integers.

- `PlaDocument.FindCompressedPools` and `CompressedPool`: the compressed storage pools of the object database that the
  file holds in the gaps between blocks, each starting with a 16-byte compression sign, a declared decompressed total
  and its blocks (each memcpy or LZ77-triple coded, at most one megabyte and change of packed payload per block). The
  object database keeps its groups, journals and library data in these pools; a pool is not a block and does not take
  part in the block index. `FindCompressedPools` finds every pool whose header and block headers parse and whose
  declared sizes tile the total (computed on each call; the scan reads the file in windows and skips the indexed block
  payloads, so a sign inside them is never returned as a pool); `Decode` produces the decoded bytes on request. The
  layout and the LZ77-triple codec follow the format notes of this repository and are confirmed against the sample
  files.

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
