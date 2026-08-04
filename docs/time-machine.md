# Dependency Time Machine

Dependency Time Machine is PackageMedic 0.5's restore-validated dependency simulator. It answers a narrow question without editing dependency declarations, lock files, or restore assets in the checkout. An explicit `--output` path is the only intentional checkout write:

> What dependency graph and policy result would this repository produce if one exact package version changed?

```bash
package-medic simulate Example.Package --to 2.0.0 ./MySolution.sln
package-medic simulate Example.Package --to 2.0.0 . --audit --deprecated --format json
```

## Evidence boundary

The simulation verifies dependency restore and graph impact only:

- Restore: run independently for the committed baseline and candidate.
- Build: not run.
- Tests: not run.
- Runtime compatibility: not verified.

PackageMedic does not describe a passing result as compatible or safe. A pass means that the candidate restored, resolved exactly where expected, introduced no configured diagnostic threshold, and passed the Dependency Impact Gate in the observed environment.

Restore still evaluates repository-controlled MSBuild content and may contact configured package sources. The temporary filesystem boundary is not an operating-system sandbox.

## Independent snapshots

`simulate` requires a clean Git worktree, including untracked files. It then:

1. Resolves `HEAD` to one immutable commit.
2. Materializes two independently owned snapshots of that exact commit.
3. Restores and analyzes snapshot A as the baseline.
4. Locates one literal effective `PackageVersion`, `PackageReference Version`, or `VersionOverride` declaration.
5. Verifies the declaration's canonical path, XML structure, current value, source line, and SHA-256 precondition.
6. Changes only the encoded version-value bytes in snapshot B.
7. Restores and analyzes snapshot B, then compares its graph with snapshot A.
8. Deletes both snapshots and rechecks that the original worktree is still clean before emitting a report.

The XML editor prohibits DTDs and external entities and preserves the original encoding, BOM, newlines, comments, spacing, quotes, and attribute ordering. No project, props, lock, assets, index, branch, or tracked checkout file is edited.

The simulation refuses to guess when the package is missing, transitive-only, dynamically versioned, conditionally or multiply declared, imported from outside the snapshot, or dependent on untracked/generated input. Select a narrower `.csproj` or solution when a repository has multiple effective declarations.

Because snapshots must reproduce committed dependency inputs byte-for-byte, v0.5 also refuses tracked `.gitattributes` or repository-local `.git/info/attributes` rules that use `export-ignore` or `export-subst`. A selected target or solution that depends on unmaterialized submodule or Git LFS content fails incomplete rather than producing a pass.

## Isolated process state

Each snapshot has a separate temporary process environment. PackageMedic clears the inherited child environment and redirects these locations beneath the owned snapshot:

- `NUGET_PACKAGES`
- `NUGET_HTTP_CACHE_PATH`
- `NUGET_PLUGINS_CACHE_PATH`
- `DOTNET_CLI_HOME`
- `HOME` / `USERPROFILE` and application-data directories
- `TEMP`, `TMP`, and `TMPDIR`

Only a small non-secret host allowlist needed to locate and run .NET is inherited. Private-feed credentials are not inherited by default. Copy only a required variable explicitly:

```bash
package-medic simulate Contoso.Package --to 4.2.0 . \
  --credential-env VSS_NUGET_EXTERNAL_FEED_ENDPOINTS
```

`--credential-env` is repeatable. The variable must exist, its value is marked sensitive, and PackageMedic redacts it from child output. Repository-owned NuGet configuration still determines which sources restore may contact.

The credential is intentionally available to restore/MSBuild logic inside both snapshots; redaction cannot prevent repository-controlled code from reading or exfiltrating it. Use this option only for a trusted commit, with short-lived read-only tokens and a disposable runner whose outbound network access is restricted. Never provide secrets to simulations triggered by untrusted fork code.

## Lock files

Candidate restore respects `RestoreLockedMode` and the tracked `packages.lock.json`. PackageMedic does not silently relax locked mode or regenerate the original lock file.

When the candidate requires a new lock graph, the valid result is a rejection with:

```json
{
  "isComplete": true,
  "verdict": "reject",
  "verification": {
    "restore": "failed",
    "restoreFailureKind": "lockedModeConflict",
    "lockedMode": "enforced"
  }
}
```

This means the candidate requires an intentional lockfile update; it does not claim binary incompatibility.

## Verdicts and exit codes

| Verdict | Exit | Meaning |
|---|---:|---|
| `pass` | `0` | Restore and comparison completed; no configured rejection was observed. |
| `noChange` | `0` | The requested declaration is NuGet-equivalent, or no package, diagnostic, project-setting, provenance, or risk delta was observed. Visible deltas produce `pass`, not a misleading `noChange`. |
| `reject` | `1` | The simulation completed, but candidate restore or a configured diagnostic/Impact Gate rejected it. |
| `incomplete` | `2` | Snapshot, timeout, evaluation, audit, cleanup, or another operational failure prevented a conclusion. |

A candidate version absent from reachable configured feeds is a complete `reject`. Authentication failures, unavailable or misconfigured sources, and unknown restore failures are `incomplete`, because PackageMedic cannot prove that the candidate itself caused them. A package missing from the selected dependency graph is invalid input and returns `2` before a simulation report is produced.

Source-policy URLs must be credential-free HTTPS addresses without query strings or fragments (or the explicit `local` value). PackageMedic treats query- or fragment-qualified observed provenance as unknown rather than collapsing it into an allowlisted base URL, and redacts common URL secret parameters from errors.

## JSON report

```bash
package-medic simulate Example.Package --to 2.0.0 . \
  --format json --output artifacts/example-2.0.0.simulation.json
```

The independent schema is [`schemas/packagemedic-simulation.schema.json`](../schemas/packagemedic-simulation.schema.json). It separates:

- `repository`: immutable commit and portable target.
- `request`: requested package and exact candidate.
- `mutation`: the temporary declaration, byte-preserving result, and before/after SHA-256.
- `verification`: restore result and explicit non-executed evidence.
- `comparison`: diagnostic, package, risk, project-setting, and Impact Gate deltas.
- `rejectionReasons`: valid observations that rejected a candidate.
- `errors`: operational failures that made the simulation incomplete.

Reports contain no temporary paths or timestamps. Hypothetical results are intentionally not emitted as SARIF or uploaded by the GitHub Action in 0.5.

## Large repositories

The cost is approximately two independent snapshots and two restores, plus optional vulnerability/deprecation audits. Point the command at the narrowest representative `.sln`, `.slnx`, or `.csproj`.

Snapshot, archive, expanded bytes, entry count, individual file, free-space, extraction time, restore time, MSBuild time, subprocess output, parallelism, assets, XML, and dependency-graph traversal remain bounded. Exceeding a limit produces `incomplete`/exit `2`; PackageMedic never publishes a partial pass.
