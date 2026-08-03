![PackageMedic logo](https://raw.githubusercontent.com/GonzMeza/package-medic/v0.4.0/assets/brand/packagemedic-logo.png)

# PackageMedic

**A dependency doctor for .NET projects**

[![NuGet version](https://img.shields.io/nuget/v/PackageMedic.Tool.svg)](https://www.nuget.org/packages/PackageMedic.Tool)
[![NuGet downloads](https://img.shields.io/nuget/dt/PackageMedic.Tool.svg)](https://www.nuget.org/packages/PackageMedic.Tool)
[![CI status](https://github.com/GonzMeza/package-medic/actions/workflows/ci.yml/badge.svg)](https://github.com/GonzMeza/package-medic/actions/workflows/ci.yml)
[![MIT license](https://img.shields.io/github/license/GonzMeza/package-medic)](LICENSE)
[![Stable status](https://img.shields.io/badge/status-stable%200.4.0-brightgreen)](https://github.com/GonzMeza/package-medic/releases)

PackageMedic 0.4 is the graph-observability release of the read-only dependency doctor for SDK-style .NET projects. It adds structured direct/transitive package inventory, opt-in NuGet vulnerability auditing, PM007, and safe comparisons against a Git reference to the policy, baseline, JSON, SARIF, and GitHub Action workflow.

> **Important:** PackageMedic 0.4 remains read-only. `audit` and `diff` report evidence; even `clean` only produces a dry-run plan. Review every diagnostic before changing dependency declarations.

PackageMedic is not affiliated with, maintained by, sponsored by, or endorsed by Microsoft. .NET, NuGet, and related names are trademarks of their respective owners.

**[Read the complete PackageMedic documentation](https://gonzmeza.github.io/package-medic/docs/)** for installation, commands, configuration, baselines, GitHub Actions, reports, diagnostics, security, and troubleshooting.

## Why PackageMedic?

Dependency problems often emerge across project boundaries: a `PackageVersion` can be unused in an entire central-management scope, two projects can quietly select different direct versions, or a useful NU1605 message can disappear in restore noise. PackageMedic evaluates projects through MSBuild and reads NuGet's resolved assets graph so these findings have project context.

PackageMedic never writes to project files, props files, lock files, or assets files. Unless `--no-restore` is supplied, `doctor` runs the standard `dotnet restore` command. Vulnerability auditing invokes the SDK's official NuGet audit command and may still contact configured feeds.

## Installation

Install the latest stable release from NuGet:

```console
dotnet tool install --global PackageMedic.Tool
```

Install PackageMedic 0.4 explicitly:

```console
dotnet tool install --global PackageMedic.Tool --version 0.4.0
```

Update an existing installation:

```console
dotnet tool update --global PackageMedic.Tool
```

## Requirements and local development

- .NET 8 or a newer .NET runtime.
- A .NET SDK capable of loading the projects being analyzed.

The tool targets `net8.0` to keep its minimum runtime broad and its runtime configuration permits compatible major-version roll-forward when only a newer .NET runtime is installed. Project evaluation and restore run through the active `dotnet` SDK, so SDK-style projects targeting .NET 8, 9, and 10 can be analyzed when their required SDK is installed.

Build and install a local package:

```console
dotnet restore PackageMedic.sln --locked-mode
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
package-medic audit ./MySolution.sln --include-transitive
package-medic diff origin/main ./MySolution.sln
package-medic init
package-medic rules
package-medic explain PM007
package-medic clean . --dry-run
```

The path is optional and defaults to the current directory.

```text
--no-restore
--format text|json|sarif
--output, -o <path>
--sarif-output <path>
--config <path>
--no-config
--baseline <path>
--fail-on none|warning|error
--fail-on-new none|warning|error
--audit
--include-transitive
--restore-timeout <seconds>
--evaluation-timeout <seconds>
--max-parallelism <1-32>
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

# Produce a repository-relative SARIF 2.1.0 report for CI
package-medic doctor . --format sarif --output artifacts/packagemedic.sarif

# Produce JSON and SARIF from one analysis
package-medic doctor . --format json --output artifacts/packagemedic.json --sarif-output artifacts/packagemedic.sarif

# Gate only diagnostics that were not accepted into the baseline
package-medic doctor . --baseline .packagemedic-baseline.json --fail-on none --fail-on-new warning

# Ask the active .NET SDK/NuGet tooling for known direct and transitive vulnerabilities
package-medic audit . --include-transitive --format json --fail-on error

# Compare diagnostics, package versions, dependency kind, and CPM settings with a Git ref
package-medic diff origin/main . --format json --fail-on warning
```

Restore progress is written to standard error, so standard output remains valid JSON or SARIF. `--output` atomically writes the selected format, while `--sarif-output` can additionally write SARIF from that same in-memory analysis. PackageMedic creates destination directories and never mixes progress into report files.

`--no-restore` requires a usable `obj/project.assets.json` for every selected project. In `diff` mode that requirement applies independently to both the Git snapshot and the current checkout. If either analysis is incomplete, PackageMedic returns exit code `2`, exposes sanitized baseline/current errors, and deliberately reports no partial changes.

## Team policy

Run `package-medic init` to create `.packagemedic.json`. The CLI searches from the selected target to the repository root; `--config` selects an explicit file and `--no-config` disables discovery. Command-line values take precedence over configuration, which takes precedence over safe defaults.

```json
{
  "$schema": "https://raw.githubusercontent.com/GonzMeza/package-medic/main/schemas/packagemedic.schema.json",
  "schemaVersion": 1,
  "failOn": "none",
  "failOnNew": "warning",
  "baseline": ".packagemedic-baseline.json",
  "maxParallelism": 4,
  "exclude": ["**/bin/**", "**/obj/**"],
  "rules": {
    "PM006": { "enabled": true, "severity": "warning" }
  },
  "suppressions": [
    {
      "rule": "PM003",
      "path": "src/Legacy/**",
      "package": "Example.Legacy",
      "reason": "Intentional exception tracked in issue 42"
    }
  ],
  "timeouts": { "restoreSeconds": 300, "evaluationSeconds": 60 }
}
```

Every suppression requires a reason. Suppressed, excluded, and disabled findings are removed from failure thresholds but remain counted in report policy metadata; suppression reasons are preserved in `suppressedDiagnostics` and detailed text output.

## Baselines and gradual adoption

Create a deterministic baseline after reviewing the current findings:

```console
package-medic baseline create . --output .packagemedic-baseline.json
package-medic doctor . --baseline .packagemedic-baseline.json --fail-on none --fail-on-new warning
```

Current diagnostics are classified as `new` or `existing`; baseline entries no longer present are counted as `resolved` and listed in JSON `resolvedDiagnostics`. Refresh the accepted state explicitly with:

```console
package-medic baseline update . --baseline .packagemedic-baseline.json
```

Baselines use the same portable fingerprint as SARIF, contain no timestamps, and are stable across repository locations and source-line movement.

## CI and SARIF

PackageMedic 0.4 maps PM001–PM007 to deterministic SARIF 2.1.0 with repository-relative locations, stable fingerprints, standard baseline states, rule help links, confidence, and original NuGet codes. SARIF can be consumed by GitHub Code Scanning or any compatible CI system.

The official GitHub Action installs the PackageMedic version associated with its tag, emits native file annotations, writes a job summary, preserves the CLI exit-code contract, and can upload the SARIF report after the scan.

```yaml
permissions:
  contents: read
  security-events: write

steps:
  - uses: actions/checkout@v6
  - uses: GonzMeza/package-medic@v0.4.0
    with:
      path: .
      config: .packagemedic.json
      baseline: .packagemedic-baseline.json
      fail-on: none
      fail-on-new: warning
      audit: 'true'
      include-transitive-audit: 'true'
      annotations: new
      upload-sarif: 'true'
```

Repositories without GitHub Code Scanning can disable `upload-sarif`; native annotations and the generated report remain available. See the [complete Action reference](action/README.md) for every input, output, permission, and security boundary.

For pull-request dependency diffs, check out enough history and select a reachable base reference. `diff-base` is intentionally incompatible with `baseline` and `fail-on-new`, because the Git comparison itself defines what is new:

```yaml
- uses: actions/checkout@v6
  with:
    fetch-depth: 0
- uses: GonzMeza/package-medic@v0.4.0
  with:
    diff-base: origin/main
    audit: 'true'
    fail-on: warning
```

The Action restores both graphs by default. Setting `restore: 'false'` is safe only when usable assets files are tracked for every project in both revisions; otherwise the comparison is marked incomplete and exits with code `2`.

## Diagnostics

| Code | Default severity | Meaning |
| --- | --- | --- |
| PM001 | warning | An effective central `PackageVersion` is unused by affected projects. |
| PM002 | warning | A direct package has different explicit versions across non-CPM projects. |
| PM003 | warning | A `PackageReference` uses `Version` while CPM is enabled; intentional `VersionOverride` is respected. |
| PM004 | error | Multiple effective central entries define the same package for a project. |
| PM005 | NuGet level | Restore or `project.assets.json` contains an important NU warning/error such as NU1605, NU1107, or NU1109. |
| PM006 | warning | A `PackageVersion`, `PackageReference Version`, or `VersionOverride` uses a documented NuGet floating pattern. |
| PM007 | warning/error | NuGet reports a known vulnerability: low/moderate/unknown are warnings; high/critical are errors. |

Every diagnostic includes an explanation, evidence, affected project/scope, source location when available, a suggested action, and confidence where relevant. Use `package-medic rules` to list rules or `package-medic explain PM007` for one rule. See [the diagnostic reference](docs/diagnostics/README.md) and [the SARIF contract](docs/sarif.md).

## JSON and exit codes

JSON output is stable, camel-cased, and keeps the fields `version`, `target`, scan `summary`, `diagnostics`, and `analysisErrors`. Its independent `schemaVersion` is `1`. PackageMedic 0.4 adds portable `packages` (including `runtimeIdentifier` when applicable), `projectSettings`, and `vulnerabilities`; `diff` also adds a structured `diff` object containing `isComplete`, separate baseline/current errors, diagnostic changes, compound package-attribute changes, and added/removed/modified CPM settings. SARIF uses version 2.1.0. Both formats are deterministically ordered and never contain timestamps.

| Exit code | Meaning |
| --- | --- |
| `0` | Analysis completed without reaching the configured `--fail-on` level. |
| `1` | At least one diagnostic reached the configured level. |
| `2` | Usage, configuration, restore, or analysis failed. |

`--fail-on none` never returns `1`, but operational failures still return `2`.

## Safety and privacy

- No telemetry is collected.
- PackageMedic does not implement its own advisory HTTP client. `audit` delegates to the active SDK's official `dotnet list package --vulnerable` command, which can contact configured NuGet sources.
- `dotnet restore` can contact feeds from the user's NuGet configuration unless `--no-restore` is used.
- PackageMedic's own CI uses committed NuGet lockfiles, locked restore, and a direct/transitive PM007 self-audit before packaging.
- Common credential-shaped values in subprocess output are redacted without corrupting JSON, and unsafe terminal controls are removed before display.
- Subprocess output is bounded, restore/evaluation timeouts are configurable, and cancellation terminates the process tree.
- Repository-controlled configuration, baseline, solution, assets, XML source, and Action report inputs have explicit size/count limits to prevent memory exhaustion in CI.
- Recursive discovery uses one filesystem pass, reports inaccessible directories as operational errors, rejects missing solution projects, and cannot escape the analysis root through symbolic links or junctions.
- Restore, audit, and MSBuild evaluation use deterministic bounded worker queues, so process and pending-task counts stay controlled. Use `--max-parallelism` or `maxParallelism` in `.packagemedic.json` to tune large repositories.
- Assets files are parsed directly from file streams and JSON/SARIF report destinations are streamed to reduce peak memory; package and policy lookups are indexed rather than repeatedly rescanned.
- `diff` resolves and archives a commit without checkout, branch switching, index changes, or worktree changes; canonical-path TAR extraction rejects traversal and Windows path aliases, enforces archive/entry/expanded-size limits, checks free space, and cleans up the temporary snapshot.
- `clean --dry-run` only lists PM001 candidates. Version 0.4 offers no apply/fix command.

## Current limitations

- SDK-style C# projects and `PackageReference` only; `packages.config` is not supported.
- The installed SDK must support MSBuild's evaluated `-getProperty`/`-getItem` JSON output and the target project's SDK.
- PackageMedic favors avoiding false positives: dynamically generated or unsafe-to-evaluate conditions may result in no diagnostic.
- PM001 reasons over effective evaluated central items, direct references, and the existing resolved graph; it does not speculate about packages used only by source code reflection or custom build logic.
- PM006 ignores unresolved MSBuild expressions and only recognizes documented floating-version forms; it is not an update recommender.
- Restore failures are reported and return exit code `2`; PackageMedic does not replace NuGet restore.
- Vulnerability results depend on the active SDK/NuGet audit sources and can return operational error `2` when those sources are unavailable.
- `diff --no-restore` cannot reconstruct missing assets from Git; both selected revisions must already contain usable tracked assets files.
- No automatic fixes, IDE extension, or desktop UI is included yet.

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

Candidates after the graph-observability 0.4 release is proven:

- an explicitly gated `clean --apply`
- assisted NU1605 repair and CPM migration
- explicit remediation plans that remain review-first
- Visual Studio or VS Code integration

Package publishing, feed administration, and replacing NuGet remain out of scope.

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), the [Code of Conduct](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md) before contributing.

Release history is documented in [CHANGELOG.md](CHANGELOG.md).

## License

[MIT](LICENSE) © 2026 GonzMeza.
