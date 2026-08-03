# Changelog

All notable changes to PackageMedic are documented in this file.

## [Unreleased]

No changes yet.

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
[Unreleased]: https://github.com/GonzMeza/package-medic/compare/v0.2.0...HEAD
