# SARIF output

PackageMedic 0.2 can serialize every PM001–PM005 finding as deterministic SARIF 2.1.0 for code-scanning systems.

```console
package-medic doctor . --format sarif
package-medic doctor . --format sarif --output artifacts/packagemedic.sarif
package-medic doctor . --format json --output artifacts/packagemedic.json --sarif-output artifacts/packagemedic.sarif
```

`--format sarif` does not change analysis or exit-code behavior. `--fail-on` still controls whether findings return exit code `1`, and operational failures still return `2`. When `--output` is omitted, the complete SARIF document is written to standard output. `--sarif-output` adds a SARIF file to any primary format without running the analysis again. Progress remains on standard error.

## Mapping

The SARIF document contains one run whose tool driver is PackageMedic. PM001–PM005 are declared as rules with stable IDs, descriptions, default levels, help text, and links to the diagnostic reference.

| PackageMedic | SARIF level |
| --- | --- |
| information | `note` |
| warning | `warning` |
| error | `error` |

Each result can include:

- a repository-relative physical location and source line;
- a stable fingerprint used to correlate the same finding between scans;
- the affected project, evidence, suggested action, and confidence;
- the original NuGet code for PM005 findings.

Absolute files outside the detected repository root are deliberately omitted as locations. This prevents machine-specific paths from leaking into portable reports and avoids annotations that cannot resolve in the checked-out source tree.

## Determinism

For the same PackageMedic version, repository root, and analysis result, serialization produces the same rule order, result order, paths, fingerprints, and JSON structure. SARIF contains no timestamps or random identifiers.

## GitHub Code Scanning

GitHub accepts the supported subset of SARIF 2.1.0. The official PackageMedic action can upload the generated report, but the file is not GitHub-specific and can be retained as a normal CI artifact or consumed by another compatible platform.

See [GitHub's SARIF documentation](https://docs.github.com/en/code-security/concepts/code-scanning/sarif-files) and the [SARIF 2.1.0 OASIS standard](https://www.oasis-open.org/standard/sarifv2-1-os/).

## Safety

Generating SARIF never modifies project or dependency files. A report file is created only when `--output` or `--sarif-output` is explicitly supplied. Credential-shaped subprocess output is redacted before diagnostics are constructed, and the SARIF serializer does not add environment variables, feed configuration, or absolute repository paths.
