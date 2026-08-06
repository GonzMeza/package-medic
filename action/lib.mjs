import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { realpathSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const exactNuGetVersion = /^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/;
export const maximumJsonReportBytes = 256 * 1024 * 1024;

export function isVerifiedRestoreRejection(report) {
  const decision = report?.diff?.verification?.decision;
  return String(decision?.verdict ?? '').trim().toLowerCase() === 'reject' &&
    String(decision?.blockingSnapshot ?? '').trim().toLowerCase() === 'candidate' &&
    String(decision?.blockingStage ?? '').trim().toLowerCase() === 'restore';
}

export function runCommand(executable, args, spawn = spawnSync) {
  const result = spawn(executable, args, { windowsHide: true, stdio: 'inherit' });
  if (result.error) throw result.error;
  return Number.isInteger(result.status) ? result.status : 2;
}

export function validateReportSize(size, maximum = maximumJsonReportBytes) {
  if (!Number.isSafeInteger(size) || size < 0) {
    throw new Error('PackageMedic JSON report size is invalid.');
  }
  if (!Number.isSafeInteger(maximum) || maximum < 1) {
    throw new Error('PackageMedic JSON report limit is invalid.');
  }
  if (size > maximum) {
    throw new Error(`PackageMedic JSON report exceeds the ${maximum}-byte safety limit.`);
  }
  return size;
}

export function parseBoolean(value, name) {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (normalized === 'true') return true;
  if (normalized === 'false') return false;
  throw new Error(`${name} must be 'true' or 'false'.`);
}

export function parseAnnotationMode(value) {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (normalized === 'true' || normalized === 'all') return 'all';
  if (normalized === 'false' || normalized === 'none') return 'none';
  if (normalized === 'new') return 'new';
  throw new Error("annotations must be 'new', 'all', or 'none' (true/false remain supported).");
}

export function validateExactVersion(value) {
  const version = String(value ?? '').trim();
  if (!exactNuGetVersion.test(version) || version.includes('*')) {
    throw new Error('tool-version must be an exact NuGet version, for example 0.6.0.');
  }
  return version;
}

export function validateGitReference(value) {
  const reference = String(value ?? '').trim();
  if (!reference) return undefined;
  if (reference.length > 512 || reference.startsWith('-') || /[\u0000-\u0020\u007f]/u.test(reference)) {
    throw new Error('diff-base must be a Git reference without whitespace, control characters, or a leading hyphen.');
  }
  return reference;
}

export function resolveAnalysisMode(value, explicitBase, eventName, pullRequestBaseSha) {
  const mode = enumValue(value || 'auto', 'mode', ['auto', 'scan', 'diff']);
  const event = String(eventName ?? '').trim().toLowerCase();
  if (event === 'pull_request_target') {
    throw new Error(
      'PackageMedic does not analyze pull_request_target because repository-controlled restore, MSBuild, analyzers, and tests must not run with its privileged context; use the unprivileged pull_request event instead.');
  }

  const requestedBase = validateGitReference(explicitBase);
  if (requestedBase) return { mode: 'diff', diffBase: requestedBase, automatic: false };
  if (mode === 'scan') return { mode: 'scan', diffBase: undefined, automatic: false };
  if (mode === 'diff') {
    throw new Error("mode 'diff' requires diff-base because no implicit comparison reference is assumed.");
  }

  if (event === 'pull_request') {
    const diffBase = validateGitReference(pullRequestBaseSha);
    if (!diffBase) throw new Error('Pull request auto mode could not resolve github.event.pull_request.base.sha.');
    return { mode: 'diff', diffBase, automatic: true };
  }

  return { mode: 'scan', diffBase: undefined, automatic: true };
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

export function resolveOptionalWorkspaceFile(workspace, inputPath, name) {
  const raw = String(inputPath ?? '').trim();
  if (!raw) return undefined;
  const candidate = path.resolve(workspace, raw);
  if (!isWithin(workspace, candidate)) {
    throw new Error(`${name} must resolve inside GITHUB_WORKSPACE.`);
  }

  let realCandidate;
  try {
    realCandidate = realpathSync(candidate);
  } catch {
    throw new Error(`${name} must name an existing file.`);
  }
  if (!isWithin(realpathSync(workspace), realCandidate)) {
    throw new Error(`${name} cannot resolve through a symbolic link outside GITHUB_WORKSPACE.`);
  }
  if (!statSync(realCandidate).isFile()) {
    throw new Error(`${name} must name a regular file.`);
  }
  return realCandidate;
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
  return String(value ?? '')
    .replaceAll(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '')
    .replaceAll('%', '%25')
    .replaceAll('\r', '%0D')
    .replaceAll('\n', '%0A');
}

export function escapeCommandProperty(value) {
  return escapeCommandData(value).replaceAll(':', '%3A').replaceAll(',', '%2C');
}

export function escapeMarkdown(value) {
  return String(value ?? '')
    .replaceAll(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '')
    .replaceAll('\\', '\\\\')
    .replaceAll(/([`*_[\]<>|])/g, '\\$1')
    .replaceAll('\r', ' ')
    .replaceAll('\n', ' ');
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

  const count = (value) => {
    const parsed = Number(value);
    return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : 0;
  };
  const classifiedNew = report.diagnostics.filter(
    (diagnostic) => String(diagnostic?.baselineState ?? '').toLowerCase() === 'new').length;
  const classifiedExisting = report.diagnostics.filter(
    (diagnostic) => String(diagnostic?.baselineState ?? '').toLowerCase() === 'existing').length;
  const hasBaselineSummary = report.baseline && typeof report.baseline === 'object';
  const hasBaselineStates = classifiedNew + classifiedExisting > 0;
  const legacyUnclassified = !hasBaselineSummary && !hasBaselineStates;
  const suppressedDiagnostics = Array.isArray(report.suppressedDiagnostics) ? report.suppressedDiagnostics : [];
  const baseline = {
    new: count(report.baseline?.new ?? (legacyUnclassified ? report.diagnostics.length : classifiedNew)),
    existing: count(report.baseline?.existing ?? classifiedExisting),
    resolved: count(report.baseline?.resolved),
  };
  const suppressed = count(report.policy?.suppressed ?? suppressedDiagnostics.length);

  const verification = report.diff?.verification && typeof report.diff.verification === 'object'
    ? (() => {
        const value = report.diff.verification;
        const verdict = String(value.decision?.verdict ?? '').trim().toLowerCase();
        const blockingSnapshot = String(value.decision?.blockingSnapshot ?? '').trim().toLowerCase();
        const blockingStage = String(value.decision?.blockingStage ?? '').trim().toLowerCase();
        return {
          level: String(value.level ?? '').trim().toLowerCase(),
          status: verdict,
          buildRegression: verdict === 'reject' && blockingSnapshot === 'candidate' && blockingStage === 'build',
          testRegression: verdict === 'reject' && blockingSnapshot === 'candidate' && blockingStage === 'test',
          testsPassed: count(value.candidate?.tests?.passed),
          testsFailed: count(value.candidate?.tests?.failed),
          testsSkipped: count(value.candidate?.tests?.skipped),
          incomplete: verdict === 'incomplete',
          baselineBuild: String(value.baseline?.build?.stage?.status ?? 'notRequested').trim(),
          candidateBuild: String(value.candidate?.build?.stage?.status ?? 'notRequested').trim(),
          baselineTests: String(value.baseline?.tests?.stage?.status ?? 'notRequested').trim(),
          candidateTests: String(value.candidate?.tests?.stage?.status ?? 'notRequested').trim(),
        };
      })()
    : undefined;

  const diff = report.diff && typeof report.diff === 'object'
    ? {
        added: count(report.diff.summary?.added),
        resolved: count(report.diff.summary?.resolved),
        severityChanged: count(report.diff.summary?.severityChanged),
        packageChanges: Array.isArray(report.diff.packageChanges) ? report.diff.packageChanges.length : 0,
        packagesAdded: count(report.diff.packageSummary?.added),
        packagesRemoved: count(report.diff.packageSummary?.removed),
        packagesUpgraded: count(report.diff.packageSummary?.upgraded),
        packagesDowngraded: count(report.diff.packageSummary?.downgraded),
        uncomparableVersionChanges: count(report.diff.packageSummary?.uncomparableVersionChanges),
        directToTransitive: count(report.diff.packageSummary?.directToTransitive),
        transitiveToDirect: count(report.diff.packageSummary?.transitiveToDirect),
        vulnerabilitiesIntroduced: count(report.diff.riskSummary?.vulnerabilitiesIntroduced),
        vulnerabilitiesResolved: count(report.diff.riskSummary?.vulnerabilitiesResolved),
        vulnerabilitiesPersistent: count(report.diff.riskSummary?.vulnerabilitiesPersistent),
        deprecationsIntroduced: count(report.diff.riskSummary?.deprecationsIntroduced),
        deprecationsResolved: count(report.diff.riskSummary?.deprecationsResolved),
        deprecationsPersistent: count(report.diff.riskSummary?.deprecationsPersistent),
        projectSettingsChanges: Array.isArray(report.diff.projectSettingsChanges) ? report.diff.projectSettingsChanges.length : 0,
        complete: report.diff.isComplete !== false,
        baselineAnalysisErrors: Array.isArray(report.diff.baselineAnalysisErrors) ? report.diff.baselineAnalysisErrors.length : 0,
        currentAnalysisErrors: Array.isArray(report.diff.currentAnalysisErrors) ? report.diff.currentAnalysisErrors.length : 0,
        verification,
        impact: report.diff.impact && typeof report.diff.impact === 'object'
          ? {
              gatePassed: report.diff.impact.gatePassed === true,
              violations: Array.isArray(report.diff.impact.violations) ? report.diff.impact.violations.length : 0,
              addedDirect: count(report.diff.impact.summary?.addedDirectPackages),
              addedTransitive: count(report.diff.impact.summary?.addedTransitivePackages),
              maximumBlastRadius: count(report.diff.impact.summary?.maximumBlastRadius),
              sourceChanges: count(report.diff.impact.summary?.sourceChanges),
              contentChanges: count(report.diff.impact.summary?.contentChanges),
              violationDetails: Array.isArray(report.diff.impact.violations)
                ? report.diff.impact.violations.slice(0, 20)
                : [],
              omittedViolations: Array.isArray(report.diff.impact.violations)
                ? Math.max(0, report.diff.impact.violations.length - 20)
                : 0,
            }
          : undefined,
      }
    : undefined;

  return { counts, diagnostics: report.diagnostics, baseline, suppressed, suppressedDiagnostics, diff };
}

export function diagnosticsForAnnotations(details, mode) {
  if (mode === 'none') return [];
  if (mode === 'all') return details.diagnostics;
  if (mode === 'new') {
    return details.diagnostics.filter(
      (diagnostic) => String(diagnostic?.baselineState ?? '').toLowerCase() !== 'existing');
  }
  throw new Error(`Unknown annotation mode '${mode}'.`);
}

function markdownCell(value) {
  return escapeMarkdown(value);
}

function boundedMarkdown(value, maximum = 600) {
  return markdownCell(String(value ?? '').slice(0, maximum));
}

function impactViolationLine(violation) {
  const code = String(violation?.code || 'Impact-policy')
    .replaceAll(/[^0-9A-Za-z._-]+/g, '-')
    .slice(0, 64) || 'Impact-policy';
  const message = boundedMarkdown(violation?.message || 'Dependency impact policy violation.');
  const context = [
    violation?.project && `project ${boundedMarkdown(violation.project, 260)}`,
    violation?.framework && `framework ${boundedMarkdown(violation.framework, 80)}`,
    violation?.packageId && `package ${boundedMarkdown(violation.packageId, 160)}`,
    violation?.rootPackageId && `root ${boundedMarkdown(violation.rootPackageId, 160)}`,
  ].filter(Boolean);
  const pathSegments = Array.isArray(violation?.dependencyPath)
    ? violation.dependencyPath.slice(0, 32).map((segment) => {
        const id = boundedMarkdown(segment?.packageId, 160);
        const version = boundedMarkdown(segment?.resolvedVersion, 80);
        return version ? `${id}@${version}` : id;
      }).filter(Boolean)
    : [];
  const pathText = pathSegments.length > 0 ? ` Path: ${pathSegments.join(' → ')}.` : '';
  const action = violation?.suggestedAction
    ? ` Suggested action: ${boundedMarkdown(violation.suggestedAction)}.`
    : '';
  return `- \`${code}\` ${message}${context.length > 0 ? ` (${context.join(', ')})` : ''}.${pathText}${action}`;
}

export function renderSummary(report, details, exitCode, workspace = process.cwd()) {
  const status = exitCode === 0 ? 'Passed' : exitCode === 1 ? 'Threshold reached' : 'Operational error';
  const target = markdownCell(safeRelativeFile(workspace, report?.target) || 'Repository');
  const projects = Number(report?.summary?.projects ?? 0);
  const analysisErrors = Array.isArray(report?.analysisErrors) ? report.analysisErrors.length : 0;
  const summary = [
    '## PackageMedic',
    '',
    `**${status}** — exit code \`${exitCode}\``,
    '',
    '| Target | Projects | Errors | Warnings | Information | Analysis errors |',
    '| --- | ---: | ---: | ---: | ---: | ---: |',
    `| ${target} | ${projects} | ${details.counts.errors} | ${details.counts.warnings} | ${details.counts.information} | ${analysisErrors} |`,
    '',
    '| New | Existing | Resolved | Suppressed |',
    '| ---: | ---: | ---: | ---: |',
    `| ${details.baseline.new} | ${details.baseline.existing} | ${details.baseline.resolved} | ${details.suppressed} |`,
    '',
    'Reports are available in the action outputs and, when enabled, the workflow artifact.',
    '',
  ];
  if (details.diff) {
    if (!details.diff.complete) {
      summary.splice(
        summary.length - 2,
        0,
        `**Comparison incomplete** — ${details.diff.baselineAnalysisErrors} base analysis error(s), ${details.diff.currentAnalysisErrors} current analysis error(s).`,
        '');
    }
    summary.splice(
      summary.length - 2,
      0,
      '| Added findings | Resolved findings | Severity changed | Package changes | CPM changes |',
      '| ---: | ---: | ---: | ---: | ---: |',
      `| ${details.diff.added} | ${details.diff.resolved} | ${details.diff.severityChanged} | ${details.diff.packageChanges} | ${details.diff.projectSettingsChanges} |`,
      '',
      '| Packages added | Removed | Upgraded | Downgraded | Direct → transitive | Transitive → direct |',
      '| ---: | ---: | ---: | ---: | ---: | ---: |',
      `| ${details.diff.packagesAdded} | ${details.diff.packagesRemoved} | ${details.diff.packagesUpgraded} | ${details.diff.packagesDowngraded} | ${details.diff.directToTransitive} | ${details.diff.transitiveToDirect} |`,
      '',
      '| Vulnerabilities introduced | Resolved | Deprecations introduced | Resolved |',
      '| ---: | ---: | ---: | ---: |',
      `| ${details.diff.vulnerabilitiesIntroduced} | ${details.diff.vulnerabilitiesResolved} | ${details.diff.deprecationsIntroduced} | ${details.diff.deprecationsResolved} |`,
      '');

    if (details.diff.vulnerabilitiesPersistent > 0 || details.diff.deprecationsPersistent > 0) {
      summary.splice(
        summary.length - 2,
        0,
        '| Vulnerabilities persistent | Deprecations persistent |',
        '| ---: | ---: |',
        `| ${details.diff.vulnerabilitiesPersistent} | ${details.diff.deprecationsPersistent} |`,
        '');
    }

    if (details.diff.impact) {
      const impact = details.diff.impact;
      summary.splice(
        summary.length - 2,
        0,
        `### Impact Gate: ${impact.gatePassed ? 'Passed' : 'Blocked'}`,
        '',
        '| Violations | Added direct | Added transitive | Maximum blast radius | Source changes | Content changes |',
        '| ---: | ---: | ---: | ---: | ---: | ---: |',
        `| ${impact.violations} | ${impact.addedDirect} | ${impact.addedTransitive} | ${impact.maximumBlastRadius} | ${impact.sourceChanges} | ${impact.contentChanges} |`,
        '');
      if (impact.violationDetails.length > 0) {
        summary.splice(
          summary.length - 2,
          0,
          ...impact.violationDetails.map(impactViolationLine),
          ...(impact.omittedViolations > 0
            ? [`- ${impact.omittedViolations} additional violation(s) are available in the JSON report.`]
            : []),
          '');
      }
    }

    if (details.diff.verification) {
      const verification = details.diff.verification;
      summary.splice(
        summary.length - 2,
        0,
        `### Verification: ${boundedMarkdown(verification.status || 'unknown', 64)}`,
        '',
        '| Level | Baseline build | Candidate build | Baseline tests | Candidate tests |',
        '| --- | --- | --- | --- | --- |',
        `| ${boundedMarkdown(verification.level || 'unknown', 64)} | ${boundedMarkdown(verification.baselineBuild, 64)} | ${boundedMarkdown(verification.candidateBuild, 64)} | ${boundedMarkdown(verification.baselineTests, 64)} | ${boundedMarkdown(verification.candidateTests, 64)} |`,
        '',
        '| Candidate tests passed | Failed | Skipped |',
        '| ---: | ---: | ---: |',
        `| ${verification.testsPassed} | ${verification.testsFailed} | ${verification.testsSkipped} |`,
        '');
      if (verification.incomplete) {
        summary.splice(
          summary.length - 2,
          0,
          '**Verification incomplete** — the requested comparison did not produce reliable evidence for every required stage.',
          '');
      }
    }
  }

  return summary.join('\n');
}
