![PackageMedic logo](https://raw.githubusercontent.com/GonzMeza/package-medic/v0.5.0/assets/brand/packagemedic-logo.png)

# PackageMedic

**A dependency doctor for .NET projects**

[![NuGet version](https://img.shields.io/nuget/v/PackageMedic.Tool.svg)](https://www.nuget.org/packages/PackageMedic.Tool)
[![NuGet downloads](https://img.shields.io/nuget/dt/PackageMedic.Tool.svg)](https://www.nuget.org/packages/PackageMedic.Tool)
[![CI status](https://github.com/GonzMeza/package-medic/actions/workflows/ci.yml/badge.svg)](https://github.com/GonzMeza/package-medic/actions/workflows/ci.yml)
[![MIT license](https://img.shields.io/github/license/GonzMeza/package-medic)](LICENSE)
[![Stable status](https://img.shields.io/badge/status-stable%200.5.0-brightgreen)](https://github.com/GonzMeza/package-medic/releases)

PackageMedic 0.5 is the PR-intelligence and dependency-simulation release of the read-only dependency doctor for SDK-style .NET projects. Its Impact Gate explains which direct package caused each transitive change, measures blast radius, checks package-source trust and locked-restore policy, and can stop risky pull requests. Dependency Time Machine restore-validates one exact package candidate in two independent snapshots without editing the checkout. It also adds PM008 deprecation evidence, semantic package and risk deltas, and automatic pull-request comparison to the existing policy, baseline, JSON, SARIF, and GitHub Action workflow.

> **Important:** PackageMedic 0.5 never changes dependency declarations in the checkout. `simulate` mutates only an owned disposable snapshot and verifies restore/graph evidence—not build, tests, runtime compatibility, or safety. Review every result before changing dependencies.

PackageMedic is not affiliated with, maintained by, sponsored by, or endorsed by Microsoft. .NET, NuGet, and related names are trademarks of their respective owners.

**[Read the complete PackageMedic documentation](https://gonzmeza.github.io/package-medic/docs/)** for installation, commands, configuration, baselines, GitHub Actions, reports, diagnostics, security, and troubleshooting.

## Why PackageMedic?

Dependency problems often emerge across project boundaries: a `PackageVersion` can be unused in an entire central-management scope, two projects can quietly select different direct versions, or a harmless-looking direct-package update can add dozens of transitives from an unexpected source. PackageMedic evaluates projects through MSBuild and reads NuGet's resolved assets graph so findings and pull-request changes have project context and a causal path.

PackageMedic never writes to project files, props files, lock files, or assets files in the checkout. `simulate` edits one validated version value only in an owned disposable snapshot. Unless `--no-restore` is supplied, `doctor` runs the standard `dotnet restore` command. Vulnerability and deprecation auditing invoke separate official SDK/NuGet commands and may still contact configured feeds.

## Installation

Install the latest stable release from NuGet:

```console
dotnet tool install --global PackageMedic.Tool
```

Install PackageMedic 0.5 explicitly:

```console
dotnet tool install --global PackageMedic.Tool --version 0.5.0
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
package-medic doctor ./MySolution.sln --deprecated --include-transitive
package-medic diff origin/main ./MySolution.sln
package-medic simulate Example.Package --to 2.0.0 ./MySolution.sln
package-medic init
package-medic rules
package-medic explain PM007
package-medic explain PM008
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
--deprecated
--include-transitive
--include-transitive-audit
--include-transitive-deprecated
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

# Add direct and transitive package deprecation evidence (PM008)
package-medic doctor . --deprecated --include-transitive --format json

# Keep vulnerability coverage transitive while checking deprecations directly only
package-medic audit . --include-transitive-audit --deprecated --format json

# Compare diagnostics, package versions, dependency kind, and CPM settings with a Git ref
package-medic diff origin/main . --format json --fail-on warning

# Explain dependency paths and enforce the repository Impact Gate
package-medic diff origin/main . --format json --output artifacts/impact.json

# Restore-validate one exact candidate in two independent snapshots of a clean HEAD
package-medic simulate Example.Package --to 2.0.0 MySolution.sln --format json

# Explicitly provide and redact only the private-feed variable needed by the simulation
package-medic simulate Contoso.Package --to 4.2.0 . --credential-env VSS_NUGET_EXTERNAL_FEED_ENDPOINTS
```

Restore progress is written to standard error, so standard output remains valid JSON or SARIF. `--output` atomically writes the selected format, while `--sarif-output` can additionally write SARIF from that same in-memory analysis. PackageMedic creates destination directories and never mixes progress into report files.

`--no-restore` requires a usable `obj/project.assets.json` for every selected project. In `diff` mode that requirement applies independently to both the Git snapshot and the current checkout. With restore enabled, baseline and current receive separate owned NuGet package, HTTP, plugin, CLI-home, and temporary caches so a same-ID/version artifact cannot leak across the comparison. If either analysis is incomplete, PackageMedic returns exit code `2`, exposes sanitized baseline/current errors, and deliberately reports no partial changes.

### Dependency Time Machine

`simulate <package-id> --to <exact-version> [path]` requires a clean Git worktree and one unambiguous literal direct/central version declaration. It materializes two snapshots of the same `HEAD` commit, gives each an isolated NuGet/.NET cache and home, verifies the observed declaration by structure and SHA-256, changes only its version bytes in the candidate snapshot, and compares the independent restores with the same diagnostic and Impact Gate logic as `diff`.

Verdicts are `pass`, `reject`, `noChange`, and `incomplete`. A candidate absent from configured feeds or blocked by `RestoreLockedMode` is a complete rejection/exit `1`; timeout, snapshot, audit, evaluation, or cleanup failures are incomplete/exit `2`. Restore can execute repository-controlled MSBuild logic and contact configured feeds. Build, tests, and runtime compatibility are explicitly not run. See the [complete Dependency Time Machine reference](docs/time-machine.md) and its independent [JSON schema](schemas/packagemedic-simulation.schema.json).

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
  "impact": {
    "failOnDowngrade": true,
    "failOnDirectToTransitive": true,
    "maxAddedPackages": 40,
    "maxAddedTransitivePackages": 25,
    "failOnSourceChange": true,
    "failOnContentChange": true,
    "requirePackageSourceMapping": true,
    "requireLockedMode": true,
    "allowedSources": ["https://api.nuget.org/v3/index.json"]
  },
  "timeouts": { "restoreSeconds": 300, "evaluationSeconds": 60 }
}
```

Every suppression requires a reason. Suppressed, excluded, and disabled findings are removed from failure thresholds but remain counted in report policy metadata; suppression reasons are preserved in `suppressedDiagnostics` and detailed text output.

### Dependency Impact Gate

`diff` builds a deterministic path from each changed transitive package back to the direct dependency that introduced it. The nested `diff.impact` report includes changed-package direction, direct/transitive additions, source and signature evidence when NuGet metadata is available, and the maximum blast radius: the largest number of changed transitives attributed to one direct root.

The `impact` policy can reject downgrades, loss of direct control, dependency-growth budgets, source changes, content-hash changes for the same package identity, packages outside an HTTPS source allowlist, missing Package Source Mapping for multi-feed projects, and missing locked restore. Its `PMI001`–`PMI010` codes are Impact Gate violations, not suppressible PM diagnostics. They are returned in text and JSON and make a complete `diff` exit with code `1`; operationally incomplete comparisons still return `2`.

When those reproducibility gates are enabled, PackageMedic fails closed: Package Source Mapping must come from effective `NuGet.Config` files inside the analysis root, reference configured sources, and contain a usable pattern for every resolved package. A lock file must also remain inside that root, be bounded, and contain the NuGet lock-file schema (`version` and `dependencies`); an unrelated or external file does not satisfy `requireLockedMode`.

Package-source provenance depends on metadata written by NuGet during restore. Credential-bearing, query-qualified, fragment-qualified, or reparse-point-backed metadata is rejected rather than normalized into an approved source identity. If `allowedSources` is configured and a changed package's source cannot be established, the gate fails closed with `PMI006` rather than treating unknown provenance as trusted. Use the literal `local` only when local packages are an intentional reviewed part of the build.

See the [complete Dependency Impact Gate reference](docs/impact-gate.md) for every policy, violation code, report field, and GitHub Action output.

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

PackageMedic 0.5 maps PM001–PM008 to deterministic SARIF 2.1.0 with repository-relative locations, stable fingerprints, standard baseline states, rule help links, confidence, and original NuGet codes. SARIF can be consumed by GitHub Code Scanning or any compatible CI system.

The official GitHub Action installs the PackageMedic version associated with its tag, emits native file annotations, writes a job summary, preserves the CLI exit-code contract, and can upload the SARIF report after the scan.

```yaml
permissions:
  contents: read
  security-events: write

steps:
  - uses: actions/checkout@v6
  - uses: GonzMeza/package-medic@v0.5.0
    with:
      mode: scan
      path: .
      config: .packagemedic.json
      baseline: .packagemedic-baseline.json
      fail-on: none
      fail-on-new: warning
      audit: 'true'
      include-transitive-audit: 'true'
      deprecated: 'true'
      annotations: new
      upload-sarif: 'true'
```

Repositories without GitHub Code Scanning can disable `upload-sarif`; native annotations and the generated report remain available. See the [complete Action reference](action/README.md) for every input, output, permission, and security boundary.

For pull-request dependency diffs, use the unprivileged `pull_request` event, check out enough history, and leave the default `mode: auto`. It selects `github.event.pull_request.base.sha` without fetching or modifying Git. `pull_request_target` is deliberately rejected because its default checkout is the trusted base branch and would produce a misleading self-comparison. `diff-base` remains an explicit override. Diff mode is intentionally incompatible with `baseline` and `fail-on-new`, because the Git comparison itself defines what is new:

```yaml
- uses: actions/checkout@v6
  with:
    fetch-depth: 0
- uses: GonzMeza/package-medic@v0.5.0
  with:
    audit: 'true'
    deprecated: 'true'
    fail-on: warning
```

The Action restores both graphs by default. Setting `restore: 'false'` is safe only when usable assets files are tracked for every project in both revisions; otherwise the comparison is marked incomplete and exits with code `2`.

The PR summary shows the Impact Gate result, dependency-growth counts, source and content changes, maximum blast radius, and each failed policy with its causal package path. Later workflow steps can consume `impact-gate-passed`, `impact-violations`, `impact-added-direct`, `impact-added-transitive`, `impact-max-blast-radius`, `impact-source-changes`, and `impact-content-changes`.

## Diagnostics

| Code | Default severity | Meaning |
| --- | --- | --- |
| PM001 | warning | An effective central `PackageVersion` is unused by affected projects. |
| PM002 | warning | A direct package has non-equivalent explicit versions in overlapping TFM scopes across non-CPM projects. |
| PM003 | warning | A `PackageReference` uses `Version` while CPM is enabled; intentional `VersionOverride` is respected. |
| PM004 | error | Multiple effective central entries define the same package for a project. |
| PM005 | NuGet level | Restore or `project.assets.json` contains an important NU warning/error such as NU1605, NU1107, or NU1109. |
| PM006 | warning | A `PackageVersion`, `PackageReference Version`, or `VersionOverride` uses a documented NuGet floating pattern. |
| PM007 | warning/error | NuGet reports a known vulnerability: low/moderate/unknown are warnings; high/critical are errors. |
| PM008 | warning/error | NuGet reports a deprecated package: critical bugs are errors; legacy/other/unknown reasons are warnings. |

Every diagnostic includes an explanation, evidence, affected project/scope, source location when available, a suggested action, and confidence where relevant. Use `package-medic rules` to list rules or `package-medic explain PM008` for one rule. See [the diagnostic reference](docs/diagnostics/README.md) and [the SARIF contract](docs/sarif.md).

## JSON and exit codes

JSON output is stable and camel-cased. Scan reports keep `version`, `target`, `summary`, `diagnostics`, and `analysisErrors`; their independent `schemaVersion` remains `1`. `diff` uses nested schema version `2` for semantic graph and Impact Gate changes. `simulate` has its own schema version `1` with repository, request, mutation, verification, comparison, verdict, rejection, and operational-error boundaries. SARIF remains focused on observed PM001–PM008 findings; hypothetical simulations never emit SARIF. All formats are deterministically ordered and contain no timestamps or temporary paths.

| Exit code | Meaning |
| --- | --- |
| `0` | Analysis passed, or simulation completed with `pass`/`noChange`. |
| `1` | A configured gate failed, or simulation completed with a rejected candidate. |
| `2` | Usage or an operational failure prevented a complete result. |

`--fail-on none` disables the diagnostic threshold, not the independent dependency Impact Gate. Operational failures still return `2`.

## Safety and privacy

- No telemetry is collected.
- PackageMedic does not implement its own advisory HTTP client. `--audit` and `--deprecated` delegate to separate official `dotnet list package` commands, which can contact configured NuGet sources.
- `dotnet restore` can contact feeds from the user's NuGet configuration unless `--no-restore` is used.
- PackageMedic's own CI uses committed NuGet lockfiles, locked restore, and a direct/transitive PM007 self-audit before packaging.
- Common credential-shaped values in subprocess output are redacted without corrupting JSON, and unsafe terminal controls are removed before display.
- Subprocess output is bounded, restore/evaluation timeouts are configurable, and cancellation terminates the process tree.
- Repository-controlled configuration, baseline, solution, assets, XML source, and Action report inputs have explicit size/count limits to prevent memory exhaustion in CI.
- Recursive discovery uses one filesystem pass, reports inaccessible directories as operational errors, rejects missing solution projects, and cannot escape the analysis root through symbolic links or junctions.
- Restore, audit, and MSBuild evaluation use deterministic bounded worker queues, so process and pending-task counts stay controlled. Use `--max-parallelism` or `maxParallelism` in `.packagemedic.json` to tune large repositories.
- Assets files are parsed directly from file streams and JSON/SARIF report destinations are streamed to reduce peak memory; package and policy lookups are indexed rather than repeatedly rescanned.
- Package-source provenance is accepted only from bounded regular NuGet restore metadata; reparse-point-backed, credential-bearing, query-qualified, and fragment-qualified values are not exposed as trusted evidence.
- If a persistent package loses previously observed source or content-hash evidence, `diff` records that loss as a source/content change and the default Impact Gate requires review.
- `diff` resolves and archives a commit without checkout, branch switching, index changes, or worktree changes; gives both analyses independent package/network/plugin caches; rejects tracked or repository-local archive-transforming attributes; preserves executable mode on Unix; enforces archive/entry/expanded-size limits; and cleans up marker-owned temporary state without following links.
- `simulate` uses two separately restored snapshots of one clean `HEAD`, validates a SHA-256/XML precondition, preserves declaration bytes outside the version value, and rechecks the original worktree after no-follow cleanup.
- Every simulation snapshot has independent NuGet/HTTP/plugin caches, CLI home, user-home aliases, app-data, and temporary directories. No private-feed variable is inherited unless named with `--credential-env`, whose value is then treated as a redaction secret.
- Snapshot isolation protects checkout integrity but is not an OS sandbox: restore/MSBuild still runs with the caller's host permissions and can execute repository-controlled logic or contact configured sources.
- `clean --dry-run` only lists PM001 candidates. Version 0.5 offers no apply/fix command.

## Current limitations

- SDK-style C# projects and `PackageReference` only; `packages.config` is not supported.
- The installed SDK must support MSBuild's evaluated `-getProperty`/`-getItem` JSON output and the target project's SDK.
- PackageMedic favors avoiding false positives: dynamically generated or unsafe-to-evaluate conditions may result in no diagnostic.
- PM001 reasons over effective evaluated central items, direct references, and the existing resolved graph; it does not speculate about packages used only by source code reflection or custom build logic.
- PM006 ignores unresolved MSBuild expressions and only recognizes documented floating-version forms; it is not an update recommender.
- Normal scan restore failures return exit `2`. A candidate restore rejection inside an otherwise valid simulation is evidence and returns `1`; timeout or infrastructure failure remains `2`.
- Vulnerability results depend on the active SDK/NuGet audit sources and can return operational error `2` when those sources are unavailable.
- Deprecation results likewise depend on configured NuGet sources and are only requested with `--deprecated`.
- `diff --no-restore` cannot reconstruct missing assets from Git; both selected revisions must already contain usable tracked assets files.
- `simulate` accepts one exact direct/central package version and a clean committed tree. It deliberately rejects dynamic, conditional, ambiguous, transitive-only, external, submodule/LFS-missing, untracked/generated, or Git export-transformed dependency inputs.
- A passing simulation proves only observed restore and dependency-graph policy; it does not prove build, test, API, binary, or runtime compatibility.
- No automatic fixes, IDE extension, or desktop UI is included yet.

## Repository layout

```text
src/PackageMedic.Cli/              command parsing and terminal/JSON orchestration
src/PackageMedic.Core/             discovery, MSBuild evaluation, assets reader, rules
tests/PackageMedic.Core.Tests/     rule and serialization tests
tests/PackageMedic.IntegrationTests/ CLI/fixture and exit-code tests
fixtures/                          real SDK-style analysis scenarios
docs/diagnostics/                  diagnostic reference
schemas/packagemedic-simulation.schema.json  Dependency Time Machine report contract
```

See [architecture.md](docs/architecture.md) for the execution boundary and design choices.

## Roadmap

Candidates after the PR-intelligence and restore-simulation 0.5 release is proven:

- 0.6: opt-in disposable CI/container build-and-test verification plus SBOM/provenance export
- 0.7: dependency bisect and a read-only VS Code extension over the stable report contracts
- 0.8: explicit review-first remediation plans and a separately gated apply workflow
- 0.9 only if a dedicated stabilization cycle is required before 1.0

Package publishing, feed administration, and replacing NuGet remain out of scope.

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), the [Code of Conduct](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md) before contributing.

Release history is documented in [CHANGELOG.md](CHANGELOG.md).

## License

[MIT](LICENSE) © 2026 GonzMeza.
