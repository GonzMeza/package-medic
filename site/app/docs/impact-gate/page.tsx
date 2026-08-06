import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

const impactConfiguration = `{
  "schemaVersion": 1,
  "impact": {
    "failOnDowngrade": true,
    "failOnDirectToTransitive": true,
    "maxAddedPackages": 40,
    "maxAddedTransitivePackages": 25,
    "failOnSourceChange": true,
    "failOnContentChange": true,
    "requirePackageSourceMapping": true,
    "requireLockedMode": true,
    "allowedSources": [
      "https://api.nuget.org/v3/index.json",
      "https://packages.example.com/v3/index.json"
    ]
  }
}`;

export default function ImpactGatePage() {
  return (
    <DocPage
      eyebrow="Pull-request intelligence"
      title="Know what a package update brings with it."
      description="The Dependency Impact Gate traces every changed transitive package to its direct cause, measures blast radius, and enforces source-trust and reproducibility policy before a pull request merges."
    >
      <section id="mental-model">
        <h2>From a flat diff to a causal review</h2>
        <p>
          A package update is rarely one line in the resolved graph. PackageMedic builds a
          deterministic shortest path from every changed transitive package to the direct
          dependency responsible for it, separately for each project, framework, and runtime.
        </p>
        <CodeBlock label="Causal dependency path">{`Contoso.Web 4.0.0
  -> Contoso.Transport 3.2.0
  -> Contoso.Json 2.1.0`}</CodeBlock>
        <p>
          When multiple direct packages reach the same transitive, one canonical path is selected
          and the other direct roots remain available in structured output. The maximum blast
          radius is the largest number of changed transitives attributed to one direct root.
        </p>
        <Callout title="Blast radius is a review signal" tone="info">
          <p>
            A large radius is not automatically unsafe. It tells the reviewer which direct update
            deserves attention and how much of the resolved graph it changed.
          </p>
        </Callout>
      </section>

      <section id="run">
        <h2>Run the gate</h2>
        <CodeBlock>{`package-medic diff origin/main .
package-medic diff origin/main . --format json --output artifacts/impact.json
package-medic diff origin/main . --audit --deprecated --include-transitive`}</CodeBlock>
        <p>
          The gate is evaluated only after both graphs complete. Exit code <code>0</code> means the
          diagnostic threshold and Impact Gate passed; <code>1</code> means either gate failed; and
          <code>2</code> means the comparison was operationally incomplete.
        </p>
        <Callout title="Independent from diagnostic thresholds" tone="warning">
          <p>
            <code>--fail-on none</code> disables the PM diagnostic threshold, not committed
            <code>impact</code> policy. Impact violations are intentionally not hidden by diagnostic
            suppressions.
          </p>
        </Callout>
      </section>

      <section id="policy">
        <h2>Define the repository boundary</h2>
        <CodeBlock label=".packagemedic.json">{impactConfiguration}</CodeBlock>
        <OptionTable
          headers={["Property", "Default", "Policy"]}
          rows={[
            [<code key="downgrade">failOnDowngrade</code>, "true", "Reject resolved package downgrades."],
            [<code key="kind">failOnDirectToTransitive</code>, "true", "Reject losing explicit control of a formerly direct dependency."],
            [<code key="packages">maxAddedPackages</code>, "unset", "Limit every package added by the comparison."],
            [<code key="transitive">maxAddedTransitivePackages</code>, "unset", "Limit dependency growth outside direct declarations."],
            [<code key="source-change">failOnSourceChange</code>, "true", "Reject a source change or loss/gain of source evidence for a persistent package."],
            [<code key="content-change">failOnContentChange</code>, "true", "Reject a SHA-512 change or loss/gain of hash evidence for the same package ID/version."],
            [<code key="mapping">requirePackageSourceMapping</code>, "false", "Require effective repository source mapping with a usable pattern for every resolved package."],
            [<code key="locked">requireLockedMode</code>, "false", "Require locked restore and a valid NuGet lock file inside the analysis root."],
            [<code key="sources">allowedSources</code>, "empty", "Allow only credential-free HTTPS sources or the explicit local value."],
          ]}
        />
      </section>

      <section id="trust">
        <h2>Source trust fails closed when requested</h2>
        <p>
          PackageMedic reads bounded source, content-hash, and signature-presence evidence from
          metadata produced by NuGet restore. It does not contact a private provenance service or
          expose credentials. Query- or fragment-qualified sources and metadata reached through a
          symbolic link or junction remain unknown rather than inheriting trust from a base URL.
          A persistent package losing previously observed source or hash evidence is itself a gated
          change, even without an explicit source allowlist.
          If an allowlist is active and a changed package&apos;s source cannot be
          established, the gate reports unknown provenance instead of assuming it is trusted.
        </p>
      </section>

      <section id="codes">
        <h2>Impact policy codes</h2>
        <OptionTable
          headers={["Code", "Condition"]}
          rows={[
            [<code key="PMI001">PMI001</code>, "Package downgrade."],
            [<code key="PMI002">PMI002</code>, "Direct dependency became transitive."],
            [<code key="PMI003">PMI003</code>, "Total added-package budget exceeded."],
            [<code key="PMI004">PMI004</code>, "Added-transitive budget exceeded."],
            [<code key="PMI005">PMI005</code>, "Package source changed."],
            [<code key="PMI006">PMI006</code>, "Source is unknown while an allowlist is active."],
            [<code key="PMI007">PMI007</code>, "Source is outside the allowlist."],
            [<code key="PMI008">PMI008</code>, "Multiple feeds lack effective repository Package Source Mapping."],
            [<code key="PMI009">PMI009</code>, "Locked restore is disabled or its in-repository NuGet lock file is missing or invalid."],
            [<code key="PMI010">PMI010</code>, "Same package ID/version has different SHA-512 content."],
          ]}
        />
      </section>

      <section id="action">
        <h2>Review it in GitHub</h2>
        <CodeBlock label="GitHub Actions YAML">{`- uses: actions/checkout@v6
  with:
    fetch-depth: 0

- uses: GonzMeza/package-medic@v0.6.1
  with:
    mode: auto
    config: .packagemedic.json
    audit: 'true'
    deprecated: 'true'`}</CodeBlock>
        <p>
          The job summary shows pass/fail status, dependency growth, source changes, maximum blast
          radius, and each failed policy with its causal path. Workflow outputs include
          <code>impact-gate-passed</code>, <code>impact-violations</code>,
          <code>impact-added-direct</code>, <code>impact-added-transitive</code>,
          <code>impact-max-blast-radius</code>, <code>impact-source-changes</code>, and
          <code>impact-content-changes</code>.
        </p>
        <p>
          Auto mode is for the unprivileged <code>pull_request</code> event. PackageMedic rejects
          <code>pull_request_target</code> because its default checkout would compare the trusted base
          against itself; do not execute an untrusted PR head with privileged secrets.
        </p>
      </section>

      <PageLinks
        previous={{ href: "/docs/baselines", label: "Baselines" }}
        next={{ href: "/docs/time-machine", label: "Time Machine" }}
      />
    </DocPage>
  );
}
