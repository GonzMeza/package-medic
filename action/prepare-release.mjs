import { randomUUID } from 'node:crypto';
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { validateExactVersion } from './lib.mjs';
import { extractReleaseNotes } from './release-lib.mjs';

function setOutput(name, value) {
  if (!process.env.GITHUB_OUTPUT) return;
  const delimiter = `packagemedic_${randomUUID()}`;
  appendFileSync(process.env.GITHUB_OUTPUT, `${name}<<${delimiter}\n${String(value)}\n${delimiter}\n`, 'utf8');
}

const repository = path.resolve(process.env.GITHUB_WORKSPACE || process.cwd());
const version = validateExactVersion(readFileSync(path.join(repository, 'VERSION'), 'utf8').trim());
const expectedTag = `v${version}`;
if (process.env.GITHUB_REF_NAME !== expectedTag) {
  throw new Error(`Release tag '${process.env.GITHUB_REF_NAME || ''}' must exactly match VERSION (${expectedTag}).`);
}

const changelog = readFileSync(path.join(repository, 'CHANGELOG.md'), 'utf8');
const notes = extractReleaseNotes(changelog, version);
const outputDirectory = path.join(repository, 'artifacts', 'release');
mkdirSync(outputDirectory, { recursive: true });
writeFileSync(path.join(outputDirectory, 'release-notes.md'), notes, 'utf8');

setOutput('version', version);
setOutput('prerelease', version.includes('-'));
