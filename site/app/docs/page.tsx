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
  {
    href: "/docs/impact-gate",
    number: "05",
    title: "Gate dependency impact",
    copy: "Trace causal package paths, measure blast radius, and enforce source-trust policy.",
  },
  {
    href: "/docs/time-machine",
    number: "06",
    title: "Simulate before editing",
    copy: "Restore-validate one exact package candidate in disposable snapshots without changing the checkout.",
  },
  {
    href: "/docs/verified-experiments",
    number: "07",
    title: "Run verified experiments",
    copy: "Review the 0.6 restore, build, test, CycloneDX, and evidence contracts.",
  },
];

export default function DocsOverview() {
  return (
    <DocPage
      eyebrow={`Documentation · v${product.version}`}
      title="Diagnose the graph with confidence."
      description="Everything needed to install PackageMedic, choose a workflow, define repository policy, and integrate the result into CI."
    >
      <Callout title="PackageMedic 0.6 leaves the checkout untouched" tone="success">
        <p>
          Commands inspect dependency evidence and produce reports. They do not rewrite project,
          props, lock, or assets files in the checkout. <code>simulate</code> changes only an owned
          disposable snapshot, and <code>clean</code> supports a dry-run plan only.
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
        <h2>What 0.6 can inspect</h2>
        <div className="docs-capability-grid">
          <article><strong>Dependency policy</strong><p>Unused central versions, drift, CPM bypasses, duplicates, floating versions, and restore problems.</p></article>
          <article><strong>Resolved inventory</strong><p>Direct and transitive packages by project, framework, runtime identifier, and dependency kind.</p></article>
          <article><strong>Known vulnerabilities</strong><p>Official NuGet audit evidence, advisory URL, severity, target framework, and direct/transitive context.</p></article>
          <article><strong>Deprecated packages</strong><p>Official NuGet reasons, critical-bug severity, dependency kind, and source-provided replacement guidance.</p></article>
          <article><strong>PR graph changes</strong><p>Added/removed packages, upgrades, downgrades, dependency-kind transitions, risk deltas, and CPM changes.</p></article>
          <article><strong>Dependency Impact Gate</strong><p>Causal paths, blast radius, source provenance, growth budgets, source mapping, and locked-restore policy.</p></article>
          <article><strong>Dependency Time Machine</strong><p>Exact-version restore simulation in two isolated snapshots with byte-preserving declaration edits.</p></article>
          <article><strong>Verified experiments</strong><p>Opt-in comparative restore, build, and bounded structured test evidence over immutable snapshots.</p></article>
          <article><strong>Portable evidence</strong><p>Deterministic CycloneDX 1.7 NuGet inventory and unsigned in-toto analysis statements.</p></article>
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
          <li><span>5</span><div><strong>Explain impact</strong><p>Trace changed transitives to their direct root and evaluate repository trust policy.</p></div></li>
          <li><span>6</span><div><strong>Report</strong><p>Return text, JSON, SARIF, or a Git comparison with a stable exit code.</p></div></li>
        </ol>
      </section>

      <PageLinks next={{ href: "/docs/getting-started", label: "Getting started" }} />
    </DocPage>
  );
}
