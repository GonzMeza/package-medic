import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function CommandsPage() {
  return (
    <DocPage
      eyebrow="CLI reference"
      title="One tool, focused commands."
      description="Use doctor for policy, audit for known vulnerabilities, diff for graph changes, and the supporting commands for repeatable team adoption."
    >
      <section id="doctor">
        <h2><code>doctor</code></h2>
        <p>Runs the complete dependency diagnosis and optionally includes vulnerability evidence.</p>
        <CodeBlock>{`package-medic doctor [path] [options]
package-medic doctor . --format json --output artifacts/medic.json
package-medic doctor . --audit --include-transitive
package-medic doctor . --format json --sarif-output artifacts/medic.sarif`}</CodeBlock>
      </section>

      <section id="audit">
        <h2><code>audit</code></h2>
        <p>
          Requests official NuGet vulnerability data from the active SDK. Direct packages are
          included by default; add <code>--include-transitive</code> for the complete resolved graph.
        </p>
        <CodeBlock>{`package-medic audit ./MySolution.sln --include-transitive
package-medic audit . --format sarif --output artifacts/audit.sarif --fail-on error`}</CodeBlock>
      </section>

      <section id="diff">
        <h2><code>diff</code></h2>
        <p>
          Compares the working graph with a safely materialized reachable Git reference without
          switching the checkout. It reports diagnostic, package, dependency-kind, and CPM changes.
        </p>
        <CodeBlock>{`package-medic diff origin/main ./MySolution.sln
package-medic diff v0.1.0 . --format json --output artifacts/diff.json
package-medic diff origin/main . --audit --include-transitive --fail-on warning`}</CodeBlock>
        <Callout title="Diff has its own definition of new" tone="warning">
          <p>
            <code>--baseline</code> and <code>--fail-on-new</code> are rejected in diff mode. With
            <code>--no-restore</code>, usable assets files must exist in both revisions.
          </p>
        </Callout>
      </section>

      <section id="policy-commands">
        <h2>Policy and reference commands</h2>
        <OptionTable
          headers={["Command", "Purpose"]}
          rows={[
            [<code key="init">package-medic init [directory|file] [--force]</code>, "Create a starter .packagemedic.json without overwriting by default."],
            [<code key="create">package-medic baseline create [path] --output &lt;file&gt;</code>, "Capture reviewed current findings as a portable baseline."],
            [<code key="update">package-medic baseline update [path] [--baseline &lt;file&gt;]</code>, "Refresh an existing accepted state explicitly."],
            [<code key="rules">package-medic rules</code>, "List PM001–PM007 and their default severity."],
            [<code key="explain">package-medic explain PM007</code>, "Show the explanation and next action for one rule."],
            [<code key="clean">package-medic clean [path] --dry-run</code>, "Preview high-confidence cleanup candidates; never applies changes in 0.4."],
          ]}
        />
      </section>

      <section id="options">
        <h2>Common scan options</h2>
        <OptionTable
          headers={["Option", "Meaning"]}
          rows={[
            [<code key="config">--config &lt;path&gt; / --no-config</code>, "Select an explicit policy file or disable automatic discovery."],
            [<code key="baseline">--baseline &lt;path&gt;</code>, "Classify current findings against a portable baseline."],
            [<code key="restore">--no-restore</code>, "Use existing project.assets.json files instead of restoring."],
            [<code key="format">--format text|json|sarif</code>, "Select the primary output format."],
            [<code key="output">--output, -o &lt;path&gt;</code>, "Atomically write the primary report."],
            [<code key="sarif">--sarif-output &lt;path&gt;</code>, "Also write SARIF from the same analysis."],
            [<code key="fail">--fail-on none|warning|error</code>, "Gate all effective diagnostics."],
            [<code key="new">--fail-on-new none|warning|error</code>, "Gate only diagnostics absent from the baseline."],
            [<code key="audit">--audit / --include-transitive</code>, "Request official NuGet audit evidence and optionally transitive packages."],
            [<code key="timeout">--restore-timeout / --evaluation-timeout</code>, "Bound restore and per-MSBuild evaluation time from 1 to 3600 seconds."],
            [<code key="parallel">--max-parallelism &lt;1-32&gt;</code>, "Bound concurrent restore, audit, and MSBuild processes."],
            [<code key="verbosity">--verbosity quiet|normal|detailed</code>, "Control progress and evidence detail."],
          ]}
        />
      </section>

      <PageLinks
        previous={{ href: "/docs/getting-started", label: "Getting started" }}
        next={{ href: "/docs/configuration", label: "Configuration" }}
      />
    </DocPage>
  );
}
