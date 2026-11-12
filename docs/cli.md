# Command line and exit codes

The `plaio` tool reports the container structure of a file. It prints plain text to standard output, writes messages to
standard error and tells success from failure by its exit code. It uses only the public API of the library.

## Commands

| Command | Works on | Output |
| --- | --- | --- |
| `plaio info <file>` | `.pla`, `.pln`, `.bimx` | `key: value` lines: kind, length, header fields and block counts, or the cover bounds of a `.bimx` file. |
| `plaio blocks <file>` | `.pla`, `.pln` | A tab-separated header line, then one line per indexed block. |
| `plaio directory <file> [--out <file.json>]` | `.pla`, `.pln` | A tab-separated header line, then one line per object directory found. With `--out`, also every record as JSON. |
| `plaio --help`, `plaio -h` | | The usage text, on standard output. |
| `plaio --version` | | `plaio <version>`, for example `plaio 0.1.0`. |

The command comes first. `--out` is the only option and is accepted by `directory` only. The kind of the file is detected
from its first bytes, not from its extension.

## Exit codes

| Code | Name | Meaning |
| --- | --- | --- |
| 0 | done | The command finished and its output was written. |
| 1 | usage | The command line is wrong: no command, an unknown command or option, no file, an empty file name, an extra argument, or `--out` given twice or without a value. Two lines on standard error: the problem and `Run 'plaio --help' for usage.` |
| 2 | input or output error | The file cannot be used: it does not exist or cannot be opened, is not a recognised `.pla`, `.pln` or `.bimx` file, is cut short or malformed, is a `.bimx` file given to `blocks` or `directory`, or the `--out` file cannot be written. One line on standard error, prefixed with `plaio: `, no stack trace. |

Any other failure is a defect in the tool and ends the process with the runtime's unhandled exception code.

## Limits of the reader

The limits are fixed; the tool has no option to change them.

| Limit | Effect |
| --- | --- |
| At most 1048576 (2^20) blocks in one index | A file with more blocks is rejected with exit code 2. |
| Unpacked size of a block at most 2^28 bytes and at most 300 times its packed size plus 65536 | A block header outside these bounds is not taken as a block; the scan moves on. |
| Mode 2 (LZ) payloads | Recognised and listed, but not decoded. No command decodes payloads; `directory` reads raw blocks only. |
| Object directories | Searched in raw (mode 0) blocks of at least 1920 bytes, at most one per block. A directory needs at least 64 records whose extents mostly abut. The run that is reported is an estimate and can be shorter than the real directory. |

The meaning of `pos` and `count` in a directory record is not known, class numbers are not mapped to class names and the
contents of objects are not read, so the tool reports none of them beyond the raw numbers.

## Output format

### info

For a `.pla` or `.pln` file:

| Key | Value |
| --- | --- |
| `kind` | `FDB` (the container shared by `.pla` and `.pln`). |
| `length` | File length in bytes. |
| `layoutVersion`, `formatVersion` | The u16 header fields at offsets 8 and 10, as `0x` and four hex digits. |
| `field12` | The u32 header field at offset 12, as `0x` and eight hex digits; its meaning is not documented. |
| `headerBytes32to63` | The raw header bytes 32 to 63 in upper-case hex, or `-` when the file has none. |
| `blocks` | Number of indexed blocks. |
| `mode Raw`, `mode ZeroMask`, `mode Lz` | Number of blocks per payload mode (0, 1 and 2). |
| `tag BL`, `tag DY` | Number of blocks per tag. |
| `flag 0x10` | Number of blocks with flag `0x10` set; its meaning is not documented. |

For a `.bimx` file:

| Key | Value |
| --- | --- |
| `kind` | `BIMx`. |
| `length` | File length in bytes. |
| `coverOffset` | File offset of the PNG cover image, in hex. |
| `coverLength` | Length of the cover image in bytes, from the PNG signature through the `IEND` chunk. |
| `contentOffset` | File offset of the content after the cover, in hex. The content is not interpreted. |

### blocks

Columns, separated by tabs:

| Column | Value |
| --- | --- |
| `offset` | File offset of the block header, hex. |
| `tag` | `BL` or `DY`. |
| `id` | The raw u32 at +2, `0x` and eight hex digits. |
| `kind` | The raw u16 at +6, `0x` and four hex digits. |
| `ver` | The byte at +8, decimal. |
| `flags` | The raw u16 at +9, `0x` and four hex digits. |
| `class` | The size class, `flags & 0xF`, decimal. |
| `mode` | `Raw`, `ZeroMask` or `Lz`. |
| `packed`, `unpacked` | Stored and decoded payload size in bytes, decimal. |
| `extra` | The raw u32 stored when flag `0x400` is set, `0x` and eight hex digits, or `-`. |
| `payloadOffset` | File offset of the payload, hex. |
| `guid` | The GUID stored when flag `0x20` is set, or `-`. |

### directory

Columns, separated by tabs:

| Column | Value |
| --- | --- |
| `blockOffset` | File offset of the raw block that holds the directory, hex. |
| `recordOffset` | Offset of the first reported record within the payload, decimal. |
| `phase` | `recordOffset % 30`, the record alignment within the payload. |
| `records` | Number of records in the run. |
| `abutting` | Adjacent pairs of the records sorted by position where one extent ends where the next starts. |
| `overlapping` | Adjacent sorted pairs whose extents overlap. |
| `duplicates` | Adjacent sorted pairs with the same position. |

The header line is printed even when no directory is found.

### The `--out` JSON file

One indented UTF-8 JSON object. All numbers are decimal; the input file name is not written.

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

| Property | Meaning |
| --- | --- |
| `length` | File length in bytes. |
| `directories[]` | One object per directory, in the order of the summary lines. |
| `blockOffset`, `payloadOffset` | File offsets of the block header and of its payload. |
| `recordOffset`, `phase`, `recordCount` | As `recordOffset`, `phase` and `records` in the summary line. |
| `abuttingPairs`, `overlappingPairs`, `duplicatePositions` | As `abutting`, `overlapping` and `duplicates` in the summary line. |
| `records[]` | Every record of the run in file order: `class` (the class number, not mapped to a name), `pos` and `count` (raw, units not known) and `guid` (the object GUID). |

The example comes from the synthetic container of the README and is shortened to one record.

The summary lines are printed before the JSON is written. The JSON is first written to a new file next to the target,
named after it with a random suffix and `.tmp`, and then moved over the target. When the write fails the temporary file is
deleted, the target is left as it was and the exit code is 2. When the input cannot be read, no output file is created.

## Scripting

```sh
plaio directory project.pln --out directories.json
case $? in
  0) ;;                                    # done; directories.json holds the records
  1) echo "check the command line" ;;
  2) echo "the file cannot be read, or directories.json cannot be written" ;;
esac

# count the blocks whose payload is not decoded (mode 2)
plaio blocks project.pln | awk -F '\t' 'NR > 1 && $8 == "Lz"' | wc -l
```
