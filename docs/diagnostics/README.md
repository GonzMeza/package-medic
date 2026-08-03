# PackageMedic diagnostic reference

## PM001 — UnusedCentralPackageVersion

Emitted when an effective evaluated `PackageVersion` is not referenced directly by any affected project. When `CentralPackageTransitivePinningEnabled` is true, a package present in the resolved graph counts as used. The MVP emits this at high confidence only after successful MSBuild evaluation and assets loading.

Review target-framework conditions and affected projects, then remove the central entry if it is genuinely stale.

## PM002 — PackageVersionDrift

Emitted for each affected non-CPM project when the same direct package has more than one explicit version across the scanned project set. `VersionOverride` is treated as the effective explicit version.

Align explicit versions or migrate the package to Central Package Management.

## PM003 — CentralPackageManagementBypass

Emitted when CPM is active and a `PackageReference` supplies `Version`. An explicit `VersionOverride` is considered intentional and is not reported as a bypass.

Move the shared version into `Directory.Packages.props`; use `VersionOverride` only for a deliberate project-level exception.

## PM004 — DuplicateCentralPackageVersion

Emitted when more than one effective `PackageVersion` item defines the same package in a project's evaluated scope, including conflicts introduced by imported props files.

Consolidate the entries into one unambiguous central version for that scope.

## PM005 — NuGetRestoreProblem

Wraps warning/error NU codes captured from `dotnet restore` or stored in `project.assets.json`. The original code is preserved in `originalCode`, and the diagnostic uses NuGet's severity.

Resolve the original NuGet issue using its code and evidence. A failed restore is also an operational error and returns exit code `2`.

## PM006 — FloatingPackageVersion

Emitted when an evaluated `PackageVersion`, `PackageReference Version`, or `VersionOverride` uses a documented NuGet floating pattern such as `*`, `1.*`, `1.2.*`, `1.2.3-*`, or `1.2.3-rc.*`.

Fixed versions and fixed ranges are not reported. Unresolved MSBuild expressions such as `$(PackageVersion)` are ignored rather than guessed, keeping the rule conservative. Pin an exact version or a deliberate fixed range when reproducible restores are required.
