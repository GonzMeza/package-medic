import assert from 'node:assert/strict';
import os from 'node:os';
import { mkdirSync, mkdtempSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import {
  annotationFor,
  escapeCommandData,
  escapeCommandProperty,
  isolatedName,
  normalizeActionInstance,
  parseBoolean,
  renderSummary,
  reportDetails,
  resolveNugetSource,
  resolveOutputDirectory,
  resolveScanPath,
  validateExactVersion,
} from '../lib.mjs';

test('validates booleans and exact package versions', () => {
  assert.equal(parseBoolean('TRUE', 'restore'), true);
  assert.equal(parseBoolean('false', 'restore'), false);
  assert.equal(validateExactVersion('0.2.0'), '0.2.0');
  assert.throws(() => parseBoolean('yes', 'restore'));
  assert.throws(() => validateExactVersion('0.2.*'));
  assert.throws(() => validateExactVersion('latest'));
});

test('accepts HTTPS and trusted local NuGet sources only', () => {
  const workspace = mkdtempSync(path.join(os.tmpdir(), 'packagemedic-workspace-'));
  const runnerTemp = mkdtempSync(path.join(os.tmpdir(), 'packagemedic-runner-'));
  const feed = path.join(workspace, 'artifacts', 'packages');
  mkdirSync(feed, { recursive: true });
  assert.equal(resolveNugetSource(workspace, runnerTemp, 'https://api.nuget.org/v3/index.json'), 'https://api.nuget.org/v3/index.json');
  assert.equal(resolveNugetSource(workspace, runnerTemp, 'artifacts/packages'), feed);
  assert.throws(() => resolveNugetSource(workspace, runnerTemp, 'https://user:secret@example.test/v3/index.json'));
  assert.throws(() => resolveNugetSource(workspace, runnerTemp, 'https://example.test/v3/index.json?token=secret'));
  assert.throws(() => resolveNugetSource(workspace, runnerTemp, os.tmpdir()));
});

test('rejects scan and report paths outside trusted roots', () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'packagemedic-workspace-'));
  const temp = mkdtempSync(path.join(os.tmpdir(), 'packagemedic-runner-'));
  mkdirSync(path.join(root, 'reports'));
  assert.equal(resolveScanPath(root, 'src'), path.join(root, 'src'));
  assert.equal(resolveOutputDirectory(root, temp, ''), path.join(temp, 'packagemedic-report'));
  assert.equal(
    resolveOutputDirectory(root, temp, 'reports', '__self_2'),
    path.join(root, 'reports', 'packagemedic-report-self_2'));
  assert.throws(() => resolveScanPath(root, '..'));
  assert.throws(() => resolveOutputDirectory(root, temp, path.join('..', 'elsewhere')));
  assert.throws(() => resolveOutputDirectory(root, temp, 'missing'));
});

test('isolates names and paths for repeated action invocations', () => {
  assert.equal(normalizeActionInstance('__self_2'), 'self_2');
  assert.equal(normalizeActionInstance(' step / unsafe '), 'step-unsafe');
  assert.equal(isolatedName('packagemedic-report', '__self_2', 'artifact-name'), 'packagemedic-report-self_2');
  assert.equal(isolatedName('packagemedic', '__self_3', 'category'), 'packagemedic-self_3');
  assert.throws(() => isolatedName('../report', 'step', 'artifact-name'));
});

test('escapes all workflow command control characters', () => {
  assert.equal(escapeCommandData('100%\r\nnext'), '100%25%0D%0Anext');
  assert.equal(escapeCommandProperty('a:b,c%'), 'a%3Ab%2Cc%25');
});

test('creates safe annotations and omits files outside the workspace', () => {
  const workspace = path.resolve('repo');
  const diagnostic = {
    code: 'PM001',
    severity: 'warning',
    title: 'Title, unsafe: value',
    explanation: 'line one\n::error::inert',
    file: path.join(workspace, 'Directory.Packages.props'),
    line: 7,
  };
  const annotation = annotationFor(diagnostic, workspace);
  assert.match(annotation, /^::warning /);
  assert.match(annotation, /file=Directory\.Packages\.props/);
  assert.match(annotation, /line=7/);
  assert.match(annotation, /Title%2C unsafe%3A value/);
  assert.match(annotation, /line one%0A::error::inert$/);

  const outside = annotationFor({ ...diagnostic, file: path.resolve('outside.txt') }, workspace);
  assert.doesNotMatch(outside, /file=/);
});

test('derives counts from diagnostics instead of trusting stale summary values', () => {
  const report = {
    target: path.join(path.resolve('repo'), 'src|main'),
    summary: { projects: 2, errors: 99 },
    diagnostics: [
      { severity: 'error' },
      { severity: 'warning' },
      { severity: 'information' },
      { severity: 'unknown' },
    ],
    analysisErrors: [],
  };
  const details = reportDetails(report);
  assert.deepEqual(details.counts, { errors: 1, warnings: 1, information: 2 });
  const summary = renderSummary(report, details, 1, path.resolve('repo'));
  assert.match(summary, /Threshold reached/);
  assert.match(summary, /src\\\|main/);
});
