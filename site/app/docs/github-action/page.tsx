import Link from "next/link";
import { product } from "../../product";
import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function GitHubActionPage() {
  const workflow = `permissions:
  contents: read
  security-events: write

steps:
  - uses: actions/checkout@v6
    with:
      fetch-depth: 0
  - uses: GonzMeza/package-medic@v${product.version}
    with:
      path: .
      audit: 'true'
      include-transitive-audit: 'true'
      deprecated: 'true'
      include-transitive-deprecated: 'true'
      fail-on: warning
      annotations: new
      upload-sarif: 'true'
      upload-artifact: 'true'`;

  return (
    <DocPage
      eyebrow="Continuous integration"
      title="Put diagnostics beside the change."
      description="The official action performs one analysis, creates JSON and SARIF, annotates files, writes a job summary, uploads optional artifacts, and preserves the CLI exit code."
    >
      <section id="workflow">
        <h2>Recommended pull-request workflow</h2>
        <CodeBlock label="GitHub Actions YAML">{workflow}</CodeBlock>
        <Callout title="Use the minimum permissions">
          <p>
            <code>contents: read</code> is sufficient unless SARIF is uploaded. Code Scanning needs
            <code>security-events: write</code>; private repositories may also require
            <code>actions: read</code> and GitHub Code Security.
          </p>
        </Callout>
      </section>

      <section id="inputs">
        <h2>Key inputs</h2>
        <OptionTable
          headers={["Input", "Default", "Purpose"]}
          rows={[
            [<code key="path">path</code>, <code key="dot">.</code>, "Project, solution, slnx, or directory inside GITHUB_WORKSPACE."],
            [<code key="version">tool-version</code>, product.version, "Exact PackageMedic.Tool version; wildcards are rejected."],
            [<code key="restore">restore</code>, "true", "Restore before analysis."],
            [<code key="config">config</code>, "unset", "Repository-relative PackageMedic configuration."],
            [<code key="baseline">baseline</code>, "unset", "Repository-relative portable baseline."],
            [<code key="mode">mode</code>, "auto", "Use unprivileged pull_request diff automatically, or force scan / diff."],
            [<code key="fail">fail-on / fail-on-new</code>, "unset", "Override policy thresholds when supplied."],
            [<code key="audit">audit</code>, "false", "Request official NuGet vulnerability evidence."],
            [<code key="transitive">include-transitive-audit</code>, "true", "Include transitive packages after audit is enabled."],
            [<code key="deprecated">deprecated</code>, "false", "Request official NuGet deprecation evidence."],
            [<code key="transitive-deprecated">include-transitive-deprecated</code>, "false", "Include transitive deprecated packages."],
            [<code key="diff">diff-base</code>, "unset", "Reachable Git ref that overrides mode."],
            [<code key="verify">verify</code>, "unset", "In diff mode, require immutable restore, build, or test evidence."],
            [<code key="build-timeout">build-timeout</code>, "900", "Bound each generated build target in seconds."],
            [<code key="test-timeout">test-timeout</code>, "1200", "Bound each generated test project in seconds."],
            [<code key="verification-config">verification-configuration</code>, "Release", "Configuration shared by verified build and test stages."],
            [<code key="annotations">annotations</code>, "new", "Emit new, all, or no native file annotations."],
            [<code key="sarif">upload-sarif</code>, "true", "Upload deterministic SARIF to Code Scanning."],
            [<code key="artifact">upload-artifact</code>, "true", "Retain JSON and SARIF for 14 days."],
            [<code key="parallel">max-parallelism</code>, "automatic, up to 4", "Bound concurrent restore, audit, and MSBuild work."],
          ]}
        />
      </section>

      <section id="outputs">
        <h2>Outputs and isolation</h2>
        <p>
          Later steps can read <code>exit-code</code>, <code>json-file</code>, <code>sarif-file</code>,
          <code>errors</code>, <code>warnings</code>, <code>information</code>,
          package direction counts, PM007/PM008 introduced/resolved/persistent counts, <code>artifact-name</code>,
          and <code>sarif-category</code>. Every action invocation gets an
          isolated report directory, artifact name, and SARIF category so repeated scans cannot
          overwrite each other.
        </p>
        <p>
          Pull-request comparisons additionally expose <code>impact-gate-passed</code>,
          <code>impact-violations</code>, <code>impact-added-direct</code>,
          <code>impact-added-transitive</code>, <code>impact-max-blast-radius</code>, and
          <code>impact-source-changes</code>, and <code>impact-content-changes</code>. The job summary
          lists failed PMI policies and their causal dependency paths.
        </p>
        <p>
          Verified comparisons additionally expose <code>verification-status</code>, build/test
          regression flags, candidate test counts, <code>verification-incomplete</code>,
          <code>sbom-file</code>, <code>sbom-created</code>, <code>provenance-file</code>, and
          <code>provenance-created</code>.
        </p>
      </section>

      <section id="diff">
        <h2>Pull-request graph diff</h2>
        <CodeBlock label="GitHub Actions YAML">{`- uses: actions/checkout@v6
  with:
    fetch-depth: 0

- uses: GonzMeza/package-medic@v${product.version}
  with:
    audit: 'true'
    deprecated: 'true'
    fail-on: warning`}</CodeBlock>
        <p>
          Auto mode uses the base SHA for an unprivileged <code>pull_request</code> event and rejects
          <code>pull_request_target</code>, whose default checkout would compare the base against itself.
          Fetch enough history so the base already exists locally; PackageMedic never fetches or changes Git. <code>diff-base</code> is an explicit
          override. Diff rejects <code>baseline</code> and <code>fail-on-new</code> because the Git
          comparison already defines which findings are new.
        </p>
        <p>
          The same run evaluates the committed <code>impact</code> configuration. A policy failure
          returns exit code <code>1</code> even when diagnostic <code>fail-on</code> is
          <code>none</code>; an incomplete base or current graph returns <code>2</code>.
        </p>
        <p>
          PackageMedic 0.6 also implements opt-in immutable restore, build, and test comparison.
          Its runner policy and evidence outputs are documented under
          <Link href="/docs/verified-experiments"> Verified experiments</Link>.
        </p>
      </section>

      <section id="boundaries">
        <h2>Action security boundaries</h2>
        <ul>
          <li>The default tool source is exclusively NuGet.org.</li>
          <li>Offline local sources are accepted only inside the workspace or runner temporary directory.</li>
          <li>Scan, configuration, baseline, and output paths cannot escape their allowed roots.</li>
          <li>Report files are size-checked before JavaScript parses them.</li>
          <li>Custom artifact, category, and output values are validated before use.</li>
        </ul>
      </section>

      <PageLinks
        previous={{ href: "/docs/verified-experiments", label: "Verified experiments" }}
        next={{ href: "/docs/reports", label: "Reports" }}
      />
    </DocPage>
  );
}
