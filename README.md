<p align="center">
  <img src="docs/images/plaio-banner.png" alt="PlaIO" width="640">
</p>

PlaIO is a pure managed .NET library and command line tool that reads the container of Archicad project files (`.pla`,
`.pln`) and the cover image of BIMx files (`.bimx`) directly from disk, without Archicad and without native dependencies.

## What it does

- Detects the file kind from the first bytes: the container shared by `.pla` and `.pln`, or `.bimx`.
- Reads the container header and exposes its fields as raw values where their meaning is not documented.
- Indexes the `BL` and `DY` blocks of the container in one forward scan, without loading the whole file.
- Decodes block payloads stored raw (mode 0) or with zero masks (mode 1). Mode 2 (LZ) payloads are recognised but not
  decoded, and some mode 1 blocks in real files do not decode (see [Limitations](#limitations)).
- Searches raw blocks for object directories (30-byte records of class number, position, count and object GUID).
- Reads the PNG cover image at the start of a `.bimx` file and reports where the rest of the content begins.

## Install

The packages are published on nuget.org, not on GitHub Packages, so the Packages box of this repository stays empty.

[![PlaIO on NuGet](https://img.shields.io/nuget/v/PlaIO?label=PlaIO)](https://www.nuget.org/packages/PlaIO) [![PlaIO.Cli on NuGet](https://img.shields.io/nuget/v/PlaIO.Cli?label=PlaIO.Cli)](https://www.nuget.org/packages/PlaIO.Cli)

Install the command line tool:

```sh
dotnet tool install --global PlaIO.Cli
```

Add the library to a .NET project:

```sh
dotnet add package PlaIO
```

The library targets .NET 8 and .NET 10. It uses managed code without native dependencies, package dependencies or
Windows-only APIs and supports Windows, Linux and macOS. Tests currently run on Windows.

## Command line

```sh
plaio info <file>                            # kind, length, header fields, block counts (or the .bimx cover bounds)
plaio blocks <file>                          # one tab-separated line per indexed block (.pla, .pln)
plaio directory <file> [--out <file.json>]   # object directories found in raw blocks (.pla, .pln)
plaio --help
plaio --version
```

`info` prints `key: value` lines. For a `.pla` or `.pln` file these are the kind, the length, the header fields
`layoutVersion`, `formatVersion` and `field12` in hex, the raw header bytes 32 to 63 in hex, the number of blocks, the
number of blocks per mode and per tag, and the number of blocks with flag `0x10`. For a `.bimx` file they are the kind,
the length, the cover offset, the cover length and the content offset.

`blocks` prints a tab-separated header line and one line per block with the columns `offset tag id kind ver flags class
mode packed unpacked extra payloadOffset guid`. Offsets, ids, kinds and flags are hex; `extra` and `guid` are `-` when the
block has none.

`directory` prints a tab-separated header line and one line per directory with the columns `blockOffset recordOffset
phase records abutting overlapping duplicates`. With `--out` it also writes every record as JSON. The JSON file is first
written under a temporary name next to the target and then moved over it, so a failed write leaves no truncated file. The
input file name is not written into the JSON.

`blocks` and `directory` work on `.pla` and `.pln` files only. Messages go to standard error, prefixed with `plaio: `.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Done. |
| 1 | The command line is wrong: no command, an unknown command or option, a missing, empty or extra argument, or `--out` given twice or without a value. |
| 2 | The input or output file cannot be used: it is missing, unreadable, not a recognised `.pla`, `.pln` or `.bimx` file, malformed, of the wrong kind for the command, or the output cannot be written. One line on standard error, no stack trace. |

The commands, the JSON shape and a scripting example are described in [the command line page](docs/cli.md).

## Library

Open a project file, list its blocks, read the payloads that can be decoded, find object directories, and save the cover
of a `.bimx` file:

```csharp
using System;
using System.IO;
using PlaIO;

using (var document = PlaDocument.Open("project.pln"))
{
    Console.WriteLine($"{document.Length} bytes, {document.Blocks.Count} blocks");

    foreach (var block in document.Blocks)
    {
        if (block.Mode == PlaBlockMode.Lz) continue; // recognised, not decoded
        try
        {
            byte[] payload = document.ReadPayload(block);
            Console.WriteLine($"{block.Tag} at 0x{block.Offset:x}: {payload.Length} bytes");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine($"{block.Tag} at 0x{block.Offset:x}: does not decode to its declared size");
        }
    }

    foreach (var directory in document.FindDirectories())
        Console.WriteLine($"directory in block 0x{directory.Block.Offset:x}: {directory.Records.Count} records");
}

using (var bimx = BimxDocument.Open("model.bimx"))
    File.WriteAllBytes("cover.png", bimx.ReadCover());
```

`PlaFile.DetectKind` tells the kinds apart before a file is opened. `ReadPayload` throws `NotSupportedException` for a
mode 2 (LZ) block and `InvalidDataException` for a payload that does not decode to its declared size. The library never
writes to the console and has no diagnostic output; problems are reported as exceptions.

See the [public API](docs/public-api.md) for the supported surface and [dependencies](docs/dependencies.md) for package
details.

## Documentation

The documentation site with the API reference is at <https://ioextend.github.io/PlaIO/>. [![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/IOExtend/PlaIO) offers an automatically generated overview of the repository.

## Examples

The output below comes from a small synthetic container built for this example, not from an Archicad project.

```text
$ plaio info synthetic.pla
kind: FDB
length: 2550
layoutVersion: 0x0010
formatVersion: 0x0002
field12: 0x01010000
headerBytes32to63: 424C04030201EAFD008180140000000000000000000000000000000000000000
blocks: 4
mode Raw: 2
mode ZeroMask: 1
mode Lz: 1
tag BL: 3
tag DY: 1
flag 0x10: 1
```

```text
$ plaio blocks synthetic.pla
offset  tag  id          kind    ver  flags   class  mode      packed  unpacked  extra       payloadOffset  guid
0x20    BL   0x01020304  0xfdea  0    0x8081  1      Raw       20      20        -           0x2d           -
0x41    BL   0x01020304  0xfdea  0    0x85a0  0      ZeroMask  6       16        0x00000002  0x66           ec6aea00-1f59-5596-4c55-4c3524620902
0x6c    DY   0x01020304  0xfdea  0    0x8290  0      Lz        10      30        -           0x7d           -
0x87    BL   0x01020304  0xfdea  0    0x8084  4      Raw       2402    2402      -           0x94           -
```

The real output separates the columns with tabs; they are aligned here for reading.

```text
$ plaio directory synthetic.pla --out directories.json
blockOffset  recordOffset  phase  records  abutting  overlapping  duplicates
0x87         2             2      80       79        0            0
```

```json
{
  "length": 2550,
  "directories": [
    {
      "blockOffset": 135,
      "payloadOffset": 148,
      "recordOffset": 2,
      "phase": 2,
      "recordCount": 80,
      "abuttingPairs": 79,
      "overlappingPairs": 0,
      "duplicatePositions": 0,
      "records": [
        { "class": 152, "pos": 5043, "count": 168, "guid": "d0d18fb0-81da-fbbb-2bd4-afc18e1e5540" }
      ]
    }
  ]
}
```

The JSON is shortened to one record and written on fewer lines than the tool writes it.

```text
$ plaio info synthetic.bimx
kind: BIMx
length: 69
coverOffset: 0x4
coverLength: 62
contentOffset: 0x42
```

## Supported files

- `.pla` and `.pln` files. Both use the same container, which starts with `ROF FDB `; PlaIO does not tell them apart and
  reports both as `FDB`.
- `.bimx` files, which start with `BIMx` followed by a PNG cover image. Only the cover is read; the content after it is
  not interpreted.

The reader was developed against files saved by Archicad 27, 29 and 30 and against `.bimx` files. No Archicad files are
included in the repository. Files from other versions are not tested.

## Limitations

- Version 0.1.0 is a preview. The API can change before 1.0.
- Mode 2 (LZ) payloads are recognised but not decoded; `ReadPayload` throws `NotSupportedException` for them.
- The meaning of the `pos` and `count` fields of a directory record is not known, so the addressing of objects is not
  supported. Class numbers are not mapped to class names, and the contents of objects (elements, parameters, geometry)
  are not read.
- Several header and block fields are exposed as raw values because their meaning is not documented.
- The block index is built by scanning for block headers from offset 32. It is not checked against an index in the file,
  so completeness is not claimed. The scan can also take a byte run for a block header, so the index may contain false
  entries.
- In real files a small share of mode 1 blocks does not decode to its declared size; `ReadPayload` throws
  `InvalidDataException` for them.
- Object directories are searched in raw (mode 0) blocks only, at most one per block. The record run that is found is an
  estimate and can be shorter than the real directory.
- Of a `.bimx` file only the cover image is read.
- There is no support for writing or modifying Archicad or BIMx files.

## Build from source and tests

Use the .NET SDK selected by `global.json`, which specifies 10.0.100 with `latestFeature` roll-forward.

From the repository root:

```sh
dotnet build PlaIO.slnx -c Release
dotnet test PlaIO.slnx -c Release
```

The tests build their inputs synthetically. Tests that need private inputs are reported as skipped, with the reason,
when the inputs are not available:

- `PLAIO_TEST_FILES` names a folder of `.pla`, `.pln` and `.bimx` files, directly in it or one subfolder down. After it,
  every `test_files` folder in an ancestor folder of the test binary is searched, the nearest first; `test_files` is
  ignored by git.
- `PLAIO_TEST_VECTORS` names a folder of test vectors: a `manifest.json` with inputs and expected outputs.

The repository includes no Archicad files. See [private test fixtures](docs/test-fixtures.md) for details.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Release changes are recorded in
[CHANGELOG.md](CHANGELOG.md).

## Security

Report vulnerabilities through GitHub private vulnerability reporting in the repository's Security tab. Do not open
public issues for security vulnerabilities. See [SECURITY.md](SECURITY.md).

## License and trademarks

PlaIO is licensed under the [MIT License](LICENSE). Copyright holder: mahach666.

Graphisoft, Archicad and BIMx are trademarks of Graphisoft SE. PlaIO is not affiliated with, endorsed by or sponsored
by Graphisoft.

The file format is read by analysing files for interoperability. No Graphisoft code or binaries are included.
