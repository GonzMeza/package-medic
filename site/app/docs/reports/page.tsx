import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function ReportsPage() {
  return (
    <DocPage
      eyebrow="Output contracts"
      title="Readable by people and pipelines."
      description="Choose text for local review, JSON for automation, or SARIF for code-scanning systems. Output format never changes the analysis or exit-code contract."
    >
      <section id="formats">
        <h2>Generate reports</h2>
        <CodeBlock>{`# Human-readable terminal output
package-medic doctor . --format text

# Deterministic JSON written atomically
package-medic doctor . --format json --output artifacts/medic.json

# SARIF 2.1.0 as the primary format
package-medic doctor . --format sarif --output artifacts/medic.sarif

# JSON and SARIF from one analysis
package-medic doctor . --format json \
  --output artifacts/medic.json \
  --sarif-output artifacts/medic.sarif`}</CodeBlock>
        <p>
          Progress is written to standard error, so redirected standard output remains valid JSON
          or SARIF. Destination directories are created and file output is replaced atomically.
        </p>
      </section>

      <section id="json">
        <h2>JSON schema version 1</h2>
        <p>
          The stable camel-cased document contains <code>version</code>, <code>target</code>, scan
          <code>summary</code>, <code>diagnostics</code>, and <code>analysisErrors</code>. PackageMedic
          0.4 also provides resolved <code>packages</code>, <code>projectSettings</code>,
          <code>vulnerabilities</code>, policy metadata, suppressed and resolved diagnostics, and a
          structured <code>diff</code> object in Git comparison mode.
        </p>
        <CodeBlock label="JSON shape">{`{
  "schemaVersion": 1,
  "version": "0.4.0",
  "target": "./MySolution.sln",
  "summary": { "errors": 0, "warnings": 1, "information": 0 },
  "analysisErrors": [],
  "packages": [],
  "projectSettings": [],
  "vulnerabilities": [],
  "diagnostics": []
}`}</CodeBlock>
      </section>

      <section id="sarif">
        <h2>SARIF 2.1.0</h2>
        <p>
          PM001–PM007 are declared as stable rules with help text and links. Results can include a
          repository-relative location, portable fingerprint, project, evidence, action,
          confidence, original NuGet code, advisory context, and baseline state.
        </p>
        <OptionTable
          headers={["PackageMedic", "SARIF level"]}
          rows={[
            ["information", <code key="note">note</code>],
            ["warning", <code key="warning">warning</code>],
            ["error", <code key="error">error</code>],
          ]}
        />
        <Callout title="Portable by construction" tone="success">
          <p>
            Absolute files outside the detected repository root are omitted. Reports add no
            timestamps, random identifiers, environment variables, feed configuration, or absolute
            repository paths.
          </p>
        </Callout>
      </section>

      <section id="determinism">
        <h2>Determinism</h2>
        <p>
          Given the same PackageMedic version, repository root, analysis result, and baseline, JSON
          and SARIF preserve the same rule, result, path, fingerprint, state, and property ordering.
          A diff SARIF includes current added or worsened findings only; resolved, package, and CPM
          changes remain available in text and JSON.
        </p>
      </section>

      <section id="exit-codes">
        <h2>Reports do not hide failures</h2>
        <p>
          Exit code <code>0</code> means the selected threshold was not reached, <code>1</code> means
          it was reached, and <code>2</code> means the analysis was operationally incomplete.
          <code>--fail-on none</code> disables only policy exit code <code>1</code>; it never converts
          an operational failure into success.
        </p>
      </section>

      <PageLinks
        previous={{ href: "/docs/github-action", label: "GitHub Action" }}
        next={{ href: "/docs/diagnostics", label: "Diagnostics" }}
      />
    </DocPage>
  );
}
