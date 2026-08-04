import { appendFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, statSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import process from 'node:process';
import {
  annotationFor,
  diagnosticsForAnnotations,
  enumValue,
  escapeCommandData,
  escapeMarkdown,
  isolatedName,
  isWithin,
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
  validateReportSize,
} from './lib.mjs';

function setOutput(name, value) {
  if (!process.env.GITHUB_OUTPUT) return;
  const delimiter = `packagemedic_${randomUUID()}`;
  appendFileSync(process.env.GITHUB_OUTPUT, `${name}<<${delimiter}\n${String(value)}\n${delimiter}\n`, 'utf8');
}

function appendSummary(markdown) {
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, markdown, 'utf8');
}

function toolExecutable(toolDirectory) {
  return path.join(toolDirectory, process.platform === 'win32' ? 'package-medic.exe' : 'package-medic');
}

function emitFailure(message, outputDirectory, artifactName, sarifCategory) {
  process.stdout.write(`::error title=PackageMedic action::${escapeCommandData(message)}\n`);
  appendSummary(`## PackageMedic\n\n**Operational error** — ${escapeMarkdown(message)}\n`);
  setOutput('exit-code', 2);
  setOutput('json-file', path.join(outputDirectory, 'packagemedic.json'));
  setOutput('sarif-file', path.join(outputDirectory, 'packagemedic.sarif'));
  setOutput('errors', 0);
  setOutput('warnings', 0);
  setOutput('information', 0);
  for (const output of [
    'findings-added', 'findings-resolved', 'severity-changed',
    'packages-added', 'packages-removed', 'packages-upgraded', 'packages-downgraded',
    'uncomparable-version-changes', 'direct-to-transitive', 'transitive-to-direct', 'cpm-changes',
    'vulnerabilities-introduced', 'vulnerabilities-resolved',
    'vulnerabilities-persistent',
    'deprecations-introduced', 'deprecations-resolved', 'deprecations-persistent',
    'impact-violations', 'impact-added-direct', 'impact-added-transitive',
    'impact-max-blast-radius', 'impact-source-changes', 'impact-content-changes',
  ]) setOutput(output, 0);
  setOutput('impact-gate-passed', '');
  setOutput('artifact-name', artifactName);
  setOutput('sarif-category', sarifCategory);
  setOutput('report-created', false);
  setOutput('sarif-created', false);
}

const actionInstance = normalizeActionInstance(process.env.PACKAGEMEDIC_ACTION_INSTANCE, randomUUID());
let artifactName = `packagemedic-report-${actionInstance}`;
let sarifCategory = `packagemedic-${actionInstance}`;
let outputDirectory = path.resolve(
  process.env.RUNNER_TEMP || process.cwd(),
  `packagemedic-report-${actionInstance}`);

try {
  const workspace = path.resolve(process.env.GITHUB_WORKSPACE || process.cwd());
  const runnerTemp = path.resolve(process.env.RUNNER_TEMP || path.join(workspace, '.packagemedic-temp'));
  mkdirSync(runnerTemp, { recursive: true });
  const scanPath = resolveScanPath(workspace, process.env.PACKAGEMEDIC_PATH || '.');
  outputDirectory = resolveOutputDirectory(
    workspace,
    runnerTemp,
    process.env.PACKAGEMEDIC_OUTPUT_DIRECTORY || '',
    actionInstance);
  artifactName = isolatedName(
    process.env.PACKAGEMEDIC_ARTIFACT_NAME || 'packagemedic-report',
    actionInstance,
    'artifact-name');
  sarifCategory = isolatedName(
    process.env.PACKAGEMEDIC_CATEGORY || 'packagemedic',
    actionInstance,
    'category');
  const version = validateExactVersion(process.env.PACKAGEMEDIC_TOOL_VERSION);
  const source = resolveNugetSource(workspace, runnerTemp, process.env.PACKAGEMEDIC_NUGET_SOURCE || 'https://api.nuget.org/v3/index.json');
  const restore = parseBoolean(process.env.PACKAGEMEDIC_RESTORE, 'restore');
  const audit = parseBoolean(process.env.PACKAGEMEDIC_AUDIT, 'audit');
  const deprecated = parseBoolean(process.env.PACKAGEMEDIC_DEPRECATED, 'deprecated');
  const includeTransitiveAudit = parseBoolean(
    process.env.PACKAGEMEDIC_INCLUDE_TRANSITIVE_AUDIT,
    'include-transitive-audit');
  const includeTransitiveDeprecated = parseBoolean(
    process.env.PACKAGEMEDIC_INCLUDE_TRANSITIVE_DEPRECATED,
    'include-transitive-deprecated');
  const { diffBase } = resolveAnalysisMode(
    process.env.PACKAGEMEDIC_MODE,
    process.env.PACKAGEMEDIC_DIFF_BASE,
    process.env.PACKAGEMEDIC_GITHUB_EVENT_NAME,
    process.env.PACKAGEMEDIC_PR_BASE_SHA);
  const annotationMode = parseAnnotationMode(process.env.PACKAGEMEDIC_ANNOTATIONS);
  parseBoolean(process.env.PACKAGEMEDIC_UPLOAD_SARIF, 'upload-sarif');
  parseBoolean(process.env.PACKAGEMEDIC_UPLOAD_ARTIFACT, 'upload-artifact');
  const failOnInput = String(process.env.PACKAGEMEDIC_FAIL_ON ?? '').trim();
  const failOn = failOnInput
    ? enumValue(failOnInput, 'fail-on', ['none', 'warning', 'error'])
    : undefined;
  const failOnNewInput = String(process.env.PACKAGEMEDIC_FAIL_ON_NEW ?? '').trim();
  const failOnNew = failOnNewInput
    ? enumValue(failOnNewInput, 'fail-on-new', ['none', 'warning', 'error'])
    : undefined;
  const verbosity = enumValue(process.env.PACKAGEMEDIC_VERBOSITY, 'verbosity', ['quiet', 'normal', 'detailed']);
  const maxParallelismInput = String(process.env.PACKAGEMEDIC_MAX_PARALLELISM ?? '').trim();
  const maxParallelism = maxParallelismInput ? Number(maxParallelismInput) : undefined;
  if (maxParallelism !== undefined &&
      (!Number.isInteger(maxParallelism) || maxParallelism < 1 || maxParallelism > 32)) {
    throw new Error('max-parallelism must be an integer between 1 and 32.');
  }
  const configFile = resolveOptionalWorkspaceFile(workspace, process.env.PACKAGEMEDIC_CONFIG, 'config');
  const baselineFile = resolveOptionalWorkspaceFile(workspace, process.env.PACKAGEMEDIC_BASELINE, 'baseline');
  if (diffBase && baselineFile) throw new Error('baseline cannot be combined with diff-base.');
  if (diffBase && failOnNew) throw new Error('fail-on-new cannot be combined with diff-base; diff already gates changed findings.');
  if (diffBase && !restore) {
    process.stdout.write(
      '::warning title=PackageMedic diff assets::restore=false requires usable assets files tracked in both compared Git trees.\n');
  }
  if (diffBase && runCommand('git', ['-C', workspace, 'cat-file', '-e', `${diffBase}^{commit}`]) !== 0) {
    throw new Error(
      `Git comparison base '${diffBase}' is not available locally. Configure actions/checkout with fetch-depth: 0 (or provide an available diff-base).`);
  }

  mkdirSync(outputDirectory, { recursive: true });
  const realWorkspace = realpathSync(workspace);
  const realRunnerTemp = realpathSync(runnerTemp);
  const realScanPath = realpathSync(scanPath);
  const realOutputDirectory = realpathSync(outputDirectory);
  if (!isWithin(realWorkspace, realScanPath)) throw new Error('path cannot resolve through a symbolic link outside GITHUB_WORKSPACE.');
  if (!isWithin(realWorkspace, realOutputDirectory) && !isWithin(realRunnerTemp, realOutputDirectory)) {
    throw new Error('output-directory cannot resolve through a symbolic link outside trusted roots.');
  }

  const toolDirectory = mkdtempSync(path.join(runnerTemp, `packagemedic-tool-${version}-`));

  mkdirSync(toolDirectory, { recursive: true });
  const installExit = runCommand('dotnet', [
    'tool', 'install', '--tool-path', toolDirectory,
    'PackageMedic.Tool', '--version', version,
    '--source', source, '--no-http-cache',
  ]);
  if (installExit !== 0) throw new Error(`PackageMedic.Tool ${version} installation failed with exit code ${installExit}.`);

  const executable = toolExecutable(toolDirectory);
  if (!existsSync(executable)) throw new Error('The installed PackageMedic executable was not found.');

  const jsonFile = path.join(outputDirectory, 'packagemedic.json');
  const sarifFile = path.join(outputDirectory, 'packagemedic.sarif');
  const baseArguments = diffBase
    ? ['diff', diffBase, scanPath, '--verbosity', verbosity]
    : ['doctor', scanPath, '--verbosity', verbosity];
  if (audit) {
    baseArguments.push('--audit');
  }
  if (deprecated) baseArguments.push('--deprecated');
  if (audit && includeTransitiveAudit) baseArguments.push('--include-transitive-audit');
  if (deprecated && includeTransitiveDeprecated) baseArguments.push('--include-transitive-deprecated');
  if (failOn) baseArguments.push('--fail-on', failOn);
  if (!restore) baseArguments.push('--no-restore');
  if (configFile) baseArguments.push('--config', configFile);
  if (!diffBase && baselineFile) baseArguments.push('--baseline', baselineFile);
  if (!diffBase && failOnNew) baseArguments.push('--fail-on-new', failOnNew);
  if (maxParallelism !== undefined) baseArguments.push('--max-parallelism', String(maxParallelism));
  const scanExit = runCommand(executable, [
    ...baseArguments,
    '--format', 'json',
    '--output', jsonFile,
    '--sarif-output', sarifFile,
  ]);

  let report;
  let details = { counts: { errors: 0, warnings: 0, information: 0 }, diagnostics: [] };
  if (existsSync(jsonFile)) {
    validateReportSize(statSync(jsonFile).size);
    report = JSON.parse(readFileSync(jsonFile, 'utf8'));
    details = reportDetails(report);
  }

  if (report) {
    for (const diagnostic of diagnosticsForAnnotations(details, annotationMode)) {
      process.stdout.write(`${annotationFor(diagnostic, workspace)}\n`);
    }
  }

  const reportsComplete = report && existsSync(sarifFile);
  const exitCode = reportsComplete && (scanExit === 0 || scanExit === 1) ? scanExit : 2;
  if (report) appendSummary(renderSummary(report, details, exitCode, workspace));
  else appendSummary(`## PackageMedic\n\n**Operational error** — the JSON report was not created.\n`);

  setOutput('exit-code', exitCode);
  setOutput('json-file', jsonFile);
  setOutput('sarif-file', sarifFile);
  setOutput('errors', details.counts.errors);
  setOutput('warnings', details.counts.warnings);
  setOutput('information', details.counts.information);
  setOutput('findings-added', details.diff?.added ?? 0);
  setOutput('findings-resolved', details.diff?.resolved ?? 0);
  setOutput('severity-changed', details.diff?.severityChanged ?? 0);
  setOutput('packages-added', details.diff?.packagesAdded ?? 0);
  setOutput('packages-removed', details.diff?.packagesRemoved ?? 0);
  setOutput('packages-upgraded', details.diff?.packagesUpgraded ?? 0);
  setOutput('packages-downgraded', details.diff?.packagesDowngraded ?? 0);
  setOutput('uncomparable-version-changes', details.diff?.uncomparableVersionChanges ?? 0);
  setOutput('direct-to-transitive', details.diff?.directToTransitive ?? 0);
  setOutput('transitive-to-direct', details.diff?.transitiveToDirect ?? 0);
  setOutput('cpm-changes', details.diff?.projectSettingsChanges ?? 0);
  setOutput('vulnerabilities-introduced', details.diff?.vulnerabilitiesIntroduced ?? 0);
  setOutput('vulnerabilities-resolved', details.diff?.vulnerabilitiesResolved ?? 0);
  setOutput('vulnerabilities-persistent', details.diff?.vulnerabilitiesPersistent ?? 0);
  setOutput('deprecations-introduced', details.diff?.deprecationsIntroduced ?? 0);
  setOutput('deprecations-resolved', details.diff?.deprecationsResolved ?? 0);
  setOutput('deprecations-persistent', details.diff?.deprecationsPersistent ?? 0);
  setOutput('impact-gate-passed', details.diff?.impact ? details.diff.impact.gatePassed : '');
  setOutput('impact-violations', details.diff?.impact?.violations ?? 0);
  setOutput('impact-added-direct', details.diff?.impact?.addedDirect ?? 0);
  setOutput('impact-added-transitive', details.diff?.impact?.addedTransitive ?? 0);
  setOutput('impact-max-blast-radius', details.diff?.impact?.maximumBlastRadius ?? 0);
  setOutput('impact-source-changes', details.diff?.impact?.sourceChanges ?? 0);
  setOutput('impact-content-changes', details.diff?.impact?.contentChanges ?? 0);
  setOutput('artifact-name', artifactName);
  setOutput('sarif-category', sarifCategory);
  setOutput('report-created', existsSync(jsonFile) || existsSync(sarifFile));
  setOutput('sarif-created', existsSync(sarifFile));
} catch (error) {
  emitFailure(
    error instanceof Error ? error.message : String(error),
    outputDirectory,
    artifactName,
    sarifCategory);
}
