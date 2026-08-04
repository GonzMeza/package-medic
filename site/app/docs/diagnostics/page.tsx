import CodeBlock from "../code-block";
import { Callout, DocPage, PageLinks } from "../components";

const diagnostics = [
  {
    code: "PM001",
    name: "UnusedCentralPackageVersion",
    severity: "warning",
    meaning: "An effective PackageVersion is not referenced directly by any affected project.",
    nuance: "When CentralPackageTransitivePinningEnabled is true, a package in the resolved graph counts as used. The rule runs only after successful evaluation and assets loading.",
    action: "Review target-framework conditions and affected projects, then remove the central entry only when it is genuinely stale.",
  },
  {
    code: "PM002",
    name: "PackageVersionDrift",
    severity: "warning",
    meaning: "The same direct package has non-equivalent explicit versions in overlapping TFM scopes across affected non-CPM projects.",
    nuance: "Equivalent exact versions such as 1.0 and 1.0.0 and versions in disjoint TFM scopes are ignored. VersionOverride is treated as the effective explicit version.",
    action: "Align explicit versions or migrate the shared package to Central Package Management.",
  },
  {
    code: "PM003",
    name: "CentralPackageManagementBypass",
    severity: "warning",
    meaning: "CPM is active but a PackageReference supplies Version directly.",
    nuance: "An explicit VersionOverride is considered intentional and is not reported as a bypass.",
    action: "Move the shared version to Directory.Packages.props and reserve VersionOverride for deliberate project-level exceptions.",
  },
  {
    code: "PM004",
    name: "DuplicateCentralPackageVersion",
    severity: "error",
    meaning: "More than one effective PackageVersion defines the same package in a project scope.",
    nuance: "The conflict can originate in the main props file or an imported props file.",
    action: "Consolidate the entries into one unambiguous central version for that scope.",
  },
  {
    code: "PM005",
    name: "NuGetRestoreProblem",
    severity: "NuGet level",
    meaning: "Restore output or project.assets.json contains an important NU warning or error.",
    nuance: "The original code, such as NU1605, NU1107, or NU1109, is preserved. A failed restore is also operational exit code 2.",
    action: "Use the original NuGet code and PackageMedic evidence to resolve the dependency or restore conflict.",
  },
  {
    code: "PM006",
    name: "FloatingPackageVersion",
    severity: "warning",
    meaning: "PackageVersion, PackageReference Version, or VersionOverride uses a NuGet floating pattern.",
    nuance: "Examples include *, 1.*, 1.2.*, 1.2.3-*, and 1.2.3-rc.*. Fixed ranges are allowed; unresolved MSBuild expressions are not guessed.",
    action: "Pin an exact version or deliberate fixed range when reproducible restores are required.",
  },
  {
    code: "PM007",
    name: "VulnerablePackage",
    severity: "warning / error",
    meaning: "Official NuGet audit output reports an advisory for a resolved package.",
    nuance: "Low, moderate, and unknown map to warning; high and critical map to error. Evidence includes version, advisory URL, project, framework, and dependency kind.",
    action: "Review the advisory and validate a compatible non-vulnerable update or replacement.",
  },
  {
    code: "PM008",
    name: "DeprecatedPackage",
    severity: "warning / error",
    meaning: "Official NuGet deprecation output reports a resolved package as deprecated.",
    nuance: "Critical bugs map to error; legacy, other, and unknown reasons map to warning. Replacement package and range are preserved when the source supplies them.",
    action: "Review the reason and compatibility, then remove or migrate the package deliberately.",
  },
];

export default function DiagnosticsPage() {
  return (
    <DocPage
      eyebrow="Rule reference"
      title="Eight diagnostics, with evidence."
      description="PackageMedic rules are conservative, stable, and explain what was observed, where it applies, and what to review next."
    >
      <section id="inspect">
        <h2>Inspect rules from the terminal</h2>
        <CodeBlock>{`package-medic rules
package-medic explain PM001
package-medic explain PM008`}</CodeBlock>
        <p>
          Rule severity can be overridden or a rule can be disabled in
          <code>.packagemedic.json</code>. Suppressions require a reason and remain visible in policy
          metadata.
        </p>
      </section>

      <section id="catalog">
        <h2>Diagnostic catalog</h2>
        <div className="docs-diagnostic-list">
          {diagnostics.map((diagnostic) => (
            <article id={diagnostic.code.toLowerCase()} key={diagnostic.code}>
              <div className="docs-diagnostic-heading">
                <span>{diagnostic.code}</span>
                <div><h3>{diagnostic.name}</h3><small>{diagnostic.severity}</small></div>
              </div>
              <p>{diagnostic.meaning}</p>
              <dl>
                <div><dt>Detection detail</dt><dd>{diagnostic.nuance}</dd></div>
                <div><dt>Suggested action</dt><dd>{diagnostic.action}</dd></div>
              </dl>
            </article>
          ))}
        </div>
      </section>

      <Callout title="PM007 and PM008 are opt-in">
        <p>
          A normal <code>doctor</code> run does not request audit metadata. Use
          <code>audit</code> or <code>doctor --audit</code> for vulnerabilities and
          <code>doctor --deprecated</code> for deprecations; add <code>--include-transitive</code>
          for the complete graph. Failure to obtain official data is an operational error, never
          evidence that the graph is safe.
        </p>
      </Callout>

      <PageLinks
        previous={{ href: "/docs/reports", label: "Reports" }}
        next={{ href: "/docs/security", label: "Safety & security" }}
      />
    </DocPage>
  );
}
