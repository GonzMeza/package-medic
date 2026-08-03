"use client";

/* eslint-disable @next/next/no-img-element */
import { useState } from "react";
import {
  assetPath,
  baselineCommand,
  doctorCommand,
  initCommand,
  installCommand,
  newOnlyCommand,
  nugetUrl,
  product,
  releaseLabel,
  releaseNoun,
  reportCommand,
} from "./product";

const diagnostics = [
  {
    code: "PM001",
    title: "Unused central versions",
    copy: "Find PackageVersion entries that no affected project actually uses.",
    tone: "mint",
  },
  {
    code: "PM002",
    title: "Version drift",
    copy: "Spot explicit package versions that silently diverge across projects.",
    tone: "cyan",
  },
  {
    code: "PM003",
    title: "CPM bypasses",
    copy: "Catch PackageReference versions that bypass central management.",
    tone: "violet",
  },
  {
    code: "PM004",
    title: "Duplicate central entries",
    copy: "Expose conflicting PackageVersion items in the effective project scope.",
    tone: "amber",
  },
  {
    code: "PM005",
    title: "Restore problems",
    copy: "Surface important NuGet codes such as NU1605, NU1107, and NU1109.",
    tone: "coral",
  },
  {
    code: "PM006",
    title: "Floating versions",
    copy: "Flag floating PackageVersion, Version, and VersionOverride declarations before they make restores drift.",
    tone: "blue",
  },
];

export default function Home() {
  const [copied, setCopied] = useState(false);

  async function copyInstallCommand() {
    await navigator.clipboard.writeText(installCommand);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1800);
  }

  return (
    <main>
      <nav className="nav-shell" aria-label="Primary navigation">
        <a className="brand-lockup" href="#top" aria-label="PackageMedic home">
          <img src={assetPath("packagemedic-icon.png")} alt="" width="34" height="34" />
          <span>PackageMedic</span>
        </a>
        <div className="nav-links">
          <a href="#diagnostics">Diagnostics</a>
          <a href="#workflow">How it works</a>
          <a href="#policy">Policy</a>
          <a href="#ci">CI</a>
          <a href="#safety">Safety</a>
        </div>
        <a
          className="nav-action"
          href="https://github.com/GonzMeza/package-medic"
          target="_blank"
          rel="noreferrer"
        >
          View on GitHub <span aria-hidden="true">↗</span>
        </a>
      </nav>

      <section className="hero" id="top">
        <div className="pixel-grid" aria-hidden="true" />
        <div className="hero-copy">
          <div className="eyebrow">
            <span className="status-pixel" />
            {releaseLabel} · v{product.version}
          </div>
          <h1>
            Your NuGet graph,
            <span> diagnosed.</span>
          </h1>
          <p className="hero-lede">
            PackageMedic scans SDK-style .NET projects for dependency drift,
            stale central versions, CPM bypasses, floating versions, and
            restore problems—then explains what to review.
          </p>

          <div className="install-box" aria-label="Install PackageMedic">
            <span className="prompt" aria-hidden="true">›</span>
            <code>{installCommand}</code>
            <button type="button" onClick={copyInstallCommand} aria-label="Copy install command">
              {copied ? "Copied" : "Copy"}
            </button>
          </div>

          <div className="hero-actions">
            <a className="button primary" href="#workflow">
              See the scan flow <span aria-hidden="true">↓</span>
            </a>
            <a
              className="button secondary"
              href={nugetUrl}
              target="_blank"
              rel="noreferrer"
            >
              Open in NuGet <span aria-hidden="true">↗</span>
            </a>
          </div>

          <ul className="hero-trust" aria-label="Key product traits">
            <li><span aria-hidden="true">◆</span> Read-only</li>
            <li><span aria-hidden="true">◆</span> No telemetry</li>
            <li><span aria-hidden="true">◆</span> Baseline-aware</li>
          </ul>
        </div>

        <div className="hero-visual" aria-label="Animated dependency scan illustration">
          <div className="network" aria-hidden="true">
            <span className="edge edge-a" />
            <span className="edge edge-b" />
            <span className="edge edge-c" />
            <span className="edge edge-d" />
            <span className="node node-a" />
            <span className="node node-b" />
            <span className="node node-c" />
            <span className="node node-d" />
            <span className="node node-e" />
          </div>
          <div className="scan-ring ring-one" aria-hidden="true" />
          <div className="scan-ring ring-two" aria-hidden="true" />
          <div className="scan-beam" aria-hidden="true" />

          <div className="pixel-volume" aria-hidden="true">
            <span className="facet facet-a" />
            <span className="facet facet-b" />
            <span className="facet facet-c" />
            <span className="facet facet-d" />
            <span className="facet facet-e" />
            <span className="facet facet-f" />
            <span className="facet facet-g" />
            <span className="facet facet-h" />
            <span className="facet facet-i" />
            <span className="facet facet-j" />
            <span className="facet facet-k" />
            <span className="facet facet-l" />
          </div>

          <div className="voxel voxel-one" aria-hidden="true"><i /><b /><em /></div>
          <div className="voxel voxel-two" aria-hidden="true"><i /><b /><em /></div>
          <div className="voxel voxel-three" aria-hidden="true"><i /><b /><em /></div>
          <div className="voxel voxel-four" aria-hidden="true"><i /><b /><em /></div>

          <div className="core-cube">
            <span className="logo-orbit" aria-hidden="true" />
            <img src={assetPath("packagemedic-mark-transparent.png")} alt="PackageMedic logo" />
          </div>

          <div className="scan-result">
            <div>
              <span className="result-dot" />
              Graph scan complete
            </div>
            <strong>47 direct</strong>
            <span>133 transitive · 1 warning</span>
          </div>
        </div>
      </section>

      <section className="signal-strip" aria-label="Compatibility summary">
        <div><strong>.NET 8–10</strong><span>SDK-style projects</span></div>
        <div><strong>Policy aware</strong><span>Config · baseline · suppressions</span></div>
        <div><strong>Text · JSON · SARIF</strong><span>Human and CI output</span></div>
        <div><strong>Cross-platform</strong><span>Windows · Linux · macOS</span></div>
      </section>

      <section className="section diagnostics-section" id="diagnostics">
        <div className="section-heading">
          <div>
            <span className="section-kicker">Diagnostic matrix</span>
            <h2>Six checks. One clearer graph.</h2>
          </div>
          <p>
            Each finding includes evidence, project context, source location
            when available, and a suggested next action.
          </p>
        </div>

        <div className="diagnostic-grid">
          {diagnostics.map((diagnostic, index) => (
            <article className={`diagnostic-card ${diagnostic.tone}`} key={diagnostic.code}>
              <div className="card-pixels" aria-hidden="true">
                <span /><span /><span />
              </div>
              <span className="diagnostic-code">{diagnostic.code}</span>
              <h3>{diagnostic.title}</h3>
              <p>{diagnostic.copy}</p>
              <span className="card-index">0{index + 1}</span>
            </article>
          ))}
        </div>
      </section>

      <section className="section workflow-section" id="workflow">
        <div className="workflow-copy">
          <span className="section-kicker">One command, full context</span>
          <h2>From project files to an explainable diagnosis.</h2>
          <p>
            PackageMedic evaluates the same MSBuild model your project uses,
            reads NuGet&apos;s resolved assets graph, and runs conservative rules
            designed to avoid noisy false positives.
          </p>

          <ol className="workflow-list">
            <li><span>01</span><div><strong>Discover</strong><small>Project, solution, slnx, or directory.</small></div></li>
            <li><span>02</span><div><strong>Evaluate</strong><small>Imports, conditions, target frameworks, and CPM.</small></div></li>
            <li><span>03</span><div><strong>Resolve</strong><small>Direct and transitive packages from project.assets.json.</small></div></li>
            <li><span>04</span><div><strong>Diagnose</strong><small>Text, deterministic JSON, or SARIF with exit codes.</small></div></li>
            <li><span>05</span><div><strong>Classify</strong><small>New, existing, and resolved against a portable baseline.</small></div></li>
          </ol>
        </div>

        <div className="terminal-window" aria-label="Example PackageMedic output">
          <div className="terminal-bar">
            <span /><span /><span />
            <small>{doctorCommand}</small>
          </div>
          <div className="terminal-body">
            <p><i>›</i> {doctorCommand} ./MySolution.sln</p>
            <p className="muted">Running dotnet restore for MySolution.sln…</p>
            <p className="muted">Evaluating 12 projects…</p>
            <div className="terminal-rule" />
            <p><b>{product.name} {product.version}</b></p>
            <p className="muted">Scanned: 1 solution · 12 projects · 180 packages</p>
            <p className="warning"><strong>PM001 warning:</strong> Central package version is not used</p>
            <p>&nbsp;&nbsp;Package: <b>Humanizer</b></p>
            <p>&nbsp;&nbsp;Version: 2.14.1</p>
            <p>&nbsp;&nbsp;File: Directory.Packages.props:18</p>
            <div className="terminal-rule" />
            <p><span className="summary-ok">0 errors</span> · <span className="summary-warn">1 warning</span> · 0 informational</p>
            <span className="terminal-cursor" aria-hidden="true" />
          </div>
        </div>
      </section>

      <section className="section policy-section" id="policy">
        <div className="section-heading">
          <div>
            <span className="section-kicker">Adopt it without CI shock</span>
            <h2>Repository policy, with every exception accounted for.</h2>
          </div>
          <p>
            Versioned configuration keeps the same rules on every machine.
            Baselines let established repositories block only regressions while
            known findings remain visible.
          </p>
        </div>
        <div className="ci-grid">
          <div className="terminal-window ci-terminal" aria-label="PackageMedic policy and baseline commands">
            <div className="terminal-bar">
              <span /><span /><span />
              <small>team adoption</small>
            </div>
            <div className="terminal-body">
              <p><i>›</i> {initCommand}</p>
              <p className="muted">Created .packagemedic.json</p>
              <p><i>›</i> {baselineCommand}</p>
              <p className="muted">Accepted findings now have portable fingerprints</p>
              <div className="terminal-rule" />
              <p><i>›</i> {newOnlyCommand}</p>
              <p><span className="summary-warn">1 new</span> · 18 existing · <span className="summary-ok">2 resolved</span></p>
            </div>
          </div>
          <div className="ci-features">
            <article>
              <span>01</span>
              <strong>Config as code</strong>
              <p>Enable rules, tune severity, set timeouts, and exclude portable paths.</p>
            </article>
            <article>
              <span>02</span>
              <strong>Justified suppressions</strong>
              <p>Every exception requires a reason and stays visible in reports.</p>
            </article>
            <article>
              <span>03</span>
              <strong>New-only gates</strong>
              <p>Keep existing debt visible while stopping fresh warnings in pull requests.</p>
            </article>
            <article>
              <span>04</span>
              <strong>Read-only cleanup</strong>
              <p>clean --dry-run previews high-confidence candidates; 0.3 has no apply path.</p>
            </article>
          </div>
        </div>
      </section>

      <section className="section ci-section" id="ci">
        <div className="section-heading">
          <div>
            <span className="section-kicker">Built for pull requests</span>
            <h2>Diagnostics where the dependency changes happen.</h2>
          </div>
          <p>
            Generate JSON and SARIF from one analysis or run the official GitHub
            Action to place isolated findings beside the affected project files.
          </p>
        </div>
        <div className="ci-grid">
          <div className="terminal-window ci-terminal" aria-label="JSON and SARIF command example">
            <div className="terminal-bar">
              <span /><span /><span />
              <small>one scan · two reports</small>
            </div>
            <div className="terminal-body">
              <p><i>›</i> {reportCommand}</p>
              <p className="muted">Wrote json report to reports/medic.json</p>
              <p className="muted">Wrote sarif report to reports/medic.sarif</p>
              <div className="terminal-rule" />
              <p><span className="summary-ok">PM001–PM006 mapped</span></p>
              <p className="muted">Shared fingerprints · new/existing baseline states</p>
            </div>
          </div>
          <div className="ci-features">
            <article>
              <span>01</span>
              <strong>New-only annotations</strong>
              <p>Annotate new findings by default, or opt into all/none.</p>
            </article>
            <article>
              <span>02</span>
              <strong>Code Scanning upload</strong>
              <p>SARIF 2.1.0 integrates with GitHub without changing project files.</p>
            </article>
            <article>
              <span>03</span>
              <strong>One scan, two reports</strong>
              <p>Atomic --output and --sarif-output files come from one analysis.</p>
            </article>
            <article>
              <span>04</span>
              <strong>Isolated Action runs</strong>
              <p>Separate reports stay safe while policy summaries expose new, existing, resolved, and suppressed counts.</p>
            </article>
          </div>
        </div>
      </section>

      <section className="section safety-section" id="safety">
        <div className="safety-cube" aria-hidden="true">
          <div className="shield-pixel">✓</div>
          <span className="satellite sat-one" />
          <span className="satellite sat-two" />
          <span className="satellite sat-three" />
        </div>
        <div className="safety-copy">
          <span className="section-kicker">Safe by design</span>
          <h2>Diagnosis without surprise edits.</h2>
          <p>
            PackageMedic remains intentionally read-only. It does not apply
            fixes, rewrite project files, or collect telemetry.
          </p>
          <div className="safety-grid">
            <div><span>01</span><strong>No automatic mutations</strong></div>
            <div><span>02</span><strong>No telemetry collection</strong></div>
            <div><span>03</span><strong>No private feed credentials printed</strong></div>
            <div><span>04</span><strong>Restore can be explicitly skipped</strong></div>
            <div><span>05</span><strong>Subprocess output and time are bounded</strong></div>
          </div>
        </div>
      </section>

      <section className="final-cta">
        <div className="cta-grid" aria-hidden="true" />
        <span className="section-kicker">Ready for a checkup?</span>
        <h2>Give your dependency graph a second opinion.</h2>
        <p>Install the current {releaseNoun} and run your first read-only scan.</p>
        <div className="install-box compact">
          <span className="prompt" aria-hidden="true">›</span>
          <code>{doctorCommand}</code>
          <button
            type="button"
            onClick={() => navigator.clipboard.writeText(doctorCommand)}
            aria-label="Copy doctor command"
          >
            Copy
          </button>
        </div>
      </section>

      <footer>
        <div className="brand-lockup">
          <img src={assetPath("packagemedic-icon.png")} alt="" width="30" height="30" />
          <span>PackageMedic</span>
        </div>
        <p>Open-source tooling for healthier .NET dependency graphs.</p>
        <div>
          <a href="https://github.com/GonzMeza/package-medic">GitHub</a>
          <a href={nugetUrl}>NuGet</a>
          <a href="https://github.com/GonzMeza/package-medic/issues">Issues</a>
        </div>
      </footer>
    </main>
  );
}
