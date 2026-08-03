# Changelog

All notable changes to PackageMedic are documented in this file.

## [Unreleased]

No changes yet.

## [0.3.0]

PackageMedic 0.3 team-adoption stable release.

### Added

- Optional `.packagemedic.json` policy with schema version 1, CLI-over-config precedence, rule enablement/severity, portable exclusions, justified suppressions, failure thresholds, baseline selection, and process timeouts.
- `package-medic init` for generating a validated starter configuration.
- Deterministic baseline create/update workflows and New, Existing, and Resolved diagnostic classification.
- `--fail-on-new` for introducing PackageMedic to existing repositories without accepting new dependency regressions.
- PM006 `FloatingPackageVersion` for conservative detection of documented NuGet floating patterns in `PackageVersion`, `PackageReference Version`, and `VersionOverride`.
- `package-medic rules` and `package-medic explain PM###` for local rule discovery.
- `package-medic clean --dry-run` for a read-only plan of high-confidence unused central-version candidates. Applying changes remains unavailable.
- Synthetic 5,000-diagnostic baseline regression coverage.

### Changed

- JSON reports expose an independent `schemaVersion`, policy counts, justified suppressions, fingerprints, and baseline states while preserving the 0.2 fields.
- SARIF results use the same stable fingerprint implementation as baselines and include standard `baselineState` values.
- The GitHub Action accepts config/baseline inputs, can annotate `new`, `all`, or `none`, and summarizes New, Existing, Resolved, and Suppressed counts from one analysis.
- Restore and MSBuild evaluation have configurable timeouts; subprocess output is bounded and process trees are terminated on cancellation.
- Invalid baselines are reported as operational errors without stack traces, and cancellation cleanup handles documented platform termination failures.
- The GitHub Action streams tool output without a fixed child-process buffer and conservatively annotates reports produced by pre-0.3 tools.
- `package-medic init` recognizes existing directories even when their names end in `.json`.
- Website and documentation now describe the stable 0.3 policy/baseline workflow and PM001–PM006.

### Safety

- PackageMedic remains read-only with respect to dependency declarations.
- Every suppression requires a non-empty reason and remains visible in machine and human summaries.
- `clean` requires `--dry-run`; no `--apply` command exists in 0.3.

## [0.2.0]

PackageMedic 0.2 CI-focused stable release.

### Added

- Deterministic SARIF 2.1.0 output for PM001–PM005.
- `--output` / `-o` for atomic text, JSON, and SARIF report files.
- `--sarif-output` for producing JSON and SARIF from one analysis.
- Official GitHub Action with native annotations, job summaries, outputs, artifacts, and optional Code Scanning upload.
- Stable diagnostic fingerprints and repository-relative SARIF locations.

### Changed

- NuGet-facing README now uses portable Markdown instead of raw HTML.
- Website and documentation describe the stable 0.2 channel and CI workflow.
- The GitHub Action analyzes once, isolates reports and upload names per invocation, and uses consistent checkout versions across workflows.

### Safety

- PackageMedic remains read-only; report files are written only when explicitly requested.
- Credentials are redacted, and machine-specific absolute paths are excluded from SARIF and GitHub annotations.

## [0.1.0] - 2026-08-02

First stable release of the read-only dependency diagnostics workflow.

### Added

- `package-medic doctor [path]` for projects, solutions, solution filters, and directories.
- PM001–PM005 diagnostics for stale central versions, version drift, CPM bypasses, duplicate central entries, and NuGet restore problems.
- Text and deterministic JSON output for local use and CI.
- Configurable failure thresholds, restore control, and output verbosity.
- SDK-style project, Central Package Management, and multi-target framework analysis.
- Windows, Linux, and macOS CI coverage.
- Credential redaction for subprocess output.
- Public PackageMedic website and documentation.

### Safety

- PackageMedic 0.1.0 never modifies project, props, lock, or assets files.
- No telemetry is collected.

[0.1.0]: https://github.com/GonzMeza/package-medic/releases/tag/v0.1.0
[0.2.0]: https://github.com/GonzMeza/package-medic/compare/v0.1.0...v0.2.0
[0.3.0]: https://github.com/GonzMeza/package-medic/compare/v0.2.0...v0.3.0
[Unreleased]: https://github.com/GonzMeza/package-medic/compare/v0.3.0...HEAD
