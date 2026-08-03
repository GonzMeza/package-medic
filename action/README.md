# PackageMedic GitHub Action

The action installs an exact `PackageMedic.Tool` version, performs one analysis, creates JSON and SARIF reports from that result, emits file annotations, writes a job summary, and only then returns PackageMedic's exit code. It runs on GitHub-hosted Linux, Windows, and macOS runners.

```yaml
permissions:
  contents: read
  security-events: write

steps:
  - uses: actions/checkout@v6
  - uses: GonzMeza/package-medic@v0.2.0
    with:
      path: .
      fail-on: warning
      restore: 'true'
      annotations: 'true'
      upload-sarif: 'true'
      upload-artifact: 'true'
```

`security-events: write` is required only for SARIF upload. Private repositories also need GitHub Code Security enabled and may require `actions: read`. If SARIF upload is unavailable, the action warns without hiding the scan result; annotations, the job summary, and optional artifacts still work.

The default package source is exclusively `https://api.nuget.org/v3/index.json`. For offline validation, a local source is accepted only inside `GITHUB_WORKSPACE` or `RUNNER_TEMP`. `tool-version` must be exact: floating versions and wildcards are rejected. Scan targets must remain inside `GITHUB_WORKSPACE`; reports can only be written inside `GITHUB_WORKSPACE` or `RUNNER_TEMP`.

## Inputs

| Input | Default | Purpose |
| --- | --- | --- |
| `path` | `.` | Project, solution, or directory to scan |
| `tool-version` | `0.2.0` | Exact PackageMedic.Tool version |
| `dotnet-version` | `8.0.x` | SDK used by the action |
| `nuget-source` | NuGet.org | Exclusive feed used to install the tool |
| `restore` | `true` | Restore before the first scan |
| `fail-on` | `warning` | `none`, `warning`, or `error` |
| `verbosity` | `normal` | `quiet`, `normal`, or `detailed` |
| `annotations` | `true` | Emit workflow annotations |
| `upload-sarif` | `true` | Send SARIF to code scanning |
| `upload-artifact` | `true` | Retain JSON and SARIF for 14 days |
| `artifact-name` | `packagemedic-report` | Safe artifact base name; an invocation suffix is added |
| `category` | `packagemedic` | Safe SARIF category base; an invocation suffix is added |
| `output-directory` | runner temporary directory | Optional existing base directory for an isolated report folder |

## Outputs

`exit-code`, `json-file`, `sarif-file`, `errors`, `warnings`, `information`, `artifact-name`, and `sarif-category` are available to later steps. Exit code `0` means the threshold was not reached, `1` means it was reached, and `2` means an operational or configuration error occurred.

Every invocation receives its own report folder, artifact name, and SARIF category. This prevents repeated PackageMedic steps in one job from overwriting each other's reports or uploads. The CLI's `--sarif-output` option creates both public machine-readable formats from one analysis.

Artifact and category base names accept letters, numbers, dots, underscores, and hyphens. Custom `output-directory` values must identify an existing base directory; the action creates only its isolated child directory after verifying that the base stays within the workspace or runner temporary directory.
