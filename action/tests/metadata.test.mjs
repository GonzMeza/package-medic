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
    'annotations',
    'upload-sarif',
    'upload-artifact',
  ]) {
    assert.match(metadata, new RegExp(`^  ${input}:$`, 'm'));
  }
  for (const output of ['exit-code', 'json-file', 'sarif-file', 'errors', 'warnings', 'information', 'artifact-name', 'sarif-category']) {
    assert.match(metadata, new RegExp(`^  ${output}:$`, 'm'));
  }
  assert.match(metadata, /github\/codeql-action\/upload-sarif@v4/);
  assert.match(metadata, /actions\/upload-artifact@v4/);
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
  assert.match(runner, /runCommand\(executable/);
  assert.doesNotMatch(runner, /spawnSync|result\.stdout|result\.stderr/);
  assert.doesNotMatch(runner, /sarifArguments|sarifExit/);
});

test('all repository workflows use the same checkout major version', () => {
  const workflows = ['ci.yml', 'pages.yml', 'release.yml']
    .map((name) => readFileSync(path.join(repository, '.github', 'workflows', name), 'utf8'))
    .join('\n');
  assert.match(workflows, /actions\/checkout@v6/);
  assert.doesNotMatch(workflows, /actions\/checkout@v[1-5]/);
});

test('all local scripts referenced by action metadata exist', () => {
  const scripts = [...metadata.matchAll(/\$GITHUB_ACTION_PATH\/(action\/[^"\s]+)/g)].map((match) => match[1]);
  assert.deepEqual(scripts.sort(), ['action/finalize.mjs', 'action/run.mjs']);
  for (const script of scripts) assert.equal(existsSync(path.join(repository, script)), true, `${script} is missing`);
});

test('release workflow validates tags, builds assets, and never publishes to NuGet', () => {
  const release = readFileSync(path.join(repository, '.github', 'workflows', 'release.yml'), 'utf8');
  assert.match(release, /node action\/prepare-release\.mjs/);
  assert.match(release, /dotnet restore PackageMedic\.sln/);
  assert.match(release, /dotnet build PackageMedic\.sln --configuration Release --no-restore/);
  assert.match(release, /dotnet test PackageMedic\.sln --configuration Release --no-build --no-restore/);
  assert.match(release, /dotnet pack src\/PackageMedic\.Cli\/PackageMedic\.Cli\.csproj/);
  assert.match(release, /node action\/checksum\.mjs artifacts\/release/);
  assert.match(release, /gh release create/);
  assert.match(release, /--verify-tag/);
  assert.match(release, /^permissions:\n  contents: read$/m);
  assert.match(release, /^    permissions:\n      contents: write$/m);
  assert.doesNotMatch(release, /dotnet nuget push|NUGET_API_KEY|api-key/i);
});
