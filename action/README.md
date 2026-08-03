# PackageMedic GitHub Action

The action installs an exact `PackageMedic.Tool` version, performs one analysis, creates JSON and SARIF reports from that result, emits file annotations, writes a job summary, and only then returns PackageMedic's exit code. It runs on GitHub-hosted Linux, Windows, and macOS runners.

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
      restore: 'true'
      annotations: new
      upload-sarif: 'true'
      upload-artifact: 'true'
```

`security-events: write` is required only for SARIF upload. Private repositories also need GitHub Code Security enabled and may require `actions: read`. If SARIF upload is unavailable, the action warns without hiding the scan result; annotations, the job summary, and optional artifacts still work.

The default package source is exclusively `https://api.nuget.org/v3/index.json`. For offline validation, a local source is accepted only inside `GITHUB_WORKSPACE` or `RUNNER_TEMP`. `tool-version` must be exact: floating versions and wildcards are rejected. Scan targets must remain inside `GITHUB_WORKSPACE`; reports can only be written inside `GITHUB_WORKSPACE` or `RUNNER_TEMP`.

## Inputs

| Input | Default | Purpose |
| --- | --- | --- |
| `path` | `.` | Project, solution, or directory to scan |
| `tool-version` | `0.4.0` | Exact PackageMedic.Tool version |
| `dotnet-version` | `8.0.x` | SDK used by the action |
| `nuget-source` | NuGet.org | Exclusive feed used to install the tool |
| `restore` | `true` | Restore before the first scan |
| `fail-on` | unset | Optional `none`, `warning`, or `error`; configuration or CLI default (`warning`) applies when unset |
| `fail-on-new` | unset | Optional `none`, `warning`, or `error` threshold for new diagnostics |
| `config` | unset | Repository-relative PackageMedic configuration file |
| `baseline` | unset | Repository-relative PackageMedic baseline file |
| `audit` | `false` | Ask the active SDK/NuGet tooling for known vulnerabilities and emit PM007 |
| `include-transitive-audit` | `true` | Include transitive packages when `audit` is enabled |
| `diff-base` | unset | Reachable Git reference to compare with the checked-out graph |
| `verbosity` | `normal` | `quiet`, `normal`, or `detailed` |
| `max-parallelism` | automatic (up to 4) | Maximum concurrent restore, audit, and MSBuild processes (`1`-`32`) |
| `annotations` | `new` | `new`, `all`, or `none`; legacy `true`/`false` map to `all`/`none` |
| `upload-sarif` | `true` | Send SARIF to code scanning |
| `upload-artifact` | `true` | Retain JSON and SARIF for 14 days |
| `artifact-name` | `packagemedic-report` | Safe artifact base name; an invocation suffix is added |
| `category` | `packagemedic` | Safe SARIF category base; an invocation suffix is added |
| `output-directory` | runner temporary directory | Optional existing base directory for an isolated report folder |

## Outputs

`exit-code`, `json-file`, `sarif-file`, `errors`, `warnings`, `information`, `artifact-name`, and `sarif-category` are available to later steps. Exit code `0` means the threshold was not reached, `1` means it was reached, and `2` means an operational or configuration error occurred.

Every invocation receives its own report folder, artifact name, and SARIF category. This prevents repeated PackageMedic steps in one job from overwriting each other's reports or uploads. The CLI's `--sarif-output` option creates both public machine-readable formats from one analysis.

With a baseline, the recommended CI policy is `fail-on: none` plus `fail-on-new: warning`. Existing findings remain visible without blocking the pull request, while newly introduced warnings and errors can fail the check. The default annotation mode is `new`; choose `all` to retain the pre-0.3 behavior or `none` to disable workflow annotations. Reports produced by a pre-0.3 tool do not contain baseline metadata, so the action conservatively treats all of their diagnostics as new instead of silently hiding annotations. The job summary always reports new, existing, resolved, and policy-suppressed counts.

`config` and `baseline` are passed to the same PackageMedic analysis that writes JSON and SARIF. Both must identify existing files inside `GITHUB_WORKSPACE`; paths that escape through `..` or symbolic links are rejected before the tool runs.

`audit` delegates to the official `dotnet list package --vulnerable` command and can contact configured NuGet sources; the action does not implement an advisory client. `include-transitive-audit` has no effect until `audit` is enabled.

With `diff-base`, use `actions/checkout` with enough history (normally `fetch-depth: 0`) so the reference exists locally. Diff mode already selects added or worsened findings, so `baseline` and `fail-on-new` are rejected when `diff-base` is set. Package and CPM changes remain available in JSON and the job summary; SARIF contains current added/worsened diagnostics. The default `restore: 'true'` analyzes both graphs. With `restore: 'false'`, both revisions must contain usable tracked assets files; if either analysis fails, the Action reports an incomplete comparison, publishes no partial changes, and returns operational exit code `2`.

Artifact and category base names accept letters, numbers, dots, underscores, and hyphens. Custom `output-directory` values must identify an existing base directory; the action creates only its isolated child directory after verifying that the base stays within the workspace or runner temporary directory.
