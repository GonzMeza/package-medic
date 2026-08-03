import assert from "node:assert/strict";
import test from "node:test";

async function render() {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request("http://localhost/", {
      headers: { accept: "text/html" },
    }),
    {
      ASSETS: {
        fetch: async () => new Response("Not found", { status: 404 }),
      },
    },
    {
      waitUntil() {},
      passThroughOnException() {},
    },
  );
}

test("server-renders the PackageMedic landing page", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);

  const html = await response.text();
  assert.match(html, /<title>PackageMedic/);
  assert.match(html, /Your NuGet graph/);
  assert.match(html, /PackageMedic\.Tool/);
  assert.match(html, /Read-only/);
  assert.match(html, /PM001/);
  assert.match(html, /Stable release/);
  assert.match(html, /0\.3\.0/);
  assert.doesNotMatch(html, /Development preview/);
  assert.match(html, /SARIF/);
  assert.match(html, /--output/);
  assert.match(html, /--sarif-output/);
  assert.match(html, /One scan, two reports/);
  assert.match(html, /Isolated Action runs/);
  assert.match(html, /PM006/);
  assert.match(html, /\.packagemedic\.json/);
  assert.match(html, /baseline create/);
  assert.match(html, /fail-on-new/);
  assert.match(html, /Justified suppressions/);
  assert.doesNotMatch(html, /react-loading-skeleton|Your site is taking shape/);
});
