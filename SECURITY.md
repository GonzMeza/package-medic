# Security policy

PackageMedic publishes security fixes for the latest stable release line.

Please do not open public issues for suspected vulnerabilities or accidentally exposed credentials. Use GitHub's private vulnerability reporting for `GonzMeza/package-medic` when available. Include affected versions, reproduction steps, impact, and any proposed mitigation. Do not include live feed tokens or passwords; replace them with inert placeholders.

PackageMedic does not collect telemetry and implements no advisory service of its own. The default `doctor` workflow runs `dotnet restore`, and `audit`/`--audit` delegates to the active SDK's official NuGet vulnerability command; both operations may contact feeds configured by the user. Use `doctor --no-restore` without `--audit` when network access is not acceptable. `diff` applies the same restore/audit choice to both compared graphs.

The repository commits NuGet content-hash lockfiles and restores them in locked mode in CI. The website uses exact direct npm versions, an integrity-checked lockfile, installation with dependency lifecycle scripts disabled, registry-signature verification, known-vulnerability auditing, and automated dependency update checks. Third-party GitHub Actions are pinned to immutable commits and updated through Dependabot.

Project discovery does not follow nested symbolic links or junctions. When the CLI supplies an analysis root, direct targets and projects referenced by solution files must stay inside it without traversing reparse points. Inaccessible directories and missing solution projects produce operational errors rather than partial clean scans. Git snapshots treat tracked symlinks as inert files; reject non-canonical, traversing, or platform-ambiguous archive paths; and enforce archive, entry, single-file, expanded-size, free-space, and extraction-time boundaries.

Repository-controlled configuration, baseline, solution, NuGet assets, imported XML location, and GitHub Action report files are bounded before memory-intensive parsing. Configuration exclusion/suppression collections and pattern lengths are also capped. Oversized inputs fail closed with operational errors rather than continuing with partial analysis.

Only the latest stable patch release and the latest source revision receive security fixes.
