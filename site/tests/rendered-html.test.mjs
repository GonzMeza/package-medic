import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const productVersion = (await readFile(new URL("../../VERSION", import.meta.url), "utf8")).trim();
const productVersionPattern = new RegExp(productVersion.replaceAll(".", "\\."));

test("exports the complete PackageMedic 0.6 landing page", async () => {
  const html = await readFile(new URL("../out/index.html", import.meta.url), "utf8");
  assert.match(html, /<title>PackageMedic/);
  assert.match(html, /Your NuGet graph/);
  assert.match(html, /PackageMedic\.Tool/);
  assert.match(html, /Read-only/);
  assert.match(html, /PM001/);
  assert.match(html, /Stable release/);
  assert.match(html, productVersionPattern);
  assert.doesNotMatch(html, /Development preview/);
  assert.match(html, /SARIF/);
  assert.match(html, /--output/);
  assert.match(html, /--sarif-output/);
  assert.match(html, /PM006/);
  assert.match(html, /PM007/);
  assert.match(html, /PM008/);
  assert.match(html, /Known vulnerabilities/);
  assert.match(html, /PR-aware diffs/);
  assert.match(html, /Dependency Impact Gate/);
  assert.match(html, /Dependency Time Machine/);
  assert.match(html, /package-medic simulate Example\.Package --to 2\.0\.0/);
  assert.match(html, /--verify test/);
  assert.match(html, /214 tests/);
  assert.match(html, /CycloneDX 1\.7/);
  assert.match(html, /in-toto/);
  assert.match(html, /blast radius/);
  assert.match(html, /\.packagemedic\.json/);
  assert.match(html, /baseline create/);
  assert.match(html, /fail-on-new/);
  assert.match(html, /Justified suppressions/);
  assert.match(html, /bound parallelism/);
  assert.match(html, /Read the docs/);
  assert.match(html, /href="(?:\/package-medic)?\/docs\/"/);
  assert.doesNotMatch(html, /react-loading-skeleton|Your site is taking shape/);
});

test("exports complete, navigable PackageMedic documentation", async () => {
  const pages = [
    ["docs/index.html", /Diagnose the graph with confidence/],
    ["docs/getting-started/index.html", /Install and run the first diagnosis/],
    ["docs/commands/index.html", /One tool, focused commands/],
    ["docs/configuration/index.html", /Make every scan repeatable/],
    ["docs/baselines/index.html", /Block regressions, not the rollout/],
    ["docs/impact-gate/index.html", /Know what a package update brings with it/],
    ["docs/time-machine/index.html", /See the graph before changing the package/],
    ["docs/verified-experiments/index.html", /Verify the change in two immutable snapshots/],
    ["docs/github-action/index.html", /Put diagnostics beside the change/],
    ["docs/reports/index.html", /Readable by people and pipelines/],
    ["docs/diagnostics/index.html", /PM008/],
    ["docs/security/index.html", /Read-only, bounded, and explicit/],
    ["docs/troubleshooting/index.html", /Turn failures into useful evidence/],
  ];

  for (const [path, expected] of pages) {
    const html = await readFile(new URL(`../out/${path}`, import.meta.url), "utf8");
    assert.match(html, /PackageMedic/);
    assert.match(html, /Search documentation/);
    assert.match(html, productVersionPattern);
    assert.match(html, expected);
    assert.match(html, /href="(?:\/package-medic)?\/docs\/diagnostics\/"/);
    assert.match(html, /href="(?:\/package-medic)?\/docs\/time-machine\/"/);
    assert.match(html, /href="(?:\/package-medic)?\/docs\/verified-experiments\/"/);
    assert.doesNotMatch(html, /Development preview/);
  }
});
