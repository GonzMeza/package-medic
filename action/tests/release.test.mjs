import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { extractReleaseNotes } from '../release-lib.mjs';

test('extracts only the requested changelog section', () => {
  const notes = extractReleaseNotes('# Changelog\n\n## [0.2.0] - 2026-09-01\n\nNew release.\n\n## [0.1.0]\n\nOld release.\n', '0.2.0');
  assert.equal(notes, 'New release.\n');
  assert.throws(() => extractReleaseNotes('## [0.1.0]\n\nOld.', '0.2.0'));
});

test('creates a deterministic SHA-256 manifest for one package', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'packagemedic-checksum-'));
  const packageName = 'PackageMedic.Tool.0.2.0.nupkg';
  writeFileSync(path.join(directory, packageName), 'package bytes', 'utf8');
  const script = path.resolve('action', 'checksum.mjs');
  const result = spawnSync(process.execPath, [script, directory], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const expected = createHash('sha256').update('package bytes').digest('hex');
  assert.equal(readFileSync(path.join(directory, 'SHA256SUMS.txt'), 'utf8'), `${expected}  ${packageName}\n`);
});

test('prepares notes only when the tag exactly matches VERSION', () => {
  const repository = mkdtempSync(path.join(os.tmpdir(), 'packagemedic-release-'));
  const output = path.join(repository, 'github-output.txt');
  writeFileSync(path.join(repository, 'VERSION'), '0.2.0\n', 'utf8');
  writeFileSync(path.join(repository, 'CHANGELOG.md'), '# Changelog\n\n## [0.2.0]\n\nRelease body.\n', 'utf8');
  writeFileSync(output, '', 'utf8');
  const script = path.resolve('action', 'prepare-release.mjs');
  const environment = { ...process.env, GITHUB_WORKSPACE: repository, GITHUB_OUTPUT: output, GITHUB_REF_NAME: 'v0.2.0' };
  const success = spawnSync(process.execPath, [script], { encoding: 'utf8', env: environment });
  assert.equal(success.status, 0, success.stderr);
  assert.equal(readFileSync(path.join(repository, 'artifacts', 'release', 'release-notes.md'), 'utf8'), 'Release body.\n');
  assert.match(readFileSync(output, 'utf8'), /version<<.+\n0\.2\.0\n/s);

  mkdirSync(path.join(repository, 'other'), { recursive: true });
  const mismatch = spawnSync(process.execPath, [script], {
    encoding: 'utf8',
    env: { ...environment, GITHUB_REF_NAME: 'v0.2.1' },
  });
  assert.notEqual(mismatch.status, 0);
  assert.match(mismatch.stderr, /must exactly match VERSION/);
});
