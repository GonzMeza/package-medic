import path from 'node:path';
import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const exactNuGetVersion = /^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/;

export function parseBoolean(value, name) {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (normalized === 'true') return true;
  if (normalized === 'false') return false;
  throw new Error(`${name} must be 'true' or 'false'.`);
}

export function validateExactVersion(value) {
  const version = String(value ?? '').trim();
  if (!exactNuGetVersion.test(version) || version.includes('*')) {
    throw new Error('tool-version must be an exact NuGet version, for example 0.2.0.');
  }
  return version;
}

export function enumValue(value, name, allowed) {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (!allowed.includes(normalized)) {
    throw new Error(`${name} must be one of: ${allowed.join(', ')}.`);
  }
  return normalized;
}

export function isWithin(parent, candidate) {
  const relative = path.relative(path.resolve(parent), path.resolve(candidate));
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

export function normalizeActionInstance(value, fallback = 'run') {
  const normalized = String(value || fallback)
    .trim()
    .replaceAll(/[^0-9A-Za-z._-]+/g, '-')
    .replaceAll(/^[._-]+|[._-]+$/g, '')
    .slice(0, 64);
  return normalized || 'run';
}

export function isolatedName(value, instance, name) {
  const base = String(value ?? '').trim();
  if (!/^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$/.test(base)) {
    throw new Error(`${name} must use 1-128 letters, numbers, dots, underscores, or hyphens.`);
  }
  return `${base}-${normalizeActionInstance(instance)}`;
}

export function resolveScanPath(workspace, inputPath) {
  const candidate = path.resolve(workspace, inputPath || '.');
  if (!isWithin(workspace, candidate)) {
    throw new Error('path must resolve inside GITHUB_WORKSPACE.');
  }
  return candidate;
}

export function resolveOutputDirectory(workspace, runnerTemp, inputPath, instance = '') {
  const base = inputPath ? path.resolve(workspace, inputPath) : path.resolve(runnerTemp);
  if (!isWithin(workspace, base) && !isWithin(runnerTemp, base)) {
    throw new Error('output-directory must resolve inside GITHUB_WORKSPACE or RUNNER_TEMP.');
  }
  let realBase;
  try {
    realBase = realpathSync(base);
  } catch {
    throw new Error('output-directory must name an existing base directory.');
  }
  const realWorkspace = realpathSync(workspace);
  const realRunnerTemp = realpathSync(runnerTemp);
  if (!isWithin(realWorkspace, realBase) && !isWithin(realRunnerTemp, realBase)) {
    throw new Error('output-directory cannot resolve through a symbolic link outside trusted roots.');
  }
  const candidate = path.resolve(
    realBase,
    instance ? `packagemedic-report-${normalizeActionInstance(instance)}` : 'packagemedic-report');
  return candidate;
}

export function resolveNugetSource(workspace, runnerTemp, input) {
  const raw = String(input ?? '').trim();
  if (!raw) throw new Error('nuget-source cannot be empty.');

  const resolveLocal = (candidate) => {
    const realCandidate = realpathSync(path.resolve(candidate));
    const realWorkspace = realpathSync(workspace);
    const realRunnerTemp = realpathSync(runnerTemp);
    if (!isWithin(realWorkspace, realCandidate) && !isWithin(realRunnerTemp, realCandidate)) {
      throw new Error('Local nuget-source must resolve inside GITHUB_WORKSPACE or RUNNER_TEMP.');
    }
    return realCandidate;
  };

  if (path.isAbsolute(raw)) return resolveLocal(raw);

  let source;
  try {
    source = new URL(raw);
  } catch {
    return resolveLocal(path.resolve(workspace, raw));
  }

  if (source.protocol === 'file:') return resolveLocal(fileURLToPath(source));
  if (source.username || source.password) throw new Error('nuget-source must not contain embedded credentials.');
  if (source.search || source.hash) throw new Error('nuget-source must not contain query parameters or fragments.');
  if (source.protocol === 'https:') return source.href;
  if (source.protocol === 'http:' && ['localhost', '127.0.0.1', '::1'].includes(source.hostname)) return source.href;
  throw new Error('nuget-source must use HTTPS, localhost HTTP, or a trusted local directory.');
}

export function escapeCommandData(value) {
  return String(value ?? '').replaceAll('%', '%25').replaceAll('\r', '%0D').replaceAll('\n', '%0A');
}

export function escapeCommandProperty(value) {
  return escapeCommandData(value).replaceAll(':', '%3A').replaceAll(',', '%2C');
}

function safeRelativeFile(workspace, value) {
  if (!value) return undefined;
  const absolute = path.isAbsolute(value) ? path.resolve(value) : path.resolve(workspace, value);
  if (!isWithin(workspace, absolute)) return undefined;
  return path.relative(workspace, absolute).split(path.sep).join('/');
}

function replaceWorkspace(value, workspace) {
  let result = String(value ?? '');
  const variants = new Set([
    path.resolve(workspace),
    path.resolve(workspace).split(path.sep).join('/'),
    path.resolve(workspace).split(path.sep).join('\\'),
  ]);
  for (const variant of variants) result = result.replaceAll(variant, '.');
  return result;
}

function positiveLine(value) {
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : undefined;
}

export function annotationFor(diagnostic, workspace) {
  const severity = String(diagnostic?.severity ?? 'information').toLowerCase();
  const level = severity === 'error' ? 'error' : severity === 'warning' ? 'warning' : 'notice';
  const location = diagnostic?.location ?? {};
  const file = safeRelativeFile(workspace, diagnostic?.file ?? location.file ?? location.uri);
  const line = positiveLine(diagnostic?.line ?? location.line ?? location.startLine);
  const code = String(diagnostic?.code ?? 'PackageMedic').trim();
  const titleText = replaceWorkspace(diagnostic?.title ?? 'Dependency diagnostic', workspace).trim();
  const title = code ? `${code}: ${titleText}` : titleText;
  const parts = [diagnostic?.explanation, diagnostic?.evidence, diagnostic?.suggestedAction]
    .map((item) => replaceWorkspace(item, workspace).trim())
    .filter(Boolean);
  const message = (parts.join(' ') || titleText).slice(0, 8000);
  const properties = [`title=${escapeCommandProperty(title)}`];
  if (file) properties.unshift(`file=${escapeCommandProperty(file)}`);
  if (file && line) properties.push(`line=${line}`);
  return `::${level} ${properties.join(',')}::${escapeCommandData(message)}`;
}

export function reportDetails(report) {
  if (!report || typeof report !== 'object' || !Array.isArray(report.diagnostics)) {
    throw new Error('PackageMedic JSON report does not contain a diagnostics array.');
  }

  const counts = { errors: 0, warnings: 0, information: 0 };
  for (const diagnostic of report.diagnostics) {
    const severity = String(diagnostic?.severity ?? 'information').toLowerCase();
    if (severity === 'error') counts.errors += 1;
    else if (severity === 'warning') counts.warnings += 1;
    else counts.information += 1;
  }

  return { counts, diagnostics: report.diagnostics };
}

function markdownCell(value) {
  return String(value ?? '').replaceAll('|', '\\|').replaceAll('\r', ' ').replaceAll('\n', ' ');
}

export function renderSummary(report, details, exitCode, workspace = process.cwd()) {
  const status = exitCode === 0 ? 'Passed' : exitCode === 1 ? 'Threshold reached' : 'Operational error';
  const target = markdownCell(safeRelativeFile(workspace, report?.target) ?? 'Repository');
  const projects = Number(report?.summary?.projects ?? 0);
  const analysisErrors = Array.isArray(report?.analysisErrors) ? report.analysisErrors.length : 0;
  return [
    '## PackageMedic',
    '',
    `**${status}** — exit code \`${exitCode}\``,
    '',
    '| Target | Projects | Errors | Warnings | Information | Analysis errors |',
    '| --- | ---: | ---: | ---: | ---: | ---: |',
    `| ${target} | ${projects} | ${details.counts.errors} | ${details.counts.warnings} | ${details.counts.information} | ${analysisErrors} |`,
    '',
    'Reports are available in the action outputs and, when enabled, the workflow artifact.',
    '',
  ].join('\n');
}
