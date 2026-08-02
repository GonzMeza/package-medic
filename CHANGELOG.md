# Changelog

All notable changes to PackageMedic are documented in this file.

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
