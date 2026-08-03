import Link from "next/link";
import { product } from "../product";
import CodeBlock from "./code-block";
import { Callout, DocPage, PageLinks } from "./components";

const paths = [
  {
    href: "/docs/getting-started",
    number: "01",
    title: "Run the first scan",
    copy: "Install the global tool, select a project or solution, and understand the result.",
  },
  {
    href: "/docs/configuration",
    number: "02",
    title: "Define team policy",
    copy: "Commit one configuration, tune rules, and document intentional exceptions.",
  },
  {
    href: "/docs/baselines",
    number: "03",
    title: "Adopt gradually",
    copy: "Keep existing findings visible while CI rejects only new dependency problems.",
  },
  {
    href: "/docs/github-action",
    number: "04",
    title: "Automate pull requests",
    copy: "Publish annotations, a job summary, JSON, SARIF, and optional Code Scanning results.",
  },
];

export default function DocsOverview() {
  return (
    <DocPage
      eyebrow={`Documentation · v${product.version}`}
      title="Diagnose the graph with confidence."
      description="Everything needed to install PackageMedic, choose a workflow, define repository policy, and integrate the result into CI."
    >
      <Callout title="PackageMedic 0.4 is read-only" tone="success">
        <p>
          Commands inspect dependency evidence and produce reports. They do not rewrite project,
          props, lock, or assets files. Even <code>clean</code> only supports a dry-run plan.
        </p>
      </Callout>

      <section id="quick-start">
        <h2>Quick start</h2>
        <p>
          Install the stable .NET tool and point <code>doctor</code> at a project, solution,
          <code>.slnx</code> file, or directory. Omitting the path scans the current directory.
        </p>
        <CodeBlock>{`dotnet tool install --global PackageMedic.Tool --version ${product.version}
package-medic doctor ./MySolution.sln`}</CodeBlock>
      </section>

      <section id="choose-workflow">
        <h2>Choose a workflow</h2>
        <div className="docs-path-grid">
          {paths.map((path) => (
            <Link href={path.href} key={path.href}>
              <span>{path.number}</span>
              <h3>{path.title}</h3>
              <p>{path.copy}</p>
              <strong>Open guide →</strong>
            </Link>
          ))}
        </div>
      </section>

      <section id="capabilities">
        <h2>What 0.4 can inspect</h2>
        <div className="docs-capability-grid">
          <article><strong>Dependency policy</strong><p>Unused central versions, drift, CPM bypasses, duplicates, floating versions, and restore problems.</p></article>
          <article><strong>Resolved inventory</strong><p>Direct and transitive packages by project, framework, runtime identifier, and dependency kind.</p></article>
          <article><strong>Known vulnerabilities</strong><p>Official NuGet audit evidence, advisory URL, severity, target framework, and direct/transitive context.</p></article>
          <article><strong>Git graph changes</strong><p>Added, resolved, or worsened diagnostics plus package-version, dependency-kind, and CPM changes.</p></article>
          <article><strong>Repository policy</strong><p>Configuration, exclusions, justified suppressions, portable baselines, and new-only gates.</p></article>
          <article><strong>CI-ready reports</strong><p>Readable text, stable JSON, deterministic SARIF, GitHub annotations, summaries, and artifacts.</p></article>
        </div>
      </section>

      <section id="mental-model">
        <h2>How a scan works</h2>
        <ol className="docs-steps">
          <li><span>1</span><div><strong>Discover</strong><p>Resolve the selected projects without leaving the analysis root.</p></div></li>
          <li><span>2</span><div><strong>Restore and evaluate</strong><p>Use the active .NET SDK, MSBuild model, imports, conditions, and target frameworks.</p></div></li>
          <li><span>3</span><div><strong>Read the graph</strong><p>Inspect NuGet&apos;s resolved <code>project.assets.json</code> evidence.</p></div></li>
          <li><span>4</span><div><strong>Apply policy</strong><p>Classify rules, suppressions, baselines, and configured failure thresholds.</p></div></li>
          <li><span>5</span><div><strong>Report</strong><p>Return text, JSON, SARIF, or a Git comparison with a stable exit code.</p></div></li>
        </ol>
      </section>

      <PageLinks next={{ href: "/docs/getting-started", label: "Getting started" }} />
    </DocPage>
  );
}
