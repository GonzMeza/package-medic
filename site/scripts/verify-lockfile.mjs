import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const packageJson = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const lockfile = JSON.parse(await readFile(new URL('../package-lock.json', import.meta.url), 'utf8'));

assert.equal(lockfile.lockfileVersion, 3, 'package-lock.json must use lockfileVersion 3');

const directGroups = ['dependencies', 'devDependencies'];
const exactVersion = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;

for (const group of directGroups) {
  for (const [name, version] of Object.entries(packageJson[group] ?? {})) {
    assert.match(
      version,
      exactVersion,
      `${group}.${name} must use an exact version instead of a range, URL, tag, or Git reference`,
    );
    assert.equal(
      lockfile.packages['']?.[group]?.[name],
      version,
      `package-lock.json does not match ${group}.${name}`,
    );
  }
}

for (const [name, version] of Object.entries(packageJson.overrides ?? {})) {
  assert.equal(typeof version, 'string', `overrides.${name} must select one exact version`);
  assert.match(version, exactVersion, `overrides.${name} must use an exact version`);
}

const packageNameFromPath = (packagePath) => packagePath.slice(packagePath.lastIndexOf('node_modules/') + 13);
const verifiedResolutions = new Set();
const unresolvedPackages = [];
const installScripts = [];
let verifiedPackageCount = 0;

for (const [packagePath, metadata] of Object.entries(lockfile.packages ?? {})) {
  if (packagePath === '' || metadata.link) continue;

  const identity = `${packageNameFromPath(packagePath)}@${metadata.version}`;
  if (metadata.hasInstallScript) installScripts.push(identity);

  if (!metadata.resolved) {
    unresolvedPackages.push({ identity, metadata, packagePath });
    continue;
  }

  const resolved = new URL(metadata.resolved);
  assert.equal(resolved.protocol, 'https:', `${identity} must use HTTPS`);
  assert.equal(resolved.hostname, 'registry.npmjs.org', `${identity} must come from registry.npmjs.org`);
  assert.match(metadata.integrity ?? '', /^sha512-/, `${identity} must have a SHA-512 integrity value`);
  verifiedResolutions.add(identity);
  verifiedPackageCount += 1;
}

for (const { identity, metadata, packagePath } of unresolvedPackages) {
  const nestedMarker = '/node_modules/';
  const parentPath = packagePath.slice(0, packagePath.lastIndexOf(nestedMarker));
  const parentMetadata = lockfile.packages[parentPath];
  const packageName = packageNameFromPath(packagePath);

  assert.equal(metadata.optional, true, `${identity} has no registry resolution and is not optional`);
  assert.equal(
    parentMetadata?.bundleDependencies?.includes(packageName),
    true,
    `${identity} has no registry resolution and is not declared as a bundled dependency`,
  );
  assert.equal(
    verifiedResolutions.has(`${packageNameFromPath(parentPath)}@${parentMetadata.version}`),
    true,
    `${identity} is bundled by a parent without a verified registry resolution`,
  );
}

// This native resolver is currently the only locked dependency declaring an install script.
// CI installs with --ignore-scripts; the allowlist makes any newly introduced script a review event.
assert.deepEqual(installScripts.sort(), ['unrs-resolver@1.11.1']);

console.log(
  `Verified ${verifiedPackageCount + unresolvedPackages.length} locked packages: exact direct versions, npm registry only, SHA-512 integrity, and no unexpected install scripts.`,
);
