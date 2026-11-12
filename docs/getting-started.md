---
title: Getting started
---

# Getting started

## Command line tool

```sh
dotnet tool install --global PlaIO.Cli
plaio info project.pln
```

`info` prints the kind and length of the file. For a `.pla` or `.pln` file it also prints the header fields and the block counts per mode and per tag; for a `.bimx` file it prints where the cover image lies.

```sh
plaio blocks project.pln
plaio directory project.pln --out directories.json
```

`blocks` lists every indexed block as tab-separated lines. `directory` prints one summary line per object directory found, and with `--out` writes all records as JSON. Both work on `.pla` and `.pln` files only.

Run `plaio --help` for the usage. The output columns, the JSON shape and the exit codes are described in [Command line and exit codes](cli.md).

## Library

```sh
dotnet add package PlaIO
```

```csharp
using System;
using System.IO;
using PlaIO;

using (var doc = PlaDocument.Open("project.pln"))
{
    Console.WriteLine($"format version 0x{doc.Header.FormatVersion:x4}, {doc.Blocks.Count} blocks");

    foreach (PlaBlock block in doc.Blocks)
    {
        if (block.Mode == PlaBlockMode.Lz) continue; // recognised, but ReadPayload throws NotSupportedException
        try
        {
            byte[] payload = doc.ReadPayload(block);
        }
        catch (InvalidDataException)
        {
            // A mode 1 block that does not decode to its declared size.
        }
    }

    foreach (ObjectDirectory directory in doc.FindDirectories())
    {
        Console.WriteLine($"directory in the block at 0x{directory.Block.Offset:x}: {directory.Records.Count} records");
    }
}

using (var bimx = BimxDocument.Open("model.bimx"))
{
    File.WriteAllBytes("cover.png", bimx.ReadCover());
}
```

Use `PlaFile.DetectKind` to tell an FDB container (`.pla`, `.pln`) from a `.bimx` file before opening it.

The library never writes to the console and has no diagnostic writer. A file it cannot read is reported by an exception: `InvalidDataException` for data that is not the expected kind or is cut short, `NotSupportedException` for a mode 2 payload, and the usual I/O exceptions for a file that cannot be opened.

The supported surface is listed in [Public API](public-api.md); every type is in the [API reference](api/PlaIO.yml).

## Status

PlaIO 0.1.0 is a preview and the API can change before 1.0. It does not decode mode 2 (LZ) payloads, does not interpret the position and count of directory records, does not map class numbers to class names and does not read object contents. In real files a small share of mode 1 blocks does not decode to its declared size, and `ReadPayload` throws `InvalidDataException` for them. The block index is found by scanning and may contain false entries.
