import Link from "next/link";
import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function VerifiedExperimentsPage() {
  return (
    <DocPage
      eyebrow="Verified experiments"
      title="Verify the change in two immutable snapshots."
      description="Verified experiments extend graph comparison with ordered restore, build, and structured test evidence while keeping the original checkout unchanged."
    >
      <Callout title="Opt in to executable evidence" tone="warning">
        <p>
          PackageMedic 0.6 keeps verification disabled by default. Build and test levels execute
          repository-controlled MSBuild logic, analyzers, generators, adapters, and tests, so use
          them only in a disposable runner appropriate for the repository&apos;s trust level.
        </p>
      </Callout>

      <section id="levels">
        <h2>Choose the evidence level explicitly</h2>
        <p>
          Verification is opt-in for <code>diff</code> and <code>simulate</code>. PackageMedic runs
          the same requested stages for the baseline and candidate, in order; it never accepts a
          candidate failure without first establishing passing baseline evidence for that stage.
        </p>
        <OptionTable
          headers={["Option", "Stages", "What it establishes"]}
          rows={[
            [<code key="restore">--verify restore</code>, "restore", "Both dependency analyses completed in independent immutable snapshots."],
            [<code key="build">--verify build</code>, "restore → build", "Both snapshots also completed dotnet build with --no-restore."],
            [<code key="test">--verify test</code>, "restore → build → test", "Both snapshots also produced consistent structured TRX test evidence."],
          ]}
        />
        <CodeBlock>{`# Compare a Git base with the committed HEAD
package-medic diff origin/main . --verify restore

# Add build verification in Release configuration
package-medic diff origin/main . --verify build --verification-configuration Release --build-timeout 900

# Test implies restore and build
package-medic diff origin/main . --verify test --build-timeout 900 --test-timeout 1200

# Verify a Time Machine candidate against the same committed HEAD
package-medic simulate Example.Package --to 2.0.0 . --verify test`}</CodeBlock>
        <p>
          Timeouts are in seconds and apply per build target or test project. Values range from{" "}
          <code>1</code> to <code>3600</code>. The default configuration is <code>Release</code>;
          configuration names accept only letters, digits, dots, underscores, and hyphens.
        </p>
      </section>

      <section id="comparison">
        <h2>Interpret a comparative verdict</h2>
        <OptionTable
          headers={["Verdict", "Exit", "Meaning"]}
          rows={[
            [<code key="pass">pass</code>, <code key="pass-exit">0</code>, "The requested evidence passed on both sides and a dependency change was observed."],
            [<code key="no-change">noChange</code>, <code key="no-change-exit">0</code>, "The requested evidence passed, but no dependency change was observed."],
            [<code key="reject">reject</code>, <code key="reject-exit">1</code>, "The baseline passed and the candidate produced a deterministic restore, build, or test failure."],
            [<code key="incomplete">incomplete</code>, <code key="incomplete-exit">2</code>, "Missing, contradictory, unsafe, timed-out, or operational evidence prevents a trustworthy comparison."],
          ]}
        />
        <Callout title="Uncertainty wins over rejection">
          <p>
            A failing or incomplete baseline is not evidence of a regression. Missing test projects,
            unavailable or contradictory TRX, output limits, timeouts, authentication failures, and
            other infrastructure problems remain <code>incomplete</code>; they never become a pass.
          </p>
        </Callout>
      </section>

      <section id="test-evidence">
        <h2>Tests use bounded structured evidence</h2>
        <p>
          Test projects come from evaluated MSBuild <code>IsTestProject</code> metadata rather than
          filename or package heuristics. PackageMedic supports VSTest and Microsoft Testing
          Platform projects, runs <code>dotnet test</code> without another build or restore, and
          reconciles the process exit code with TRX counts and stable failed-test identities.
        </p>
        <p>
          Each project gets a private results directory that is removed with its snapshot. Raw TRX
          files are not a PackageMedic output or uploaded artifact. A Microsoft Testing Platform
          project must already be able to emit TRX; PackageMedic does not install report extensions
          into the repository.
        </p>
        <Callout title="Native MTP needs an explicit .NET 10 runner contract">
          <p>
            Native Microsoft Testing Platform selection requires a .NET 10 or newer SDK and an
            effective <code>global.json</code> whose <code>test.runner</code> is
            <code>Microsoft.Testing.Platform</code>. Earlier SDKs use the VSTest bridge. In the
            GitHub Action, set <code>dotnet-version: &apos;10.0.x&apos;</code> when the repository
            requires native MTP execution. The repository must also reference
            <code>Microsoft.Testing.Extensions.TrxReport</code>; without complete bounded TRX
            evidence, PackageMedic returns an incomplete verdict rather than trusting an exit code.
          </p>
        </Callout>
      </section>

      <section id="immutable-boundary">
        <h2>Know the execution boundary</h2>
        <ol className="docs-steps">
          <li><span>1</span><div><strong>Resolve commits</strong><p>Require a clean worktree, resolve the base and current HEAD to immutable commit IDs, and validate archive semantics.</p></div></li>
          <li><span>2</span><div><strong>Materialize twice</strong><p>Create independent baseline and candidate snapshots with separate NuGet, .NET, home, and temporary state.</p></div></li>
          <li><span>3</span><div><strong>Apply trusted policy</strong><p>Resolve repository configuration from the immutable base and apply that policy to both sides.</p></div></li>
          <li><span>4</span><div><strong>Run ordered stages</strong><p>Analyze first, then run only the explicitly requested build and test stages.</p></div></li>
          <li><span>5</span><div><strong>Clean before output</strong><p>Remove owned state, recheck the original worktree and HEAD, then write the requested reports.</p></div></li>
        </ol>
        <Callout title="Snapshot isolation is not a sandbox" tone="warning">
          <p>
            Restore, MSBuild, and tests can execute repository-controlled code with the caller&apos;s
            host permissions and network access. PackageMedic 0.6 does not provide a container mode,
            syscall isolation, or a security verdict for package code. Use a disposable, separately
            secured runner for code you do not trust.
          </p>
        </Callout>
      </section>

      <section id="github-action">
        <h2>Use the Action boundary deliberately</h2>
        <p>
          The Action accepts <code>verify</code>, <code>build-timeout</code>,
          {" "}<code>test-timeout</code>, and <code>verification-configuration</code> in diff mode. It
          exposes the verdict, regression flags, candidate test counts, incompleteness, and the
          optional provenance path as outputs.
        </p>
        <CodeBlock label="Action outputs">{`verification-status
build-regression
test-regression
tests-passed
tests-failed
tests-skipped
verification-incomplete
provenance-file`}</CodeBlock>
        <OptionTable
          headers={["Runner", "Action behavior"]}
          rows={[
            ["GitHub-hosted", "Build/test verification is allowed, but the workflow still controls permissions, secrets, and network access."],
            ["Self-hosted", <span key="self-hosted">Build/test verification is refused unless <code>allow-self-hosted-verification: &apos;true&apos;</code> is set explicitly.</span>],
            ["CLI", "There is no runner-type switch; the operator is responsible for the execution boundary."],
          ]}
        />
        <p>
          <code>allow-self-hosted-verification</code> is acknowledgement, not hardening. It does not
          isolate a persistent runner or make untrusted repository code safe. See the{" "}
          <Link href="/docs/github-action">GitHub Action guide</Link> for the stable workflow and
          permission model.
        </p>
      </section>

      <section id="sbom">
        <h2>Export the observed NuGet inventory as CycloneDX</h2>
        <CodeBlock>{`# Write only the CycloneDX document
package-medic sbom . --output artifacts/packagemedic.cdx.json

# Add CycloneDX to a normal analysis
package-medic doctor . --sbom-output artifacts/packagemedic.cdx.json
package-medic audit . --sbom-output artifacts/audit.cdx.json
package-medic diff origin/main . --sbom-output artifacts/current.cdx.json`}</CodeBlock>
        <p>
          The output is deterministic CycloneDX 1.7 JSON with portable project/framework/runtime
          contexts, NuGet package URLs, direct/transitive kind, and the canonical dependency paths
          retained by the analysis. The standalone <code>sbom</code> command requires{" "}
          <code>--output</code>; <code>--sbom-output</code> is available on <code>doctor</code>,
          {" "}<code>audit</code>, and <code>diff</code>, but not <code>simulate</code>. Diff exports the
          current or candidate side of the comparison.
        </p>
        <Callout title="Deliberately marked incomplete">
          <p>
            This is a NuGet dependency inventory, not a complete application SBOM. Its composition
            always declares itself incomplete because alternate graph edges, non-NuGet, native,
            build-time, operating-system, and runtime components are outside the current model. The
            standalone <code>sbom</code>{" "}command writes nothing when analysis is operationally
            incomplete; for supplemental output, always inspect the command exit and recorded
            analysis-error count rather than inferring completeness from the file&apos;s presence.
          </p>
        </Callout>
      </section>

      <section id="provenance">
        <h2>Bind completed evidence to the candidate commit</h2>
        <CodeBlock>{`package-medic diff origin/main . --verify test --provenance-output artifacts/packagemedic.intoto.json`}</CodeBlock>
        <p>
          <code>--provenance-output</code> is accepted only by verified <code>diff</code>. After a
          conclusive verdict, PackageMedic writes a deterministic in-toto Statement v1 whose
          subject is the immutable candidate Git commit. Its PackageMedic-specific predicate binds
          the baseline commit and deterministic comparison-report digest, then records the target,
          tool version, trusted configuration fingerprint, verification level, and verdict. The
          CycloneDX digest is included only when a complete resolved graph was available; a
          deterministic candidate restore rejection can therefore carry valid evidence without an
          SBOM claim.
        </p>
        <Callout title="Unsigned evidence, not SLSA provenance" tone="warning">
          <p>
            The file has no DSSE envelope or signature and makes no SLSA claim. A separate trusted
            system must authenticate or sign it before consumers can treat it as an attestation.
            Incomplete verification creates no provenance file.
          </p>
        </Callout>
      </section>

      <PageLinks
        previous={{ href: "/docs/time-machine", label: "Time Machine" }}
        next={{ href: "/docs/github-action", label: "GitHub Action" }}
      />
    </DocPage>
  );
}
