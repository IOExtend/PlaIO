# The scene adapter

`PlaIO.Scene` (namespace `PlaIO.Scene`) turns an Archicad container read by PlaIO into an `IOScene`
[`SceneDocument`](https://github.com/IOExtend/IOScene/blob/main/docs/contract.md) — the neutral scene model that
[IOViewer](https://github.com/IOExtend/IOViewer) and the coordination tools consume. The adapter uses only the public
API of the `PlaIO` library and depends on the exact `PlaIO` version it ships with.

## What the scene holds today

- One `SceneModel` for the file: `Key` and `Name` (see [Frozen derivations](#frozen-derivations)), `SourceFormat`
  (`pla` or `pln`, the lower-case file extension) and `Producer` (`PlaIO.Scene <version>`).
- No elements, no geometry, no property sets: the container reader does not decode element contents yet. Mode 2 (LZ)
  payloads, the meaning of the object directory records and the class names are research items (repository issues #7,
  #8, #9). The scene holds no guesses: what cannot be read exactly is not written, and the `container-only` diagnostic
  says so.

A geometry-free scene file is a valid scene document: a consumer that reads it shows the model with no content to draw.

## Frozen derivations

The consumers of a scene store `SceneModel.Key` together with the source id of an element, so a change of the
derivation is a breaking release of `PlaIO.Scene`:

- `Key` is `PlaSceneOptions.ModelKey` when given; otherwise the file name with its extension, without its directory.
- The key never switches to a better identifier. A better identifier, once the format gives the adapter one, goes into
  a new `SceneModel` member, not into the key.

The identity tests of the derivation are in `tests/PlaIO.Tests/PlaSceneReaderTests.cs`.

## Diagnostics

| Severity | Code | Meaning |
|---|---|---|
| Info | `container-only` | The reader decodes the container only: elements, properties and geometry are not read yet, so the scene has no elements. |

## Publication

The project is `IsPackable=false` and is not published on NuGet: it ships on a `PlaIO` release tag, with an exact
`[X.Y.Z]` dependency on `PlaIO` and an `IOScene [0.1.1, 0.2.0-0.0)` range, when the reader decodes elements and
geometry — the adapter trigger of the [target architecture](https://github.com/IOExtend/.github/blob/main/profile/README.md)
("PlaIO decodes geometry / has elements to export / has a writer"). Until then nothing publishes, so no consumer can
build on an empty mapping.
