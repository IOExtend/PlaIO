# Dependencies

All package versions live in `Directory.Packages.props` (central package management); project files carry no versions.
Build settings shared by every project live in `Directory.Build.props`; the SDK is pinned in `global.json`
(`10.0.100`, `rollForward: latestFeature`, no prereleases).

## Runtime packages (library and tool)

None. The library and the command line tool use only the .NET base class library, so the published packages bring no
third-party code with them.

## Build-time packages

| Package | Version | Used for |
|---|---|---|
| Microsoft.SourceLink.GitHub | 10.0.403 | Source Link information in the PDBs, so a debugger can fetch the matching sources from GitHub. |

It is referenced by every project with `PrivateAssets` all, so it is not a dependency of the published packages.

## Test packages

Pinned to exact versions: Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3 and xunit.runner.visualstudio 4.0.1.
They are referenced only by `tests/PlaIO.Tests`, which is not packed.

## Auditing

`NuGetAudit` is on with `NuGetAuditMode=all` (direct and transitive) and `NuGetAuditLevel=low`, so every known
advisory is reported as an NU1901-NU1904 warning at restore. In `src` and `tests`, `TreatWarningsAsErrors` turns these
warnings into restore errors, so an advisory fails the build.
Check manually:

    dotnet list package --vulnerable --include-transitive
    dotnet list package --outdated

At the time of writing, the audit reports no vulnerable packages in any project.
Re-run the audit when changing dependencies.

## Build settings

`Directory.Build.props` sets `LangVersion` 14.0, `Nullable` enable, `ImplicitUsings` enable, `Deterministic` true,
`EmbedUntrackedSources` true, `PublishRepositoryUrl` true, `VersionPrefix` 0.1.0, symbol packages in `snupkg` format (`IncludeSymbols`),
and `ContinuousIntegrationBuild` true when `CI`, `GITHUB_ACTIONS` or `TF_BUILD` is set. It also sets `PathMap` on every
build, not only on CI, so the folder of the build machine does not appear in PDBs and binaries. `src/Directory.Build.props`
and `tests/Directory.Build.props` import it and set `TreatWarningsAsErrors` true; the tests file also turns
`GenerateDocumentationFile` off. The library generates its XML documentation file, which feeds the
[API reference](api/PlaIO.yml). The library, the CLI and the tests target `net8.0` and `net10.0`.
