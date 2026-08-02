<p align="center">
  <img src="assets/brand/packagemedic-logo.png" width="180" alt="PackageMedic logo">
</p>

<h1 align="center">PackageMedic</h1>

<p align="center"><strong>A dependency doctor for .NET projects</strong></p>

<p align="center">
  <a href="https://www.nuget.org/packages/PackageMedic.Tool"><img alt="NuGet version" src="https://img.shields.io/nuget/vpre/PackageMedic.Tool.svg"></a>
  <a href="https://www.nuget.org/packages/PackageMedic.Tool"><img alt="NuGet downloads" src="https://img.shields.io/nuget/dt/PackageMedic.Tool.svg"></a>
  <a href="https://github.com/GonzMeza/package-medic/actions/workflows/ci.yml"><img alt="CI status" src="https://github.com/GonzMeza/package-medic/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/GonzMeza/package-medic"></a>
  <a href="https://github.com/GonzMeza/package-medic/releases/tag/v0.1.0"><img alt="Stable status" src="https://img.shields.io/badge/status-stable%200.1.0-brightgreen"></a>
</p>

PackageMedic 0.1 is the first stable, read-only dependency doctor for SDK-style .NET projects. It finds stale Central Package Management entries, version drift, CPM bypasses, duplicate central versions, and important NuGet restore problems—then explains what to review.

> [!IMPORTANT]
> PackageMedic 0.1 is stable for its documented read-only scope. Review every diagnostic before changing dependency declarations.

PackageMedic is not affiliated with, maintained by, sponsored by, or endorsed by Microsoft. .NET, NuGet, and related names are trademarks of their respective owners.

## Why PackageMedic?

Dependency problems often emerge across project boundaries: a `PackageVersion` can be unused in an entire central-management scope, two projects can quietly select different direct versions, or a useful NU1605 message can disappear in restore noise. PackageMedic evaluates projects through MSBuild and reads NuGet's resolved assets graph so these findings have project context.

The MVP never writes to project files, props files, lock files, or assets files. Unless `--no-restore` is supplied, it runs the standard `dotnet restore` command and clearly reports that configured feeds may be contacted.

## Installation

Install the latest stable release from NuGet:

```console
dotnet tool install --global PackageMedic.Tool
```

Update an existing installation:

```console
dotnet tool update --global PackageMedic.Tool
```

## Requirements and local development

- .NET 8 runtime or newer runtime configured to roll forward.
- A .NET SDK capable of loading the projects being analyzed.

The tool targets `net8.0` to keep its minimum runtime broad. Project evaluation and restore run through the active `dotnet` SDK, so SDK-style projects targeting .NET 8, 9, and 10 can be analyzed when their required SDK is installed.

Build and install a local package:

```console
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack src/PackageMedic.Cli --configuration Release --output artifacts/packages
dotnet tool install --global --add-source ./artifacts/packages PackageMedic.Tool
package-medic --version
```

During development, use `dotnet tool update` instead of `install` when that package ID is already installed.

## Usage

```console
package-medic doctor
package-medic doctor ./src/MyProject/MyProject.csproj
package-medic doctor ./MySolution.sln
package-medic doctor ./MySolution.slnx
package-medic doctor ./src
```

The path is optional and defaults to the current directory.

```text
--no-restore
--format text|json
--fail-on none|warning|error
--verbosity quiet|normal|detailed
--version
--help
```

Examples:

```console
# CI-oriented deterministic JSON; fail on warnings or errors
package-medic doctor . --format json --fail-on warning

# Use an assets graph produced by an earlier restore
package-medic doctor MySolution.sln --no-restore --verbosity detailed

# Report findings without failing the command
package-medic doctor --fail-on none
```

Restore progress is written to standard error, so standard output remains valid JSON when `--format json` is selected.

## Diagnostics

| Code | Default severity | Meaning |
| --- | --- | --- |
| PM001 | warning | An effective central `PackageVersion` is unused by affected projects. |
| PM002 | warning | A direct package has different explicit versions across non-CPM projects. |
| PM003 | warning | A `PackageReference` uses `Version` while CPM is enabled; intentional `VersionOverride` is respected. |
| PM004 | error | Multiple effective central entries define the same package for a project. |
| PM005 | NuGet level | Restore or `project.assets.json` contains an important NU warning/error such as NU1605, NU1107, or NU1109. |

Every diagnostic includes an explanation, evidence, affected project/scope, source location when available, a suggested action, and confidence where relevant. See [the diagnostic reference](docs/diagnostics/README.md).

## JSON and exit codes

JSON output is stable, camel-cased, and contains `version`, `target`, scan `summary`, `diagnostics`, and `analysisErrors`. Diagnostics are deterministically ordered and never contain timestamps.

| Exit code | Meaning |
| --- | --- |
| `0` | Analysis completed without reaching the configured `--fail-on` level. |
| `1` | At least one diagnostic reached the configured level. |
| `2` | Usage, configuration, restore, or analysis failed. |

`--fail-on none` never returns `1`, but operational failures still return `2`.

## Safety and privacy

- No telemetry is collected.
- PackageMedic does not call remote services itself.
- `dotnet restore` can contact feeds from the user's NuGet configuration unless `--no-restore` is used.
- Common credential-shaped values in subprocess output are redacted before display.
- The MVP performs no dependency-file mutations and offers no apply/fix command.

## Current limitations

- SDK-style C# projects and `PackageReference` only; `packages.config` is not supported.
- The installed SDK must support MSBuild's evaluated `-getProperty`/`-getItem` JSON output and the target project's SDK.
- PackageMedic favors avoiding false positives: dynamically generated or unsafe-to-evaluate conditions may result in no diagnostic.
- PM001 reasons over effective evaluated central items, direct references, and the existing resolved graph; it does not speculate about packages used only by source code reflection or custom build logic.
- Restore failures are reported and return exit code `2`; PackageMedic does not replace NuGet restore.
- No vulnerability scanning, SARIF, automatic fixes, IDE extension, or UI is included yet.

## Repository layout

```text
src/PackageMedic.Cli/              command parsing and terminal/JSON orchestration
src/PackageMedic.Core/             discovery, MSBuild evaluation, assets reader, rules
tests/PackageMedic.Core.Tests/     rule and serialization tests
tests/PackageMedic.IntegrationTests/ CLI/fixture and exit-code tests
fixtures/                          real SDK-style analysis scenarios
docs/diagnostics/                  diagnostic reference
```

See [architecture.md](docs/architecture.md) for the execution boundary and design choices.

## Roadmap

After the read-only MVP is proven:

- `clean --dry-run` and an explicitly gated `clean --apply`
- dependency diffs against a Git ref
- SARIF and a dedicated GitHub Action
- assisted NU1605 repair and CPM migration
- vulnerability analysis
- Visual Studio or VS Code integration

Package publishing, feed administration, and replacing NuGet remain out of scope.

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), the [Code of Conduct](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md) before contributing.

Release history is documented in [CHANGELOG.md](CHANGELOG.md).

## License

[MIT](LICENSE) © 2026 GonzMeza.
