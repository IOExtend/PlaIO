# Private test fixtures

Most tests build their input in code (`Synthetic.cs` in the test project) and run everywhere. A few tests check the reader
against real Archicad files and against vectors cut from them. Those inputs cannot be published, so they are not in the
repository. The tests that need them find them through two helpers, `TestFiles` and `TestVectors`, and are reported as
**skipped with a reason** when the input is missing. They never pass silently and never fail for lack of a file.

## Sample files: `PLAIO_TEST_FILES`

`TestFiles` looks for sample files in:

1. The folder named by the environment variable `PLAIO_TEST_FILES`.
2. Then every folder named `test_files` in an ancestor folder of the test binary (the repository root among them),
   nearest first. `test_files/` is listed in `.gitignore`.

A file may lie directly in the folder or in one level of subfolders. Files are found by extension, never by name: every
`.pla` and `.pln` file is opened as an FDB container and every `.bimx` file as a BIMx file. A file name seen twice is taken
once (the first folder wins). No test asserts a count or value tied to a particular file; the tests check properties that
every file of the kind must have.

`SampleTests` (9 facts) uses them:

| Test | Needs |
|---|---|
| `EverySampleIsDetectedAsTheKindOfItsExtension` | any `.pla`, `.pln` or `.bimx` file |
| `EveryFdbSampleHasTheConfirmedHeader` | `.pla` or `.pln` files |
| `EveryFdbIndexIsOrderedAndInsideTheFile` | `.pla` or `.pln` files |
| `FdbSamplesHaveRawAndZeroMaskBlocksAndSomeHaveLzBlocks` | `.pla` or `.pln` files |
| `EveryRawBlockDecodesToItsDeclaredSize` | `.pla` or `.pln` files |
| `NearlyEveryZeroMaskBlockDecodesToItsDeclaredSize` | `.pla` or `.pln` files |
| `ReadingAnLzBlockThrowsNotSupported` | `.pla` or `.pln` files |
| `EveryFdbSampleHasADirectoryOfMostlyAbuttingExtents` | `.pla` or `.pln` files |
| `EveryBimxCoverIsACompletePng` | `.bimx` files |

The FDB and BIMx lists skip independently: a folder with only `.bimx` files runs the first and the last test and skips the
seven FDB tests; a folder with only `.pla` and `.pln` files skips the last test.

## Vectors: `PLAIO_TEST_VECTORS`

`TestVectors` reads the folder named by the environment variable `PLAIO_TEST_VECTORS`; there is no default location. The
folder holds small inputs and expected outputs cut from sample files and a `manifest.json` that lists them. Each manifest
entry names a vector file, its kind and its SHA-256; a vector whose bytes do not match the hash fails the test. Vector names
are read from the manifest at run time and are not written into the code.

`VectorTests` (4 facts) uses them:

| Test | Checks |
|---|---|
| `EveryManifestFileMatchesItsSha256` | every listed file against its hash |
| `HeaderVectorsParseToTheConfirmedFields` | header vectors of FDB and BIMx files |
| `BlockVectorsDecodeToTheExpectedPayload` | block vectors against their expected payloads |
| `DirectoryVectorParsesToTheExpectedRecords` | the directory vector against its expected records |

Each test loops over the manifest, so it is skipped as a whole when the folder is missing.

## How a test is skipped

xunit 2.9.3 has no runtime skip of its own. The attributes `[SkippableFact]` and `[SkippableTheory]` (`SkippableFact.cs`)
report a test that throws `SkipException` as skipped, with the message as the reason. `TestFiles` and `TestVectors` throw
it when their input is missing, so a test needs no code of its own to skip. The reason says which input is missing and
which environment variable to set, and also says so when the variable names a folder that does not exist.

## What runs without the private files

With neither variable set and no `test_files` folder, 146 of the 159 test cases run and pass on each target framework
(`net8.0` and `net10.0`) and 13 are skipped: the 9 facts of `SampleTests` and the 4 facts of `VectorTests`.

Run with the private files:

    PLAIO_TEST_FILES=<folder> PLAIO_TEST_VECTORS=<folder> dotnet test

PowerShell:

    $env:PLAIO_TEST_FILES = '<folder>'
    $env:PLAIO_TEST_VECTORS = '<folder>'
    dotnet test
