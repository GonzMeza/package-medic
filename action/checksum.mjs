import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const directory = path.resolve(process.argv[2] || 'artifacts/release');
const packages = readdirSync(directory)
  .filter((name) => name.endsWith('.nupkg'))
  .sort((left, right) => left.localeCompare(right));
if (packages.length !== 1) {
  throw new Error(`Expected exactly one .nupkg release asset, found ${packages.length}.`);
}

const lines = packages.map((name) => {
  const digest = createHash('sha256').update(readFileSync(path.join(directory, name))).digest('hex');
  return `${digest}  ${name}`;
});
writeFileSync(path.join(directory, 'SHA256SUMS.txt'), `${lines.join('\n')}\n`, 'utf8');
