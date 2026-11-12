---
title: PlaIO
---

# PlaIO

PlaIO is a .NET library and command line tool that reads the container structure of Archicad project files (`.pla`, `.pln`) and the cover image of BIMx files (`.bimx`) directly from disk, without Archicad.

- Detects the file kind from the first bytes of a file.
- Reads the header of the FDB container that `.pla` and `.pln` files share, and builds an index of its blocks.
- Decodes block payloads stored raw (mode 0) or zero-masked (mode 1). Mode 2 (LZ) payloads are recognised but not decoded, and a small share of mode 1 blocks in real files does not decode to its declared size.
- Searches raw blocks for object directories and returns their records as stored (class number, position, count, GUID).
- Reads the PNG cover image of a `.bimx` file.

PlaIO reports the structure of a file; it does not read Archicad elements, class names or object contents, and it does not write files. The library targets .NET 8 and .NET 10, uses managed code only, has no package dependencies and supports Windows, Linux and macOS.

## Where to go next

- [Getting started](getting-started.md): install the tool or the package and read a first file.
- [Public API](public-api.md): the supported surface of the library.
- [Command line and exit codes](cli.md): the `plaio` commands, their output and the exit codes.
- [API reference](api/PlaIO.yml): every public type, generated from the XML documentation.
- [Source on GitHub](https://github.com/IOExtend/PlaIO) and [DeepWiki](https://deepwiki.com/IOExtend/PlaIO) for an automatically generated overview of the repository.

PlaIO is not affiliated with, endorsed by or sponsored by Graphisoft. Graphisoft, Archicad and BIMx are trademarks of Graphisoft SE.
