# Changelog

All notable changes to PackageMedic are documented in this file.

## [Unreleased]

No changes yet.

## [0.4.0] - 2026-08-03

PackageMedic 0.4 is the first public release after 0.1.0. It consolidates and supersedes the unpublished 0.2 and 0.3 development milestones with their CI, policy, baseline, and safety work rebuilt on the hardened 0.4 dependency and website foundation.

### Added

- Deterministic SARIF 2.1.0 output for PM001–PM007, stable diagnostic fingerprints, repository-relative locations, and standard baseline states.
- `--output` / `-o` for atomic text, JSON, and SARIF report files, plus `--sarif-output` for producing JSON and SARIF from one analysis.
- Official GitHub Action with native annotations, job summaries, outputs, isolated artifacts, optional Code Scanning upload, and one-analysis JSON/SARIF generation.
- Optional `.packagemedic.json` policy with schema version 1, CLI-over-config precedence, rule enablement/severity, portable exclusions, justified suppressions, failure thresholds, baseline selection, process timeouts, and bounded parallelism.
- `package-medic init` for generating a validated starter configuration.
- Deterministic baseline create/update workflows and New, Existing, and Resolved diagnostic classification.
- `--fail-on-new` for introducing PackageMedic to existing repositories without accepting new dependency regressions.
- PM006 `FloatingPackageVersion` for conservative detection of documented NuGet floating patterns in `PackageVersion`, `PackageReference Version`, and `VersionOverride`.
- `package-medic rules` and `package-medic explain PM###` for local rule discovery.
- `package-medic clean --dry-run` for a read-only plan of high-confidence unused central-version candidates. Applying changes remains unavailable.
- `package-medic audit [path]` and `doctor --audit` for opt-in vulnerability data from the active SDK's official NuGet audit command, with optional transitive coverage.
- PM007 `VulnerablePackage`, preserving package ID, resolved version, advisory, framework, dependency kind, and NuGet severity in structured reports.
- Portable direct/transitive package inventory with requested and resolved versions plus central, project, override, implicit, or resolved version source.
- Runtime-identifier-aware package inventory so RID-specific assets remain distinct instead of being collapsed into the framework-only graph.
- `package-medic diff <git-ref> [path]` for deterministic added, resolved, and severity-changed diagnostics; package additions/removals/compound attribute changes; and added, removed, or modified CPM settings.
- GitHub Action inputs `audit`, `include-transitive-audit`, and `diff-base`, with matching annotations, SARIF, and job-summary behavior.

### Changed

- JSON reports expose an independent `schemaVersion`, policy counts, justified suppressions, fingerprints, baseline states, `packages`, `projectSettings`, and `vulnerabilities`; diff JSON adds a versioned `diff` object with completion state and separate baseline/current analysis errors while preserving the normal report fields.
- SARIF uses the same stable fingerprint implementation as baselines and includes PM001–PM007 with standard `baselineState` values.
- The GitHub Action accepts config, baseline, audit, transitive-audit, and Git-diff inputs; can annotate `new`, `all`, or `none`; and summarizes policy, baseline, vulnerability, and graph-change results from one analysis.
- The NuGet-facing README uses portable Markdown rather than raw HTML.
- Project discovery skips generated dependency, coverage, report, and website build directories.
- The website uses a smaller Next.js-only static build with exact direct dependency versions, npm lockfile integrity policy, disabled dependency lifecycle scripts in CI, registry signature checks, vulnerability audits, and Dependabot coverage.
- Repository NuGet dependencies are content-hash locked; CI restores in locked mode and self-audits the complete direct/transitive graph with PM007 before packaging.
- The `net8.0` tool can roll forward to a compatible newer major .NET runtime, and repository builds can use a newer major SDK when the selected .NET 9 SDK is unavailable.
- Repository tests use the current stable xUnit v3 toolchain and pass the test runner's cancellation token through asynchronous test operations.
- Large repositories use a single discovery pass, indexed package/policy lookups, direct stream parsing for assets, streamed report files, shared MSBuild source caches, and deterministic bounded restore/audit/MSBuild worker queues configurable with `maxParallelism` or `--max-parallelism`.
- Synthetic regression coverage exercises 1,000-project discovery, 5,000-diagnostic baselines, bounded parallel execution, streaming reports, and Git snapshot limits.
- Third-party GitHub Actions are pinned to immutable commits, with Dependabot tracking their release channels.
- The untrusted website build job has read-only permissions; Pages publication and OIDC permissions exist only in the isolated deploy job.
- A complete responsive `/docs` website covers installation, commands, configuration, baselines, the GitHub Action, reports, PM001–PM007, security boundaries, and troubleshooting with searchable navigation and copyable examples.

### Safety

- PackageMedic remains read-only and implements no advisory HTTP client of its own.
- Report files are created only when explicitly requested and use temporary-file plus atomic-replacement writes.
- Every suppression requires a non-empty reason and remains visible in machine and human summaries.
- `clean` requires `--dry-run`; no dependency-changing `--apply` command exists in 0.4.
- Restore and MSBuild evaluation have configurable timeouts; subprocess output is bounded, credentials are redacted, unsafe terminal controls are removed, and cancellation terminates process trees.
- Machine-specific absolute paths are excluded from portable SARIF, baselines, and GitHub annotations.
- Repository-controlled configuration, baseline, solution, assets, XML source, and Action JSON report inputs have explicit size/count limits before memory-intensive parsing.
- Git references are resolved to canonical commits and materialized with `git archive`, without checkout, branch, index, or worktree changes.
- Snapshot TAR extraction prevents path traversal, converts tracked symbolic links to inert files, bounds Git operations, and removes owned temporary directories on success or failure.
- Discovery does not recurse through symbolic links or junctions, and solution project references must remain inside the selected analysis root without link-based escapes.
- Inaccessible discovery paths and missing solution projects produce operational errors instead of partial clean reports.
- Snapshot paths must be canonical; ambiguous separators, dot segments, Windows device aliases, alternate data streams, and trailing-dot/space aliases are rejected before extraction.
- Git snapshots enforce archive, entry-count, single-file, expanded-size, free-space, and extraction-time limits.
- Secret redaction preserves structured JSON, strips unsafe terminal controls, and baseline JSON rejects duplicate properties.
- Diff refuses to publish partial package, diagnostic, or CPM changes when either graph has analysis errors; it returns operational exit code `2` with sanitized errors instead.

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
[0.4.0]: https://github.com/GonzMeza/package-medic/compare/v0.1.0...v0.4.0
[Unreleased]: https://github.com/GonzMeza/package-medic/compare/v0.4.0...HEAD
