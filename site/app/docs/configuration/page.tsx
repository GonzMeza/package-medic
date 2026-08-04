import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

const configuration = `{
  "$schema": "https://raw.githubusercontent.com/GonzMeza/package-medic/main/schemas/packagemedic.schema.json",
  "schemaVersion": 1,
  "failOn": "none",
  "failOnNew": "warning",
  "baseline": ".packagemedic-baseline.json",
  "maxParallelism": 4,
  "exclude": ["**/bin/**", "**/obj/**"],
  "rules": {
    "PM006": { "enabled": true, "severity": "warning" },
    "PM007": { "enabled": true, "severity": "error" },
    "PM008": { "enabled": true, "severity": "warning" }
  },
  "suppressions": [
    {
      "rule": "PM003",
      "path": "src/Legacy/**",
      "package": "Example.Legacy",
      "reason": "Intentional exception tracked in issue 42"
    }
  ],
  "impact": {
    "failOnDowngrade": true,
    "failOnDirectToTransitive": true,
    "maxAddedPackages": 40,
    "maxAddedTransitivePackages": 25,
    "failOnSourceChange": true,
    "failOnContentChange": true,
    "requirePackageSourceMapping": true,
    "requireLockedMode": true,
    "allowedSources": ["https://api.nuget.org/v3/index.json"]
  },
  "timeouts": {
    "restoreSeconds": 300,
    "evaluationSeconds": 60
  }
}`;

export default function ConfigurationPage() {
  return (
    <DocPage
      eyebrow="Policy as code"
      title="Make every scan repeatable."
      description="Store PackageMedic policy beside the code so local runs and CI evaluate the same rules, severities, exclusions, and operational limits."
    >
      <section id="create">
        <h2>Create the configuration</h2>
        <CodeBlock>{"package-medic init"}</CodeBlock>
        <p>
          The CLI searches for <code>.packagemedic.json</code> from the selected target toward the
          repository root. Use <code>--config</code> for a different file or <code>--no-config</code>
          to disable discovery. CLI values win over configuration, which wins over safe defaults.
        </p>
      </section>

      <section id="example">
        <h2>Complete example</h2>
        <CodeBlock label=".packagemedic.json">{configuration}</CodeBlock>
      </section>

      <section id="properties">
        <h2>Configuration properties</h2>
        <OptionTable
          headers={["Property", "Accepted value", "Purpose"]}
          rows={[
            [<code key="schema">schemaVersion</code>, <code key="one">1</code>, "Required configuration contract version; independent of the product version."],
            [<code key="fail">failOn</code>, "none, warning, error", "Default threshold for all effective findings."],
            [<code key="new">failOnNew</code>, "none, warning, error", "Default threshold for findings absent from a baseline."],
            [<code key="baseline">baseline</code>, "Relative path", "Baseline resolved relative to this configuration file."],
            [<code key="exclude">exclude</code>, "Glob array", "Portable repository paths that should not be analyzed."],
            [<code key="rules">rules</code>, "PM001–PM008 map", "Enable a rule and optionally override its severity."],
            [<code key="supp">suppressions</code>, "Rule selectors", "Document intentional exceptions by path and/or exact package."],
            [<code key="impact">impact</code>, "Impact Gate object", "Set dependency growth, downgrade, source-trust, source-mapping, and locked-restore policy for diff."],
            [<code key="parallel">maxParallelism</code>, "1–32", "Maximum concurrent restore, audit, and MSBuild processes."],
            [<code key="timeouts">timeouts</code>, "1–3600 seconds", "Bound restore and per-project evaluation."],
          ]}
        />
      </section>

      <section id="impact">
        <h2>Impact policy is a separate gate</h2>
        <p>
          The <code>impact</code> object applies to complete Git comparisons. Downgrades,
          direct-to-transitive transitions, source changes, and loss/gain of source evidence fail by default. Package
          growth budgets, an allowlist, Package Source Mapping, and locked restore are opt-in so
          teams can adopt them deliberately. A SHA-512 content change under the same package
          ID/version identity is rejected by default because it deserves explicit supply-chain
          review.
        </p>
        <p>
          Allowed sources must be credential-free HTTPS URLs; <code>local</code> is supported only
          as an explicit value. With an active allowlist, unknown source metadata fails closed.
          Impact violations are not diagnostic suppressions: change the policy through review when
          the repository boundary genuinely changes.
        </p>
      </section>

      <section id="suppressions">
        <h2>Suppress intentionally, not invisibly</h2>
        <p>
          Every suppression requires a non-empty reason. A selector always names one rule and may
          narrow the match with a repository-relative path glob, an exact package ID, or both.
          Suppressed findings do not reach failure gates, but remain counted in report policy
          metadata and retain their reason in detailed text and JSON.
        </p>
        <Callout title="Prefer the narrowest selector" tone="warning">
          <p>
            Tie an exception to a rule, path, and package whenever possible. Use baselines for
            reviewed historical debt and suppressions for deliberate policy exceptions.
          </p>
        </Callout>
      </section>

      <section id="limits">
        <h2>Fail-closed input boundaries</h2>
        <p>
          Configuration is bounded to 1 MiB, 1,000 exclusions, 1,000 suppressions, and 4,096
          characters per glob. Invalid, oversized, unknown, or mistyped properties cause exit code
          <code>2</code>; PackageMedic never silently continues with partial policy.
        </p>
      </section>

      <PageLinks
        previous={{ href: "/docs/commands", label: "Commands" }}
        next={{ href: "/docs/baselines", label: "Baselines" }}
      />
    </DocPage>
  );
}
