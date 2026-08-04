# PackageMedic GitHub Action

The action installs an exact `PackageMedic.Tool` version, performs one analysis, creates JSON and SARIF reports from that result, emits file annotations, writes a job summary, and only then returns PackageMedic's exit code. It runs on GitHub-hosted Linux, Windows, and macOS runners.

```yaml
permissions:
  contents: read
  security-events: write

steps:
  - uses: actions/checkout@v6
    with:
      fetch-depth: 0
  - uses: GonzMeza/package-medic@v0.5.0
    with:
      path: .
      audit: 'true'
      include-transitive-audit: 'true'
      deprecated: 'true'
      include-transitive-deprecated: 'true'
      fail-on: warning
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
| `tool-version` | `0.5.0` | Exact PackageMedic.Tool version |
| `dotnet-version` | `8.0.x` | SDK used by the action |
| `nuget-source` | NuGet.org | Exclusive feed used to install the tool |
| `restore` | `true` | Restore before the first scan |
| `fail-on` | unset | Optional `none`, `warning`, or `error`; configuration or CLI default (`warning`) applies when unset |
| `fail-on-new` | unset | Optional `none`, `warning`, or `error` threshold for new diagnostics |
| `config` | unset | Repository-relative PackageMedic configuration file |
| `baseline` | unset | Repository-relative PackageMedic baseline file |
| `mode` | `auto` | Use unprivileged `pull_request` diff automatically, or force `scan` / `diff` |
| `audit` | `false` | Ask the active SDK/NuGet tooling for known vulnerabilities and emit PM007 |
| `include-transitive-audit` | `true` | Include transitive packages when `audit` is enabled |
| `deprecated` | `false` | Ask the active SDK/NuGet tooling for deprecated packages and emit PM008 |
| `include-transitive-deprecated` | `false` | Include transitive packages when `deprecated` is enabled |
| `diff-base` | unset | Reachable Git reference that overrides `mode` |
| `verbosity` | `normal` | `quiet`, `normal`, or `detailed` |
| `max-parallelism` | automatic (up to 4) | Maximum concurrent restore, audit, and MSBuild processes (`1`-`32`) |
| `annotations` | `new` | `new`, `all`, or `none`; legacy `true`/`false` map to `all`/`none` |
| `upload-sarif` | `true` | Send SARIF to code scanning |
| `upload-artifact` | `true` | Retain JSON and SARIF for 14 days |
| `artifact-name` | `packagemedic-report` | Safe artifact base name; an invocation suffix is added |
| `category` | `packagemedic` | Safe SARIF category base; an invocation suffix is added |
| `output-directory` | runner temporary directory | Optional existing base directory for an isolated report folder |

## Outputs

`exit-code`, report paths, severity counts, artifact identifiers, and diff counts are available to later steps. Diff outputs include packages added/removed/upgraded/downgraded, vulnerabilities and deprecations introduced/resolved/persistent, and the dependency Impact Gate. `impact-gate-passed` is `true` or `false` when a diff contains an impact report and is empty for a normal scan or an older tool. The related numeric outputs are `impact-violations`, `impact-added-direct`, `impact-added-transitive`, `impact-max-blast-radius`, `impact-source-changes`, and `impact-content-changes`. The last two also count loss/gain of source or content-hash evidence for persistent packages; the default policies fail closed on those changes. Exit code `0` means all active gates passed, `1` means a diagnostic or impact policy threshold was reached, and `2` means an operational or configuration error occurred.

Every invocation receives its own report folder, artifact name, and SARIF category. This prevents repeated PackageMedic steps in one job from overwriting each other's reports or uploads. The CLI's `--sarif-output` option creates both public machine-readable formats from one analysis.

With a baseline, the recommended CI policy is `fail-on: none` plus `fail-on-new: warning`. Existing findings remain visible without blocking the pull request, while newly introduced warnings and errors can fail the check. The default annotation mode is `new`; choose `all` to retain the pre-0.3 behavior or `none` to disable workflow annotations. Reports produced by a pre-0.3 tool do not contain baseline metadata, so the action conservatively treats all of their diagnostics as new instead of silently hiding annotations. The job summary always reports new, existing, resolved, and policy-suppressed counts.

`config` and `baseline` are passed to the same PackageMedic analysis that writes JSON and SARIF. Both must identify existing files inside `GITHUB_WORKSPACE`; paths that escape through `..` or symbolic links are rejected before the tool runs.

`audit` and `deprecated` delegate to separate official `dotnet list package --vulnerable` and `--deprecated` commands and can contact configured NuGet sources; the action does not implement an advisory client. Their transitive switches have no effect until the corresponding audit is enabled.

In `mode: auto`, unprivileged `pull_request` events compare against `github.event.pull_request.base.sha`; push and manual events run a normal scan. `pull_request_target` is rejected because its default checkout is the trusted base branch and would otherwise compare the base against itself. Do not check out and execute an untrusted PR head in that privileged event. Use `actions/checkout` with `fetch-depth: 0` in a normal `pull_request` workflow so the base commit exists locally. PackageMedic validates the commit but never fetches it. `diff-base` explicitly selects another reachable ref and overrides `mode`. Diff mode rejects `baseline` and `fail-on-new`, reports package/CPM/risk changes and causal impact paths in JSON and the job summary, and places only current added/worsened diagnostics in SARIF. The summary shows at most 20 Impact Gate violations; the JSON artifact retains the complete report. With `restore: 'false'`, both revisions must contain usable tracked assets files; otherwise the comparison fails closed with exit code `2`.

Artifact and category base names accept letters, numbers, dots, underscores, and hyphens. Custom `output-directory` values must identify an existing base directory; the action creates only its isolated child directory after verifying that the base stays within the workspace or runner temporary directory.
