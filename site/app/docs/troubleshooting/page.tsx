import CodeBlock from "../code-block";
import { Callout, DocPage, PageLinks } from "../components";

const problems = [
  {
    title: "Exit code 2 after restore",
    cause: "Restore failed, timed out, or returned an unusable dependency graph.",
    action: "Run dotnet restore for the same target, inspect the sanitized NU/MSBuild evidence, verify feed access, and increase --restore-timeout only when the operation is legitimately slow.",
  },
  {
    title: "--no-restore reports missing assets",
    cause: "At least one selected project has no usable obj/project.assets.json.",
    action: "Run dotnet restore first or remove --no-restore. In diff mode, assets must be usable in both selected revisions.",
  },
  {
    title: "Audit cannot obtain vulnerability data",
    cause: "The active SDK, feed, or advisory source is unavailable or produced unsupported output.",
    action: "Check the installed SDK and NuGet sources, then run dotnet list package --vulnerable directly. PackageMedic returns 2 rather than claiming the graph is safe.",
  },
  {
    title: "Diff cannot find the Git reference",
    cause: "The reference is missing locally, unreachable, or CI checked out shallow history.",
    action: "Fetch the base reference. In GitHub Actions, use actions/checkout with fetch-depth: 0 before diff-base.",
  },
  {
    title: "Configuration or baseline is rejected",
    cause: "The file is malformed, contains an unknown property, escapes the repository boundary, or exceeds a size/count limit.",
    action: "Validate .packagemedic.json against the published schema, use repository-relative paths, and regenerate baselines with the matching tool version.",
  },
  {
    title: "SARIF is not visible in GitHub",
    cause: "Code Scanning is disabled or the workflow lacks security-events: write.",
    action: "Enable GitHub Code Security where required and grant the minimum SARIF permission. The action still retains annotations and artifacts when upload is unavailable.",
  },
  {
    title: "A project in the solution is reported missing",
    cause: "The solution references an absent file or a project outside the selected safe analysis root.",
    action: "Repair the solution reference or select the correct repository root. PackageMedic intentionally rejects partial solution scans.",
  },
  {
    title: "The scan is slow in a large repository",
    cause: "Restore, audit, or MSBuild evaluation dominates the workload.",
    action: "Use a solution or narrower directory, exclude generated trees, keep assets warm, and tune maxParallelism within available CPU and memory. Use --no-restore only with verified assets.",
  },
];

export default function TroubleshootingPage() {
  return (
    <DocPage
      eyebrow="Operational guide"
      title="Turn failures into useful evidence."
      description="PackageMedic fails closed when analysis is incomplete. These checks distinguish a dependency finding from a restore, configuration, audit, Git, or CI operational problem."
    >
      <Callout title="Start with detailed output">
        <p>
          Detailed verbosity adds progress and sanitized evidence without changing analysis or
          thresholds. Reproduce the same target and options locally before changing policy.
        </p>
      </Callout>

      <section id="first-checks">
        <h2>First checks</h2>
        <CodeBlock>{`dotnet --info
dotnet restore ./MySolution.sln
package-medic --version
package-medic doctor ./MySolution.sln --fail-on none --verbosity detailed`}</CodeBlock>
      </section>

      <section id="problems">
        <h2>Common problems</h2>
        <div className="docs-troubleshooting-list">
          {problems.map((problem, index) => (
            <details key={problem.title} open={index === 0}>
              <summary>{problem.title}<span aria-hidden="true">+</span></summary>
              <div>
                <p><strong>Likely cause:</strong> {problem.cause}</p>
                <p><strong>What to do:</strong> {problem.action}</p>
              </div>
            </details>
          ))}
        </div>
      </section>

      <section id="report">
        <h2>When reporting a PackageMedic bug</h2>
        <ul>
          <li>Include PackageMedic and <code>dotnet --info</code> versions.</li>
          <li>Provide the smallest safe project or repository structure that reproduces the issue.</li>
          <li>Include the exact command, exit code, and sanitized detailed output.</li>
          <li>State whether restore, audit, configuration, baseline, or Git diff was enabled.</li>
          <li>Remove private feed URLs, usernames, tokens, and proprietary package names when necessary.</li>
        </ul>
        <p>
          Use a public GitHub issue for normal bugs and private vulnerability reporting for suspected
          security issues.
        </p>
      </section>

      <PageLinks
        previous={{ href: "/docs/security", label: "Safety & security" }}
        next={{ href: "/docs", label: "Documentation overview" }}
      />
    </DocPage>
  );
}
