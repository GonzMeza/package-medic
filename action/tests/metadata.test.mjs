import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const metadataPath = path.join(repository, 'action.yml');
const metadata = readFileSync(metadataPath, 'utf8');

test('action metadata declares a composite action and public contract', () => {
  assert.match(metadata, /^name:\s+PackageMedic$/m);
  assert.match(metadata, /^\s+using:\s+composite$/m);
  for (const input of [
    'path',
    'tool-version',
    'restore',
    'fail-on',
    'fail-on-new',
    'config',
    'baseline',
    'audit',
    'include-transitive-audit',
    'diff-base',
    'max-parallelism',
    'annotations',
    'upload-sarif',
    'upload-artifact',
  ]) {
    assert.match(metadata, new RegExp(`^  ${input}:$`, 'm'));
  }
  for (const output of ['exit-code', 'json-file', 'sarif-file', 'errors', 'warnings', 'information', 'artifact-name', 'sarif-category']) {
    assert.match(metadata, new RegExp(`^  ${output}:$`, 'm'));
  }
  assert.match(metadata, /github\/codeql-action\/upload-sarif@[0-9a-f]{40} # v4/);
  assert.match(metadata, /actions\/upload-artifact@[0-9a-f]{40} # v4/);
  assert.doesNotMatch(metadata, /\t/);
  const repositoryVersion = readFileSync(path.join(repository, 'VERSION'), 'utf8').trim();
  assert.match(metadata, new RegExp(`tool-version:[\\s\\S]*?default: ${repositoryVersion.replaceAll('.', '\\.')}\\s`));
});

test('action creates JSON and SARIF from one PackageMedic analysis', () => {
  const runner = readFileSync(path.join(repository, 'action', 'run.mjs'), 'utf8');
  assert.match(runner, /'--format', 'json',[\s\S]*'--sarif-output', sarifFile/);
  assert.match(runner, /baseArguments\.push\('--config', configFile\)/);
  assert.match(runner, /baseArguments\.push\('--baseline', baselineFile\)/);
  assert.match(runner, /baseArguments\.push\('--fail-on-new', failOnNew\)/);
  assert.match(runner, /\['diff', diffBase, scanPath/);
  assert.match(runner, /baseArguments\.push\('--audit'\)/);
  assert.match(runner, /baseArguments\.push\('--include-transitive'\)/);
  assert.match(runner, /baseArguments\.push\('--max-parallelism', String\(maxParallelism\)\)/);
  assert.match(runner, /runCommand\(executable/);
  assert.doesNotMatch(runner, /spawnSync|result\.stdout|result\.stderr/);
  assert.doesNotMatch(runner, /sarifArguments|sarifExit/);
});

test('all repository workflows pin checkout to the same immutable commit', () => {
  const workflows = ['ci.yml', 'pages.yml', 'release.yml']
    .map((name) => readFileSync(path.join(repository, '.github', 'workflows', name), 'utf8'))
    .join('\n');
  const checkoutUses = [...workflows.matchAll(/actions\/checkout@([^\s]+)/g)].map((match) => match[1]);
  assert.ok(checkoutUses.length >= 3);
  assert.deepEqual(new Set(checkoutUses), new Set(['d23441a48e516b6c34aea4fa41551a30e30af803']));
  assert.equal((workflows.match(/persist-credentials: false/g) ?? []).length, checkoutUses.length);
  assert.doesNotMatch(workflows, /uses:\s+[^\s]+@v\d+/);
});

test('website workflow enforces the npm supply-chain gates', () => {
  const pages = readFileSync(path.join(repository, '.github', 'workflows', 'pages.yml'), 'utf8');
  assert.match(pages, /npm run audit:lockfile/);
  assert.match(pages, /npm ci --ignore-scripts/);
  assert.match(pages, /npm run audit:signatures/);
  assert.match(pages, /npm run audit:security/);
  assert.match(pages, /npm run lint/);
  assert.match(pages, /npm test/);
  const [buildSection, deploySection] = pages.split('\n  deploy:');
  assert.match(buildSection, /build:[\s\S]*?permissions:\s+contents: read/);
  assert.doesNotMatch(buildSection, /pages: write|id-token: write/);
  assert.match(deploySection, /permissions:\s+pages: write\s+id-token: write/);

  const dependabot = readFileSync(path.join(repository, '.github', 'dependabot.yml'), 'utf8');
  for (const ecosystem of ['npm', 'nuget', 'github-actions']) {
    assert.match(dependabot, new RegExp(`package-ecosystem: ${ecosystem}`));
  }
});

test('CI smoke-tests local Action diff mode', () => {
  const workflow = readFileSync(path.join(repository, '.github', 'workflows', 'ci.yml'), 'utf8');
  assert.match(workflow, /name: Smoke-test local GitHub Action diff mode/);
  assert.match(workflow, /diff-base: HEAD/);
  assert.match(workflow, /name: Verify Action diff reports/);
});

test('all local scripts referenced by action metadata exist', () => {
  const scripts = [...metadata.matchAll(/\$GITHUB_ACTION_PATH\/(action\/[^"\s]+)/g)].map((match) => match[1]);
  assert.deepEqual(scripts.sort(), ['action/finalize.mjs', 'action/run.mjs']);
  for (const script of scripts) assert.equal(existsSync(path.join(repository, script)), true, `${script} is missing`);
});

test('release workflow validates tags, builds assets, and never publishes to NuGet', () => {
  const release = readFileSync(path.join(repository, '.github', 'workflows', 'release.yml'), 'utf8')
    .replaceAll('\r\n', '\n');
  assert.match(release, /node action\/prepare-release\.mjs/);
  assert.match(release, /dotnet restore PackageMedic\.sln --locked-mode/);
  assert.match(release, /dotnet build PackageMedic\.sln --configuration Release --no-restore/);
  assert.match(release, /dotnet test PackageMedic\.sln --configuration Release --no-build --no-restore/);
  assert.match(release, /audit PackageMedic\.sln --no-restore --include-transitive --fail-on warning/);
  assert.match(release, /dotnet pack src\/PackageMedic\.Cli\/PackageMedic\.Cli\.csproj/);
  assert.match(release, /node action\/checksum\.mjs artifacts\/release/);
  assert.match(release, /gh release create/);
  assert.match(release, /--verify-tag/);
  assert.match(release, /^permissions:\n  contents: read$/m);
  assert.match(release, /^    permissions:\n      contents: write$/m);
  assert.doesNotMatch(release, /dotnet nuget push|NUGET_API_KEY|api-key/i);
});

test('tool runtime supports compatible newer major .NET installations', () => {
  const project = readFileSync(path.join(repository, 'src', 'PackageMedic.Cli', 'PackageMedic.Cli.csproj'), 'utf8');
  const globalJson = JSON.parse(readFileSync(path.join(repository, 'global.json'), 'utf8'));
  assert.match(project, /<TargetFramework>net8\.0<\/TargetFramework>/);
  assert.match(project, /<RollForward>Major<\/RollForward>/);
  assert.equal(globalJson.sdk.version, '9.0.308');
  assert.equal(globalJson.sdk.rollForward, 'major');
  assert.equal(globalJson.sdk.allowPrerelease, false);
});
