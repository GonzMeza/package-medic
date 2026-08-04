import CodeBlock from "../code-block";
import { Callout, DocPage, PageLinks } from "../components";

export default function SecurityPage() {
  return (
    <DocPage
      eyebrow="Trust boundaries"
      title="Read-only, bounded, and explicit."
      description="PackageMedic treats repositories, subprocesses, reports, and Git snapshots as untrusted inputs while keeping network behavior visible to the operator."
    >
      <section id="read-only">
        <h2>No checkout mutation</h2>
        <ul>
          <li>No project, props, lock, or assets file in the checkout is rewritten.</li>
          <li>No automatic package update or vulnerability remediation is applied.</li>
          <li><code>simulate</code> changes one validated version value only inside an owned disposable snapshot.</li>
          <li><code>clean</code> requires <code>--dry-run</code> and produces a review plan only.</li>
          <li>Reports are written only when an explicit output path is supplied.</li>
          <li>No telemetry is collected.</li>
        </ul>
      </section>

      <section id="network">
        <h2>Know when the network may be used</h2>
        <p>
          <code>doctor</code> runs <code>dotnet restore</code> by default, and restore can contact
          feeds from the active NuGet configuration. <code>--audit</code> and
          <code>--deprecated</code> delegate to separate official SDK/NuGet commands and can contact
          configured sources. <code>diff</code> gives baseline and current independent package,
          HTTP, plugin, CLI-home, and temporary caches. <code>simulate</code> performs two independent restores and any
          requested audits.
          PackageMedic implements no advisory HTTP client of its own.
        </p>
        <CodeBlock>{`# Analyze existing assets without restore or advisory requests
package-medic doctor ./MySolution.sln --no-restore

# Explicitly request official NuGet vulnerability evidence
package-medic audit ./MySolution.sln --include-transitive

# Explicitly request official NuGet deprecation evidence
package-medic doctor ./MySolution.sln --deprecated --include-transitive`}</CodeBlock>
      </section>

      <section id="processes">
        <h2>Subprocess and output controls</h2>
        <ul>
          <li>Restore, audit, and MSBuild processes run with bounded parallelism.</li>
          <li>Restore and evaluation timeouts are configurable and terminate the process tree.</li>
          <li>Captured subprocess output has hard bounds.</li>
          <li>Credential-shaped output is redacted and unsafe terminal controls are removed.</li>
          <li>Executable paths and parent links are canonicalized against logical and physical repository roots.</li>
          <li>Simulation child processes receive isolated NuGet, .NET CLI, home, app-data, and temporary directories.</li>
          <li>Private-feed variables are inherited only when explicitly named with <code>--credential-env</code>.</li>
          <li>Progress stays on standard error so machine-readable standard output remains valid.</li>
        </ul>
      </section>

      <section id="filesystem">
        <h2>Filesystem and Git snapshot controls</h2>
        <ul>
          <li>Discovery performs one bounded pass and does not follow nested symbolic links or junctions.</li>
          <li>Explicit targets and solution projects must stay inside the analysis root.</li>
          <li>Missing or inaccessible projects produce operational errors instead of a partial clean scan.</li>
          <li>Git archive paths are canonicalized; traversal, platform ambiguity, and unsafe links are rejected.</li>
          <li>Archives enforce entry, file, expanded-size, free-space, and extraction-time limits.</li>
          <li>Time Machine uses two snapshots of the same commit, validates source SHA-256, and rechecks the worktree after cleanup.</li>
        </ul>
      </section>

      <section id="input-limits">
        <h2>Memory-intensive inputs are bounded</h2>
        <div className="docs-capability-grid compact">
          <article><strong>Configuration</strong><p>1 MiB, 1,000 exclusions, 1,000 suppressions, and 4,096 characters per glob.</p></article>
          <article><strong>Baseline</strong><p>64 MiB and 100,000 accepted diagnostic entries.</p></article>
          <article><strong>Solutions and XML</strong><p>64 MiB with DTD processing prohibited and external resolution disabled.</p></article>
          <article><strong>NuGet assets</strong><p>512 MiB, with bounded individual tokens and package/source/graph collection counts.</p></article>
          <article><strong>Action reports</strong><p>256 MiB before the JavaScript action parses JSON.</p></article>
          <article><strong>Generated reports</strong><p>Streamed and atomically replaced to avoid large intermediate strings.</p></article>
        </div>
      </section>

      <section id="supply-chain">
        <h2>PackageMedic&apos;s own supply chain</h2>
        <p>
          The repository commits NuGet content-hash lockfiles and restores them in locked mode. The
          site pins exact direct npm versions, verifies registry HTTPS sources and SHA-512 integrity,
          installs with lifecycle scripts disabled in CI, audits known vulnerabilities and registry
          signatures, and pins third-party GitHub Actions to immutable commits.
        </p>
        <Callout title="Report vulnerabilities privately" tone="warning">
          <p>
            Use GitHub private vulnerability reporting for suspected PackageMedic vulnerabilities.
            Do not place live feed tokens or credentials in a public issue or reproduction.
          </p>
        </Callout>
      </section>

      <section id="dependency-source-policy">
        <h2>Enforce trust without hiding uncertainty</h2>
        <p>
          In <code>diff</code>, the Impact Gate can restrict changed packages to credential-free
          HTTPS sources, reject source changes, require effective repository Package Source Mapping for multi-feed
          projects, and require a valid in-repository lock file with locked restore. Provenance comes from
          bounded NuGet restore metadata; an active allowlist treats missing source evidence as a
          violation instead of assuming the package is trusted. A changed SHA-512 hash under the
          same package ID/version identity is also blocked by default with <code>PMI010</code>. Losing
          previously observed source or hash evidence is treated as a change, not as an unchanged package.
        </p>
        <Callout title="Evidence, not attestation">
          <p>
            Source, content hash, and signature-presence metadata strengthen review context. Query- or
            fragment-qualified sources and reparse-point-backed package metadata fail closed, but this evidence does
            not replace independent publisher verification or a cryptographic trust policy.
          </p>
        </Callout>
      </section>

      <section id="simulation-boundary">
        <h2>Snapshot isolation is not an OS sandbox</h2>
        <p>
          Time Machine isolates filesystem and cache state, preserves declaration bytes outside the
          selected version, refuses ambiguous edits, and never relaxes locked restore. Restore and
          MSBuild can still execute repository-controlled logic with the caller&apos;s host permissions
          and contact configured feeds.
        </p>
        <Callout title="Use a disposable runner for untrusted repositories" tone="warning">
          <p>
            A passing simulation is restore and graph evidence—not a build, test, runtime,
            compatibility, or package-safety attestation.
          </p>
        </Callout>
      </section>

      <PageLinks
        previous={{ href: "/docs/diagnostics", label: "Diagnostics" }}
        next={{ href: "/docs/troubleshooting", label: "Troubleshooting" }}
      />
    </DocPage>
  );
}
