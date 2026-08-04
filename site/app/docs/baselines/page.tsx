import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function BaselinesPage() {
  return (
    <DocPage
      eyebrow="Gradual adoption"
      title="Block regressions, not the rollout."
      description="A baseline records reviewed diagnostic fingerprints so established repositories can keep debt visible while failing only on newly introduced findings."
    >
      <section id="create">
        <h2>Create an accepted state</h2>
        <ol className="docs-steps compact">
          <li><span>1</span><div><strong>Scan without a failure gate</strong><p>Review the evidence behind every existing diagnostic.</p></div></li>
          <li><span>2</span><div><strong>Fix obvious problems</strong><p>Do not baseline findings that should be corrected immediately.</p></div></li>
          <li><span>3</span><div><strong>Create the baseline</strong><p>Commit the portable file with the repository policy.</p></div></li>
          <li><span>4</span><div><strong>Gate new findings</strong><p>Keep all existing findings visible while CI rejects new warnings and errors.</p></div></li>
        </ol>
        <CodeBlock>{`package-medic doctor . --fail-on none
package-medic baseline create . --output .packagemedic-baseline.json
package-medic doctor . \
  --baseline .packagemedic-baseline.json \
  --fail-on none \
  --fail-on-new warning`}</CodeBlock>
      </section>

      <section id="states">
        <h2>Diagnostic states</h2>
        <OptionTable
          headers={["State", "Meaning", "Typical action"]}
          rows={[
            [<strong key="new">New</strong>, "The fingerprint is absent from the selected baseline.", "Review and fix or explicitly accept."],
            [<strong key="existing">Existing</strong>, "The same accepted diagnostic is still present.", "Keep visible and schedule remediation."],
            [<strong key="resolved">Resolved</strong>, "A baseline entry is no longer present.", "Verify the change and refresh the baseline."],
          ]}
        />
        <p>
          Fingerprints are repository-portable, do not contain timestamps, and remain stable across
          source-line movement. JSON includes resolved entries; SARIF uses the standard
          <code>new</code> and <code>unchanged</code> baseline states.
        </p>
      </section>

      <section id="update">
        <h2>Refresh intentionally</h2>
        <CodeBlock>{`package-medic baseline update . \
  --baseline .packagemedic-baseline.json`}</CodeBlock>
        <Callout title="A baseline update is a policy decision" tone="warning">
          <p>
            Review the diff before committing it. Updating a baseline accepts the current findings;
            it is not a substitute for understanding why a new diagnostic appeared.
          </p>
        </Callout>
      </section>

      <section id="configuration">
        <h2>Centralize the gate</h2>
        <CodeBlock label=".packagemedic.json">{`{
  "schemaVersion": 1,
  "failOn": "none",
  "failOnNew": "warning",
  "baseline": ".packagemedic-baseline.json"
}`}</CodeBlock>
        <p>
          With these values committed, local and CI scans use the same new-only policy without
          repeating command-line options.
        </p>
      </section>

      <section id="limits">
        <h2>Baseline safety</h2>
        <p>
          Baselines are bounded to 64 MiB and 100,000 entries. Malformed, unknown, or oversized
          data fails with operational exit code <code>2</code> instead of producing a partial
          classification.
        </p>
      </section>

      <PageLinks
        previous={{ href: "/docs/configuration", label: "Configuration" }}
        next={{ href: "/docs/impact-gate", label: "Impact Gate" }}
      />
    </DocPage>
  );
}
