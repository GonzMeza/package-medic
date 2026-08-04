import assert from 'node:assert/strict';
import os from 'node:os';
import { mkdirSync, mkdtempSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import {
  annotationFor,
  diagnosticsForAnnotations,
  escapeCommandData,
  escapeCommandProperty,
  escapeMarkdown,
  isolatedName,
  normalizeActionInstance,
  parseAnnotationMode,
  parseBoolean,
  renderSummary,
  reportDetails,
  resolveAnalysisMode,
  resolveNugetSource,
  resolveOutputDirectory,
  resolveOptionalWorkspaceFile,
  resolveScanPath,
  runCommand,
  validateExactVersion,
  validateGitReference,
  validateReportSize,
} from '../lib.mjs';

test('validates booleans and exact package versions', () => {
  assert.equal(parseBoolean('TRUE', 'restore'), true);
  assert.equal(parseBoolean('false', 'restore'), false);
  assert.equal(validateExactVersion('0.3.0'), '0.3.0');
  assert.throws(() => parseBoolean('yes', 'restore'));
  assert.throws(() => validateExactVersion('0.2.*'));
  assert.throws(() => validateExactVersion('latest'));
});

test('validates optional Git references without restricting useful revision syntax', () => {
  assert.equal(validateGitReference(''), undefined);
  assert.equal(validateGitReference('origin/main'), 'origin/main');
  assert.equal(validateGitReference('HEAD~2'), 'HEAD~2');
  assert.equal(validateGitReference('8f21ac7^{commit}'), '8f21ac7^{commit}');
  assert.throws(() => validateGitReference('--help'));
  assert.throws(() => validateGitReference('main branch'));
  assert.throws(() => validateGitReference('main\nnext'));
  assert.throws(() => validateGitReference('a'.repeat(513)));
});

test('selects pull request diff mode without inventing or fetching Git references', () => {
  assert.deepEqual(resolveAnalysisMode('auto', '', 'pull_request', 'abc123'), {
    mode: 'diff', diffBase: 'abc123', automatic: true,
  });
  assert.deepEqual(resolveAnalysisMode('auto', '', 'push', ''), {
    mode: 'scan', diffBase: undefined, automatic: true,
  });
  assert.deepEqual(resolveAnalysisMode('scan', 'origin/main', 'push', ''), {
    mode: 'diff', diffBase: 'origin/main', automatic: false,
  });
  assert.throws(() => resolveAnalysisMode('diff', '', 'push', ''), /requires diff-base/u);
  assert.throws(() => resolveAnalysisMode('auto', '', 'pull_request', ''), /could not resolve/u);
  assert.throws(
    () => resolveAnalysisMode('auto', '', 'pull_request_target', 'abc123'),
    /does not analyze pull_request_target/u);
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

test('rejects oversized JSON reports before reading them into action memory', () => {
  assert.equal(validateReportSize(1024, 2048), 1024);
  assert.throws(() => validateReportSize(2049, 2048), /safety limit/u);
  assert.throws(() => validateReportSize(-1, 2048), /size is invalid/u);
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
  assert.equal(resolveNugetSource(workspace, runnerTemp, 'artifacts/packages'), realpathSync(feed));
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
  assert.equal(resolveOutputDirectory(root, temp, ''), path.join(realpathSync(temp), 'packagemedic-report'));
  assert.equal(
    resolveOutputDirectory(root, temp, 'reports', '__self_2'),
    path.join(realpathSync(path.join(root, 'reports')), 'packagemedic-report-self_2'));
  assert.throws(() => resolveScanPath(root, '..'));
  assert.throws(() => resolveOutputDirectory(root, temp, path.join('..', 'elsewhere')));
  assert.throws(() => resolveOutputDirectory(root, temp, 'missing'));
  assert.equal(resolveOptionalWorkspaceFile(root, 'config/packagemedic.json', 'config'), realpathSync(config));
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
  assert.equal(escapeCommandData('100%\u001b\u0000\r\nnext'), '100%25%0D%0Anext');
  assert.equal(escapeCommandProperty('a:b,c%'), 'a%3Ab%2Cc%25');
});

test('neutralizes untrusted Markdown in job summaries', () => {
  assert.equal(
    escapeMarkdown('[click](https://example.test)\u001b <img> | `code`'),
    '\\[click\\](https://example.test) \\<img\\> \\| \\`code\\`');
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

test('summarizes structured Git graph changes', () => {
  const impactViolation = {
    code: 'PMI001',
    message: 'Unsafe [downgrade](https://example.test)\n<img>',
    suggestedAction: 'Review `the change`',
    project: 'src/App.csproj',
    framework: 'net8.0',
    packageId: 'Example.Package',
    rootPackageId: 'Root.Package',
    dependencyPath: [
      { packageId: 'Root.Package', resolvedVersion: '1.0.0' },
      { packageId: 'Example.Package', resolvedVersion: '2.0.0' },
    ],
  };
  const report = {
    target: '.',
    summary: { projects: 1 },
    diagnostics: [{ code: 'PM007', severity: 'error', baselineState: 'new' }],
    analysisErrors: [],
    diff: {
      isComplete: true,
      summary: { added: 1, resolved: 2, severityChanged: 3 },
      packageChanges: [{ kind: 'versionChanged' }, { kind: 'added' }],
      packageSummary: {
        added: 1, removed: 0, upgraded: 1, downgraded: 0,
        uncomparableVersionChanges: 0, directToTransitive: 0, transitiveToDirect: 1,
      },
      riskSummary: {
        vulnerabilitiesIntroduced: 1, vulnerabilitiesResolved: 0,
        vulnerabilitiesPersistent: 2,
        deprecationsIntroduced: 0, deprecationsResolved: 1,
        deprecationsPersistent: 3,
      },
      projectSettingsChanges: [{ project: 'src/App.csproj' }],
      impact: {
        gatePassed: false,
        summary: {
          addedDirectPackages: 1,
          addedTransitivePackages: 4,
          maximumBlastRadius: 3,
          sourceChanges: 2,
          contentChanges: 1,
          violations: 99,
        },
        violations: [impactViolation],
      },
    },
  };

  const details = reportDetails(report);
  assert.deepEqual(details.diff, {
    added: 1,
    resolved: 2,
    severityChanged: 3,
    packageChanges: 2,
    packagesAdded: 1,
    packagesRemoved: 0,
    packagesUpgraded: 1,
    packagesDowngraded: 0,
    uncomparableVersionChanges: 0,
    directToTransitive: 0,
    transitiveToDirect: 1,
    vulnerabilitiesIntroduced: 1,
    vulnerabilitiesResolved: 0,
    vulnerabilitiesPersistent: 2,
    deprecationsIntroduced: 0,
    deprecationsResolved: 1,
    deprecationsPersistent: 3,
    projectSettingsChanges: 1,
    complete: true,
    baselineAnalysisErrors: 0,
    currentAnalysisErrors: 0,
    impact: {
      gatePassed: false,
      violations: 1,
      addedDirect: 1,
      addedTransitive: 4,
      maximumBlastRadius: 3,
      sourceChanges: 2,
      contentChanges: 1,
      violationDetails: [impactViolation],
      omittedViolations: 0,
    },
  });
  const summary = renderSummary(report, details, 1);
  assert.match(summary, /\| 1 \| 2 \| 3 \| 2 \| 1 \|/);
  assert.match(summary, /\| 1 \| 0 \| 1 \| 0 \| 0 \| 1 \|/);
  assert.match(summary, /Impact Gate: Blocked/);
  assert.match(summary, /\| 1 \| 1 \| 4 \| 3 \| 2 \| 1 \|/);
  assert.match(summary, /Root\.Package@1\.0\.0 → Example\.Package@2\.0\.0/);
  assert.doesNotMatch(summary, /<img>/);
  assert.match(summary, /\\<img\\>/);
});

test('marks incomplete Git comparisons in the job summary', () => {
  const report = {
    target: '.',
    summary: { projects: 0 },
    diagnostics: [],
    analysisErrors: [],
    diff: {
      isComplete: false,
      summary: { added: 0, resolved: 0, severityChanged: 0 },
      packageChanges: [],
      projectSettingsChanges: [],
      baselineAnalysisErrors: ['base failed'],
      currentAnalysisErrors: ['current failed'],
    },
  };

  const details = reportDetails(report);
  assert.equal(details.diff.complete, false);
  assert.match(renderSummary(report, details, 2), /Comparison incomplete/);
  assert.match(renderSummary(report, details, 2), /1 base analysis error\(s\), 1 current analysis error\(s\)/);
});
