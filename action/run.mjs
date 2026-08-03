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
  resolveNugetSource,
  resolveOutputDirectory,
  resolveOptionalWorkspaceFile,
  resolveScanPath,
  runCommand,
  validateExactVersion,
  validateGitReference,
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
  const includeTransitiveAudit = parseBoolean(
    process.env.PACKAGEMEDIC_INCLUDE_TRANSITIVE_AUDIT,
    'include-transitive-audit');
  const diffBase = validateGitReference(process.env.PACKAGEMEDIC_DIFF_BASE);
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
    if (includeTransitiveAudit) baseArguments.push('--include-transitive');
  }
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
