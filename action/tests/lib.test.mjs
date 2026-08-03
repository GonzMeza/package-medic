import assert from 'node:assert/strict';
import os from 'node:os';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import {
  annotationFor,
  diagnosticsForAnnotations,
  escapeCommandData,
  escapeCommandProperty,
  isolatedName,
  normalizeActionInstance,
  parseAnnotationMode,
  parseBoolean,
  renderSummary,
  reportDetails,
  resolveNugetSource,
  resolveOutputDirectory,
  resolveOptionalWorkspaceFile,
  resolveScanPath,
  runCommand,
  validateExactVersion,
} from '../lib.mjs';

test('validates booleans and exact package versions', () => {
  assert.equal(parseBoolean('TRUE', 'restore'), true);
  assert.equal(parseBoolean('false', 'restore'), false);
  assert.equal(validateExactVersion('0.3.0'), '0.3.0');
  assert.throws(() => parseBoolean('yes', 'restore'));
  assert.throws(() => validateExactVersion('0.2.*'));
  assert.throws(() => validateExactVersion('latest'));
});

test('supports baseline-aware annotation modes and legacy booleans', () => {
  assert.equal(parseAnnotationMode('new'), 'new');
  assert.equal(parseAnnotationMode('all'), 'all');
  assert.equal(parseAnnotationMode('none'), 'none');
  assert.equal(parseAnnotationMode('true'), 'all');
  assert.equal(parseAnnotationMode('FALSE'), 'none');
  assert.throws(() => parseAnnotationMode('warnings'));

  const details = {
    diagnostics: [
      { code: 'PM001', baselineState: 'new' },
      { code: 'PM002', baselineState: 'existing' },
      { code: 'PM003' },
    ],
  };
  assert.deepEqual(diagnosticsForAnnotations(details, 'new').map((item) => item.code), ['PM001', 'PM003']);
  assert.equal(diagnosticsForAnnotations(details, 'all').length, 3);
  assert.deepEqual(diagnosticsForAnnotations(details, 'none'), []);
});

test('streams child process output instead of buffering it in the action', () => {
  let receivedOptions;
  const exitCode = runCommand('package-medic', ['doctor', '.'], (_executable, _args, options) => {
    receivedOptions = options;
    return { status: 0 };
  });

  assert.equal(exitCode, 0);
  assert.deepEqual(receivedOptions, { windowsHide: true, stdio: 'inherit' });
});

test('treats pre-0.3 diagnostics as new when baseline metadata is absent', () => {
  const report = {
    summary: { projects: 1 },
    diagnostics: [
      { code: 'PM001', severity: 'warning' },
      { code: 'PM002', severity: 'information' },
    ],
    analysisErrors: [],
  };

  const details = reportDetails(report);

  assert.deepEqual(details.baseline, { new: 2, existing: 0, resolved: 0 });
  assert.deepEqual(diagnosticsForAnnotations(details, 'new'), report.diagnostics);
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
  mkdirSync(path.join(root, 'config'));
  const config = path.join(root, 'config', 'packagemedic.json');
  const baseline = path.join(root, 'config', 'baseline.json');
  writeFileSync(config, '{}', 'utf8');
  writeFileSync(baseline, '{}', 'utf8');
  assert.equal(resolveScanPath(root, 'src'), path.join(root, 'src'));
  assert.equal(resolveOutputDirectory(root, temp, ''), path.join(temp, 'packagemedic-report'));
  assert.equal(
    resolveOutputDirectory(root, temp, 'reports', '__self_2'),
    path.join(root, 'reports', 'packagemedic-report-self_2'));
  assert.throws(() => resolveScanPath(root, '..'));
  assert.throws(() => resolveOutputDirectory(root, temp, path.join('..', 'elsewhere')));
  assert.throws(() => resolveOutputDirectory(root, temp, 'missing'));
  assert.equal(resolveOptionalWorkspaceFile(root, 'config/packagemedic.json', 'config'), config);
  assert.equal(resolveOptionalWorkspaceFile(root, '', 'baseline'), undefined);
  assert.throws(() => resolveOptionalWorkspaceFile(root, '../baseline.json', 'baseline'));
  assert.throws(() => resolveOptionalWorkspaceFile(root, 'config/missing.json', 'config'));
  assert.throws(() => resolveOptionalWorkspaceFile(root, 'config', 'config'));
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
      { severity: 'error', baselineState: 'new' },
      { severity: 'warning', baselineState: 'existing' },
      { severity: 'information', baselineState: 'new' },
      { severity: 'unknown' },
    ],
    baseline: { new: 2, existing: 1, resolved: 3 },
    policy: { suppressed: 4 },
    suppressedDiagnostics: [{ code: 'PM001' }],
    analysisErrors: [],
  };
  const details = reportDetails(report);
  assert.deepEqual(details.counts, { errors: 1, warnings: 1, information: 2 });
  assert.deepEqual(details.baseline, { new: 2, existing: 1, resolved: 3 });
  assert.equal(details.suppressed, 4);
  const summary = renderSummary(report, details, 1, path.resolve('repo'));
  assert.match(summary, /Threshold reached/);
  assert.match(summary, /src\\\|main/);
  assert.match(summary, /\| 2 \| 1 \| 3 \| 4 \|/);
});
