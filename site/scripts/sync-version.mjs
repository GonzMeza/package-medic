import { readFile, writeFile } from "node:fs/promises";

const versionFile = new URL("../../VERSION", import.meta.url);
const generatedFile = new URL("../app/version.generated.ts", import.meta.url);
const version = (await readFile(versionFile, "utf8")).trim();

if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.test(version)) {
  throw new Error(`VERSION does not contain a valid semantic version: ${version}`);
}

const source =
  `// Generated from the repository VERSION file. Do not edit directly.\n` +
  `export const productVersion = ${JSON.stringify(version)};\n`;

await writeFile(generatedFile, source, "utf8");
