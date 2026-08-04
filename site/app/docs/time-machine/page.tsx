import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";
import { simulationJsonExample } from "../simulation-example";

export default function TimeMachinePage() {
  return (
    <DocPage
      eyebrow="Dependency simulation"
      title="See the graph before changing the package."
      description="Dependency Time Machine restore-validates one exact package candidate in two independent snapshots, compares its dependency impact, and leaves the checkout untouched."
    >
      <section id="quick-start">
        <h2>Ask one precise what-if question</h2>
        <CodeBlock>{`package-medic simulate Example.Package --to 2.0.0 ./MySolution.sln

# Include official NuGet risk evidence
package-medic simulate Example.Package --to 2.0.0 . \
  --audit --deprecated --include-transitive --format json \
  --output artifacts/example-2.0.0.simulation.json`}</CodeBlock>
        <p>
          The package must already be an effective direct <code>PackageReference</code> or central
          <code>PackageVersion</code>. The candidate must be one exact NuGet version.
        </p>
      </section>

      <section id="evidence">
        <h2>Know exactly what a pass means</h2>
        <div className="docs-capability-grid compact">
          <article><strong>Restore</strong><p>Run independently for the committed baseline and candidate.</p></article>
          <article><strong>Dependency graph</strong><p>Compare versions, kinds, causal paths, risks, provenance, and Impact Gate policy.</p></article>
          <article><strong>Build</strong><p>Not run in 0.5.</p></article>
          <article><strong>Tests and runtime</strong><p>Not run or verified in 0.5.</p></article>
        </div>
        <Callout title="A pass is deliberately narrow" tone="warning">
          <p>
            It means the candidate restored, resolved exactly where expected, and passed the
            observed diagnostic and Impact Gate policy. It is not a compatibility or safety claim.
          </p>
        </Callout>
      </section>

      <section id="snapshots">
        <h2>Two snapshots, one immutable commit</h2>
        <ol className="docs-steps">
          <li><span>1</span><div><strong>Require a clean tree</strong><p>Resolve one immutable <code>HEAD</code> and reject tracked or untracked changes.</p></div></li>
          <li><span>2</span><div><strong>Build the baseline</strong><p>Materialize, restore, and analyze snapshot A.</p></div></li>
          <li><span>3</span><div><strong>Edit only the candidate</strong><p>Materialize snapshot B, verify the XML and SHA-256 precondition, then replace only the encoded version value.</p></div></li>
          <li><span>4</span><div><strong>Restore and compare</strong><p>Analyze B and apply the same diagnostics, risk evidence, and Dependency Impact Gate.</p></div></li>
          <li><span>5</span><div><strong>Clean and verify</strong><p>Delete owned snapshots without following links and recheck the original worktree before reporting.</p></div></li>
        </ol>
        <p>
          The editor preserves encoding, BOM, line endings, comments, spacing, quote style, and
          attribute order. It refuses dynamic, conditional, ambiguous, external, transitive-only,
          or unsafe XML declarations instead of guessing.
        </p>
      </section>

      <section id="verdicts">
        <h2>Verdicts and exit codes</h2>
        <OptionTable
          headers={["Verdict", "Exit", "Meaning"]}
          rows={[
            [<code key="pass">pass</code>, <code key="pass-exit">0</code>, "Restore and comparison completed with no observed rejection."],
            [<code key="no-change">noChange</code>, <code key="no-change-exit">0</code>, "The declaration is NuGet-equivalent, or no package, diagnostic, setting, provenance, or risk delta was observed."],
            [<code key="reject">reject</code>, <code key="reject-exit">1</code>, "The candidate restore or a configured diagnostic/Impact Gate rejected the change."],
            [<code key="incomplete">incomplete</code>, <code key="incomplete-exit">2</code>, "An operational failure prevented a trustworthy conclusion."],
          ]}
        />
        <p>
          A version missing from reachable configured feeds is a complete rejection. Authentication,
          unavailable-source, unknown-restore, timeout, extraction, evaluation, audit, cleanup, or
          infrastructure failures are incomplete and never become a pass.
        </p>
      </section>

      <section id="locks">
        <h2>Lock files stay authoritative</h2>
        <p>
          Candidate restore respects <code>RestoreLockedMode</code> and tracked
          <code>packages.lock.json</code> files. Time Machine never silently disables locked mode or
          rewrites the original lock file. A locked-mode conflict is reported as a complete
          rejection that requires an intentional lockfile update—not as binary incompatibility.
        </p>
      </section>

      <section id="private-feeds">
        <h2>Private-feed state is opt-in</h2>
        <p>
          Each snapshot receives separate NuGet package, HTTP, plugin, CLI-home, app-data, user-home,
          and temporary caches. Secret environment variables are not inherited automatically.
        </p>
        <CodeBlock>{`package-medic simulate Contoso.Package --to 4.2.0 . \
  --credential-env VSS_NUGET_EXTERNAL_FEED_ENDPOINTS \
  --credential-env CONTOSO_FEED_TOKEN`}</CodeBlock>
        <p>
          Every named variable must exist and is registered for output redaction. The option is
          repeatable. NuGet configuration still controls which sources restore may contact. Source
          policy accepts credential-free HTTPS URLs without queries or fragments, or the explicit
          <code>local</code> value.
        </p>
        <Callout title="A trusted commit is required" tone="warning">
          <p>
            Restore/MSBuild logic can read an explicitly inherited credential; redaction cannot
            stop repository code from exfiltrating it. Use short-lived read-only tokens only on a
            trusted commit and disposable runner, never on untrusted fork code.
          </p>
        </Callout>
      </section>

      <section id="json">
        <h2>A separate deterministic report contract</h2>
        <p>
          Simulation JSON schema version 1 separates repository, request, mutation, verification,
          comparison, rejection reasons, and operational errors. Reports contain no timestamps or
          temporary paths. Hypothetical results are not emitted as SARIF or uploaded by the GitHub
          Action in 0.5.
        </p>
        <CodeBlock label="Complete schema-v1 example">{simulationJsonExample}</CodeBlock>
      </section>

      <section id="large-repositories">
        <h2>Bounded for large repositories</h2>
        <p>
          Cost is approximately two independent snapshots and restores plus optional audits. Select
          the narrowest representative solution or project. Archive size, entries, expanded bytes,
          free space, extraction time, subprocess output, restore/evaluation time, graph traversal,
          assets, XML, and parallelism all have hard bounds; exceeding one returns incomplete.
        </p>
        <Callout title="Isolation is not an OS sandbox">
          <p>
            Restore and MSBuild still execute repository-controlled logic with the caller&apos;s host
            permissions and may contact configured feeds. Use a disposable CI runner or container
            when the repository itself is untrusted.
          </p>
        </Callout>
      </section>

      <PageLinks
        previous={{ href: "/docs/impact-gate", label: "Impact Gate" }}
        next={{ href: "/docs/github-action", label: "GitHub Action" }}
      />
    </DocPage>
  );
}
