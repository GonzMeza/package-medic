# Dependency Impact Gate

PackageMedic 0.6 turns a Git dependency diff into a reviewable impact decision. It answers three questions that a flat package list cannot:

1. Which direct dependency caused each changed transitive package?
2. How large is that direct dependency's blast radius?
3. Does the change still satisfy the repository's source-trust and reproducibility policy?

The gate is evaluated by `diff` after both dependency graphs complete successfully:

```console
package-medic diff origin/main . --format text
package-medic diff origin/main . --format json --output artifacts/impact.json
package-medic diff origin/main . --audit --deprecated --include-transitive
```

## Causal paths and blast radius

For every changed transitive package, PackageMedic builds a deterministic shortest path from a direct root through NuGet's resolved dependency graph. A path such as:

```text
Contoso.Web 4.0.0 -> Contoso.Transport 3.2.0 -> Contoso.Json 2.1.0
```

means that the direct `Contoso.Web` dependency is the reason the changed `Contoso.Json` package appears in that project, target framework, and runtime graph. When multiple direct dependencies reach the same package, the report selects a stable canonical path and records the alternative roots.

The maximum blast radius is the largest number of changed transitive packages attributed to one direct root in one project/framework/runtime graph. It is a review signal, not a claim that every changed package is unsafe.

## Configuration

Commit the policy in `.packagemedic.json`:

```json
{
  "$schema": "https://raw.githubusercontent.com/GonzMeza/package-medic/main/schemas/packagemedic.schema.json",
  "schemaVersion": 1,
  "impact": {
    "failOnDowngrade": true,
    "failOnDirectToTransitive": true,
    "maxAddedPackages": 40,
    "maxAddedTransitivePackages": 25,
    "failOnSourceChange": true,
    "failOnContentChange": true,
    "requirePackageSourceMapping": true,
    "requireLockedMode": true,
    "allowedSources": [
      "https://api.nuget.org/v3/index.json",
      "https://packages.example.com/v3/index.json"
    ]
  }
}
```

| Property | Default | Meaning |
| --- | --- | --- |
| `failOnDowngrade` | `true` | Reject a resolved package downgrade. |
| `failOnDirectToTransitive` | `true` | Reject losing explicit control of a formerly direct dependency. |
| `maxAddedPackages` | unset | Limit all packages added by the comparison. |
| `maxAddedTransitivePackages` | unset | Limit added transitive packages. |
| `failOnSourceChange` | `true` | Reject a source change or loss/gain of source evidence for a persistent package. |
| `failOnContentChange` | `true` | Reject a same-ID/version hash change or loss/gain of content-hash evidence. |
| `requirePackageSourceMapping` | `false` | Require effective repository-owned Package Source Mapping with a usable pattern for every resolved package when multiple sources are active. |
| `requireLockedMode` | `false` | Require locked restore and a bounded, structurally valid NuGet lock file inside the analysis root. |
| `allowedSources` | empty | Allow only these credential-free HTTPS sources; `local` is an explicit supported value. |

Package-source evidence comes from bounded regular metadata written by NuGet during restore. Query/fragment-qualified sources and metadata reached through symbolic links or junctions remain unknown instead of being collapsed into an approved base URL. Losing previously observed source or hash evidence on a persistent package is a gated change even without an allowlist. When `allowedSources` is non-empty, unknown provenance is also a policy failure rather than an implicit trust decision. Source URLs containing credentials are never accepted as allowlist entries.

Mapping validation applies `packageSources`, `packageSourceMapping`, `clear`, `add`, and `remove` declarations in repository configuration order. User- or machine-level files outside the analysis root cannot silently satisfy repository policy. Lock-file validation likewise rejects missing, malformed, oversized, external, or reparse-point paths.

## Violation codes

Impact violations are separate from PM001–PM008 diagnostics and suppressions. Their codes are stable review categories:

| Code | Condition |
| --- | --- |
| `PMI001` | Package downgrade. |
| `PMI002` | Direct dependency became transitive. |
| `PMI003` | Total added-package budget exceeded. |
| `PMI004` | Added-transitive budget exceeded. |
| `PMI005` | Package source changed. |
| `PMI006` | Package source is unknown while an allowlist is active. |
| `PMI007` | Package source is not allowed. |
| `PMI008` | Multiple sources are active without effective repository Package Source Mapping. |
| `PMI009` | Locked restore is disabled or its in-repository NuGet lock file is missing or invalid. |
| `PMI010` | The same package ID/version identity has different SHA-512 content. |

Each package-level violation includes the affected project/framework, package, responsible direct root, causal path when available, and a suggested review action.

## Exit codes and reports

- `0`: the comparison completed and both the diagnostic threshold and Impact Gate passed.
- `1`: a diagnostic threshold or an Impact Gate policy failed.
- `2`: configuration, Git materialization, restore, audit, or graph analysis was incomplete.

`--fail-on none` disables only the diagnostic threshold. It does not disable committed `impact` policy. PackageMedic never treats an incomplete comparison as a passing gate.

The JSON report exposes the gate under `diff.impact` with `gatePassed`, `summary`, `packages`, `violations`, and the effective `policy`. `summary.contentChanges` counts same-identity SHA-512 changes. SARIF remains focused on current PM001–PM008 source findings; dependency paths and policy violations are represented in text, JSON, and the GitHub Action job summary.

## GitHub Action

Auto mode selects the pull request base commit for an unprivileged `pull_request` workflow, provided it is available locally. The privileged `pull_request_target` event is rejected because its default checkout would compare the trusted base against itself:

```yaml
- uses: actions/checkout@v6
  with:
    fetch-depth: 0

- uses: GonzMeza/package-medic@v0.6.1
  with:
    mode: auto
    config: .packagemedic.json
    audit: 'true'
    deprecated: 'true'
```

The job summary includes gate status, dependency-growth counts, source/content changes, maximum blast radius, and failed policies with causal paths. Outputs include `impact-gate-passed`, `impact-violations`, `impact-added-direct`, `impact-added-transitive`, `impact-max-blast-radius`, `impact-source-changes`, and `impact-content-changes`.
