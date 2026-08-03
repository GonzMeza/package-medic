# Architecture

PackageMedic separates the CLI contract from analysis so diagnostics can be tested without a terminal or a live restore.

1. `ProjectDiscovery` resolves the optional target and finds `.csproj`, `.sln`, and `.slnx` inputs in one filesystem pass while pruning dependency, build, coverage, report, and website-output directories. Recursive traversal skips symbolic links/junctions; inaccessible directories and missing solution projects make the analysis incomplete instead of being silently omitted.
2. `RestoreRunner` invokes `dotnet restore` unless disabled, captures NU diagnostics, and redacts credential-shaped output. Multiple independent restore targets use a bounded worker queue.
3. `MsBuildProjectEvaluator` asks the active SDK for evaluated properties and items as JSON. Projects use a bounded worker queue, target frameworks share one bounded process gate, and imported XML source locations are cached once per analysis, so imports, conditions, and Central Package Management participate without unbounded process or pending-task fan-out.
4. `AssetsFileReader` parses `project.assets.json` directly from a file stream, builds the structured direct/resolved/transitive inventory, and imports NuGet log entries.
5. `DiagnosticEngine` runs independent PM001–PM006 rules over normalized project models; the opt-in `VulnerabilityAuditRunner` delegates PM007 evidence to the SDK's official NuGet audit command.
6. `AnalysisPolicy` applies configured rule severity/enablement, exclusions, and justified suppressions without mutating the analyzed repository.
7. `BaselineMatcher` classifies the effective diagnostics as New or Existing and reports baseline entries that are now Resolved.
8. The CLI deterministically renders text, JSON, or SARIF and applies the all-diagnostic and new-only failure thresholds independently from the selected destination.
9. `diff` safely archives a canonical Git commit into an owned temporary directory, enforces archive, entry, single-file, expanded-size, free-space, and extraction-time boundaries, analyzes both graphs, compares portable identities/RID-aware inventory/CPM settings, and disposes the snapshot without touching the checkout. Comparison is all-or-nothing: an analysis error on either side produces an incomplete report with sanitized errors and no partial changes.

## Output boundary

Machine-readable serializers live in the core project so they can be tested independently from terminal and file I/O. JSON preserves its original fields while exposing report `schemaVersion: 1`, policy counts, suppression reasons, fingerprints, baseline states, RID-aware package inventory, project settings, and vulnerabilities. Diff output embeds its own versioned comparison object, completion state, and baseline/current analysis errors. Product version and document schema versions are intentionally independent. SARIF adds rule metadata, repository-relative locations, shared stable fingerprints, and baseline states without changing the diagnostic engine.

The CLI owns destinations. With no `--output`, it writes the selected format to standard output. With `--output`, JSON and SARIF stream into a temporary file in the destination directory before an atomic replacement, avoiding an additional report-sized string in memory. `--sarif-output` writes an additional SARIF document from the same `AnalysisResult`, allowing CI to obtain JSON and SARIF without repeating discovery, restore, MSBuild evaluation, or diagnostics. Progress continues to use standard error, and no report file is written implicitly.

The process is read-only with respect to dependency declarations. Restore and opt-in vulnerability audit are distinct, visible boundaries that can access configured NuGet feeds. PackageMedic implements no advisory HTTP client. MSBuild evaluation uses query switches and does not invoke build targets. Subprocess boundaries have validated configurable timeouts, bounded output capture, JSON-safe credential redaction, unsafe-control filtering, and process-tree termination on cancellation.

Repository-controlled configuration, baseline, solution, assets, imported XML source-location, and Action report files are size-checked before memory-intensive parsing. Policy collection counts and glob lengths are bounded as well, so hostile pull requests fail with an operational error instead of exhausting a CI runner.

## Policy and baseline boundary

`.packagemedic.json` uses its own versioned schema. The CLI resolves settings in the order CLI override, repository configuration, then defaults. Policy filtering occurs after one complete analysis, so JSON and SARIF cannot disagree. Suppressions require a reason and remain represented in report metadata.

Baseline documents also use schema version 1 and contain only deterministic, portable diagnostic identities. They do not contain timestamps or absolute repository paths. `baseline create` and `baseline update` write only the explicitly selected baseline destination; `clean --dry-run` writes no dependency files and version 0.4 has no apply path.

## False-positive policy

Rules consume effective evaluated MSBuild items. PackageMedic does not parse project declarations with regular expressions. XML is used only to recover source line numbers for already-evaluated items and to discover projects in solution formats. PM001 emits only when no affected evaluated project directly references the package and no transitive-pinning-enabled affected project resolves it.

## Runtime choice

The tool targets `net8.0` and permits compatible major-version runtime roll-forward, while `global.json` selects the .NET 9.0.308 SDK when available and allows a newer major SDK when it is not. This deliberately separates the minimum tool runtime from the SDK used to build PackageMedic and interpret a target repository. The active `dotnet` SDK must still support the target project's requested SDK and MSBuild query output.
