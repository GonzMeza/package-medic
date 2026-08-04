import Link from "next/link";
import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function CommandsPage() {
  return (
    <DocPage
      eyebrow="CLI reference"
      title="One tool, focused commands."
      description="Use doctor for policy and optional deprecations, audit for known vulnerabilities, diff for PR graph changes, and supporting commands for repeatable adoption."
    >
      <section id="doctor">
        <h2><code>doctor</code></h2>
        <p>Runs the complete dependency diagnosis and optionally includes vulnerability evidence.</p>
        <CodeBlock>{`package-medic doctor [path] [options]
package-medic doctor . --format json --output artifacts/medic.json
package-medic doctor . --audit --include-transitive
package-medic doctor . --deprecated --include-transitive
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
          switching the checkout. It reports diagnostics, upgrades, downgrades, dependency-kind
          transitions, vulnerability/deprecation deltas, CPM changes, causal dependency paths,
          blast radius, source provenance, and the repository Impact Gate result.
        </p>
        <CodeBlock>{`package-medic diff origin/main ./MySolution.sln
package-medic diff v0.1.0 . --format json --output artifacts/diff.json
package-medic diff origin/main . --audit --deprecated --include-transitive --fail-on warning`}</CodeBlock>
        <Callout title="Diff has its own definition of new" tone="warning">
          <p>
            <code>--baseline</code> and <code>--fail-on-new</code> are rejected in diff mode. With
            <code>--no-restore</code>, usable assets files must exist in both revisions.
          </p>
        </Callout>
        <p>
          A complete diff also evaluates the <code>impact</code> policy from
          <code>.packagemedic.json</code>. That gate remains active when
          <code>--fail-on none</code> disables diagnostic threshold failures.
        </p>
      </section>

      <section id="simulate">
        <h2><code>simulate</code></h2>
        <p>
          Restore-validates one exact direct or centrally managed package version in two independent
          snapshots of the same clean <code>HEAD</code>. Dependency declarations, lock files, and
          restore assets in the checkout are never edited; only an explicit report
          <code>--output</code> may be written.
        </p>
        <CodeBlock>{`package-medic simulate <package-id> --to <exact-version> [path]
package-medic simulate Example.Package --to 2.0.0 ./MySolution.sln
package-medic simulate Example.Package --to 2.0.0 . --audit --deprecated --format json
package-medic simulate Contoso.Private --to 4.2.0 . --credential-env PRIVATE_FEED_TOKEN`}</CodeBlock>
        <Callout title="Restore evidence, not compatibility" tone="warning">
          <p>
            A pass proves the observed restore and graph policy only. Time Machine does not run a
            build, tests, or runtime verification, and it never labels a candidate safe or compatible.
          </p>
        </Callout>
        <p>
          The command requires a clean committed tree and refuses ambiguous, conditional, dynamic,
          external, or transitive-only declarations. See the <Link href="/docs/time-machine">complete
          Time Machine guide</Link> for verdicts, lock files, private feeds, and isolation details.
        </p>
      </section>

      <section id="policy-commands">
        <h2>Policy and reference commands</h2>
        <OptionTable
          headers={["Command", "Purpose"]}
          rows={[
            [<code key="init">package-medic init [directory|file] [--force]</code>, "Create a starter .packagemedic.json without overwriting by default."],
            [<code key="create">package-medic baseline create [path] --output &lt;file&gt;</code>, "Capture reviewed current findings as a portable baseline."],
            [<code key="update">package-medic baseline update [path] [--baseline &lt;file&gt;]</code>, "Refresh an existing accepted state explicitly."],
            [<code key="rules">package-medic rules</code>, "List PM001–PM008 and their default severity."],
            [<code key="explain">package-medic explain PM008</code>, "Show the explanation and next action for one rule."],
            [<code key="clean">package-medic clean [path] --dry-run</code>, "Preview high-confidence cleanup candidates; never applies changes in 0.5."],
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
            [<code key="audit">--audit / --deprecated / --include-transitive</code>, "Request official vulnerability or deprecation evidence and optionally transitive packages."],
            [<code key="split-audit">--include-transitive-audit / --include-transitive-deprecated</code>, "Enable transitive evidence for only one audit when both are active."],
            [<code key="credential-env">--credential-env &lt;NAME&gt;</code>, "Explicitly inherit and redact one private-feed variable in simulate; repeat as needed."],
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
