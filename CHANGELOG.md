# Changelog

All notable changes to PackageMedic are documented in this file.

## [Unreleased]

No changes yet.

## [0.6.0] - 2026-08-06

PackageMedic 0.6 turns dependency comparison into an opt-in verified experiment while preserving the read-only checkout boundary.

### Added

- Ordered `--verify restore|build|test` execution for `diff` and Dependency Time Machine; higher levels imply every preceding stage.
- Independently restored immutable baseline/candidate snapshots with generated `dotnet build --no-restore` and bounded `dotnet test --no-build --no-restore` execution.
- Conservative comparative verdicts: candidate-only deterministic build/test failures reject, while unusable baselines, missing evidence, timeouts, and operational failures remain incomplete.
- VSTest and Microsoft Testing Platform planning with bounded streaming TRX parsing, stable failed-test identities, and explicit missing-reporter evidence.
- Diff schema 3 and simulation schema 2 structured verification evidence.
- Deterministic CycloneDX 1.7 output through `sbom` and `--sbom-output`; current composition is explicitly marked incomplete rather than inventing unobserved dependency edges.
- `--provenance-output` for verified Git diffs, producing deterministic unsigned in-toto Statement v1 analysis evidence bound to the candidate commit, baseline commit, comparison-report digest, configuration fingerprint, verification verdict, and an SBOM digest when a complete resolved graph exists.
- GitHub Action verification controls, job-summary tables, regression/test outputs, self-hosted execution authorization, and verified-diff provenance artifacts.
- Published analysis-attestation JSON Schema and a v0.6 architecture/security boundary.

### Changed

- Verified Git diffs require a clean current worktree and compare two resolved immutable commits; simulation materializes two snapshots of the same immutable `HEAD`.
- Baseline and candidate restores, builds, tests, caches, homes, temporary files, and result roots are isolated and never reuse checkout `bin`, `obj`, or TRX state.
- Restore, MSBuild evaluation, build, and test now share the same explicit verification configuration, preventing cross-configuration evidence.
- Multi-target restore rejection is deterministic only when every failed target has the same recognized structured cause; mixed or unclassified failures remain incomplete.
- Pull-request gate configuration comes from the trusted base revision or explicit caller-owned policy, never from the candidate being evaluated.
- The Action rejects `pull_request_target` in every mode and keeps executable verification disabled unless explicitly selected.

### Safety

- Build targets, analyzers, source generators, test adapters, and tests execute repository-controlled code. Verification is intended for trusted local repositories or ephemeral GitHub-hosted runners and is not an operating-system sandbox.
- PackageMedic generates fixed argument vectors and never accepts arbitrary build/test shell fragments.
- Self-hosted build/test verification requires explicit authorization and an independently secured execution boundary.
- TRX rejects DTDs, external entities, links, path escapes, excessive files/bytes/results, malformed counts, and unowned result roots.
- Provenance is unsigned PackageMedic analysis evidence, not DSSE, a signature, or SLSA build provenance; signing remains external.
- CI exercises verified Action restore mode on Windows, Linux, and macOS, runs native Microsoft Testing Platform end to end on .NET 10, and validates generated CycloneDX 1.7 output with the official CycloneDX CLI pinned by SHA-256.
- Verification disables reusable MSBuild and compiler servers so isolated snapshot files are not retained by background build processes.

## [0.5.0] - 2026-08-05

PackageMedic 0.5 turns the existing read-only graph comparison into pull-request intelligence. Its new dependency Impact Gate explains causal paths and blast radius, enforces source and reproducibility policy, adds official NuGet deprecation data, and lets the GitHub Action select a safe PR comparison automatically without modifying or fetching repository state.

### Added

- Dependency Time Machine through `package-medic simulate <package-id> --to <exact-version> [path]`, a restore-validated what-if comparison over two independent snapshots of the same clean `HEAD` commit.
- Deterministic dependency-simulation schema version 1 with portable repository/request/mutation/verification/comparison boundaries; `pass`, `reject`, `noChange`, and `incomplete` verdicts; separate rejection reasons and operational errors; and explicit restore/build/test/runtime evidence.
- Exact literal simulation support for CPM `PackageVersion`, project `PackageReference Version`, and `VersionOverride`, including NuGet-equivalent no-change recognition and candidate resolution checks across every selected project/framework/RID context.
- Explicit `--credential-env <name>` inheritance for private-feed variables inside the otherwise clean simulation subprocess environment, with exact-value output redaction and repeatable variables.
- PM008 `DeprecatedPackage`, populated by the active SDK's official `dotnet list package --deprecated --format json --output-version 1` output.
- `--deprecated` for `doctor`, `audit`, and `diff`, with optional direct and transitive coverage through the existing `--include-transitive` switch.
- Structured `deprecatedPackages` report data including NuGet deprecation reasons, dependency kind, target framework, and recommended replacement package/version range when supplied by the source.
- Critical-bug deprecations are errors by default; legacy, other, and unknown deprecations are warnings. Direct findings point to the effective project or central version declaration when available.
- Semantic package diff classification for additions, removals, upgrades, downgrades, non-comparable version changes, and direct/transitive transitions.
- Diff schema version 2 summaries for package direction and PM007 vulnerability / PM008 deprecation findings introduced or resolved.
- GitHub Action `mode: auto|scan|diff`; `auto` compares unprivileged `pull_request` events with `github.event.pull_request.base.sha`, rejects misleading privileged `pull_request_target` analysis, and performs a normal scan for other events.
- GitHub Action controls for deprecation auditing and machine-readable outputs for package, vulnerability, and deprecation deltas.
- Dependency paths from every changed transitive package to its responsible direct root, including a deterministic canonical path and alternative direct roots when available.
- A structured `diff.impact` report with direct/transitive additions, upgrades, downgrades, dependency-kind transitions, source changes, signature evidence, causal paths, and maximum dependency blast radius.
- Repository-configurable Impact Gate policies for downgrade prevention, direct-to-transitive loss of control, total/transitive growth budgets, source changes, same-identity content-hash changes, HTTPS source allowlists, Package Source Mapping, and locked restore.
- `PMI001`–`PMI010` Impact Gate violation codes with review guidance and an independent diff failure gate.
- GitHub Action Impact Gate summaries and outputs for pass/fail, violations, added direct/transitive packages, maximum blast radius, package-source changes, and same-identity content changes.

### Changed

- Simulation baseline and candidate restores use separate tracked-file snapshots, NuGet/HTTP/plugin caches, CLI/user homes, application-data roots, and temporary directories; no baseline assets or cached package content is shared with the candidate.
- `diff` baseline and current restores use separate marker-owned NuGet package, HTTP, plugin, CLI-home, and temporary caches, preventing same-ID/version package content or provenance from leaking across the comparison.
- Candidate restore failures are classified conservatively: package/version absence, deterministic dependency conflicts, and locked-mode conflicts are valid rejection evidence; authentication, source availability, timeout, output-limit, unknown restore, evaluation, audit, snapshot, and cleanup failures remain incomplete exit code `2`.
- Dependency declaration mutation is guarded by canonical containment, regular-file/XML structure, expected element/package/metadata/current-version checks, source line, and SHA-256. Only the encoded version-value byte range changes, preserving BOM, encoding, newlines, comments, whitespace, quotes, and attribute order.
- GitHub Action `diff-base` remains an explicit override. The Action validates that comparison commits already exist locally and gives `fetch-depth: 0` guidance rather than fetching or mutating Git state.
- Package inventory now retains the effective declaration source file and line internally so source-backed audit diagnostics can produce useful annotations.
- Package inventory retains bounded NuGet restore provenance, content-hash, and signature-presence evidence when available; unsafe or credential-bearing sources are not treated as trusted metadata.
- PM007/PM008 diff identity follows package/advisory/project/framework semantics so version changes keep persistent risks persistent instead of falsely reporting them as resolved and reintroduced.
- Duplicate NU1901–NU1904 restore messages are coalesced when the equivalent structured PM007 advisory is present.
- PM002 treats exact NuGet-equivalent versions as equal and ignores intentionally disjoint target-framework scopes; target-graph matching prefers exact or compatible frameworks rather than ambiguous string prefixes.
- Transitive deprecation auditing remains opt-in in the GitHub Action to reduce noisy findings.
- Large assets graphs are parsed incrementally from streams, discovery results are reused across each analysis, edges are indexed once per target, and dependency paths propagate reachable direct roots through a bounded deterministic work queue.
- JSON report schema remains version 1 and configuration/baseline schemas remain version 1; the nested Git comparison contract alone advances to schema version 2.
- Package version ordering is performed locally with deterministic NuGet-compatible stable/prerelease precedence and never consults a registry.

### Safety

- PackageMedic 0.5 does not edit dependency files in the checkout: Time Machine mutates only one owned candidate snapshot, verifies the original worktree remains clean after both snapshots are removed, and never emits hypothetical SARIF or Action annotations.
- Time Machine requires an unambiguous exact direct/central declaration in a clean committed Git tree. Missing, transitive-only, dynamic, conditional, external, multiple, submodule-unmaterialized, and untracked/generated inputs fail closed before a verdict.
- Simulation subprocesses resolve `dotnet` and `git` from canonical absolute host `PATH` entries and reject repository- or snapshot-local executable shadowing. Temporary homes and caches are private to the current user on Unix-like hosts.
- Credential-bearing or query-qualified package-source URLs are not retained as provenance or accepted by source policy, and known URL secret parameters are redacted from operational errors.
- Candidate mutation integrity is rechecked after restore, and cleanup failures are preserved alongside the original operation failure instead of masking it.
- Tracked `.gitattributes` and repository-local `.git/info/attributes` `export-ignore` or `export-subst` rules are rejected for Time Machine and the exact commit used by `diff`, because they prevent a Git snapshot from proving byte-for-byte equivalence with committed input; extracted regular files retain Unix executable mode.
- Build, tests, and runtime compatibility are never claimed by the 0.5 simulator. Restore may execute repository-controlled MSBuild logic and contact configured feeds; filesystem snapshots and cleaned subprocess environments are integrity boundaries, not an operating-system sandbox.
- Vulnerability and deprecation audits are separate official SDK invocations because NuGet does not allow `--vulnerable` and `--deprecated` in the same command.
- Unknown or vendor-specific resolved version strings are reported as non-comparable changes instead of being guessed as upgrades or downgrades.
- Source allowlists accept only credential-free HTTPS URLs or the explicit `local` value; unknown provenance fails closed when an allowlist is configured.
- Package-cache provenance rejects symbolic-link/junction roots and metadata files, and query/fragment-qualified observed sources remain unknown instead of being collapsed into an allowlisted base URL.
- Persistent packages that lose previously observed source or content-hash evidence are now explicit source/content changes, so the default Impact Gate fails closed instead of silently treating weaker provenance as unchanged.
- Streaming assets parsing now bounds individual JSON tokens and high-cardinality package folders, sources, frameworks, libraries, targets, dependency edges, and restore diagnostics before graph materialization.
- Directory scans with one solution restore that solution before separately restoring projects omitted from it, avoiding concurrent writes when an omitted project is still reached transitively through a project reference.
- Subprocess executable resolution canonicalizes parent symlinks/junctions and both logical and physical analysis roots, preventing an external-looking `PATH` entry from redirecting execution into repository-controlled files.
- Git snapshot archives disable external `core.attributesFile` inheritance, so user-global attributes cannot silently alter the commit being compared.
- Diagnostic gating, SARIF, and annotations resolve PM007/PM008 through the same persistent risk identity used by diff summaries, preventing introduced risks from being dropped during report filtering.
- Dependency path materialization has explicit node, edge, segment, root-reachability, and traversal-operation limits; dense equal-depth alternatives no longer multiply stored paths.
- Required Package Source Mapping is evaluated from repository-owned effective sources and usable patterns, while required lockfiles must be bounded, structurally valid NuGet files inside the analysis root.
- Impact Gate policy is evaluated only from a complete baseline/current comparison. Incomplete analysis remains operational exit code `2` and never publishes a partial safe result.

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

- `package-medic doctor [path]` for projects, classic/XML solution files, and directories.
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
[0.5.0]: https://github.com/GonzMeza/package-medic/compare/v0.4.0...v0.5.0
[0.6.0]: https://github.com/GonzMeza/package-medic/compare/v0.5.0...v0.6.0
[Unreleased]: https://github.com/GonzMeza/package-medic/compare/v0.6.0...HEAD
