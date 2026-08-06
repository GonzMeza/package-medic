import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import process from 'node:process';

assert.equal(process.env.PACKAGEMEDIC_EXIT_CODE, '0');
assert.equal(process.env.PACKAGEMEDIC_VERIFICATION_STATUS, 'noChange');
assert.equal(process.env.PACKAGEMEDIC_VERIFICATION_INCOMPLETE, 'false');
assert.equal(process.env.PACKAGEMEDIC_SBOM_CREATED, 'true');
assert.equal(process.env.PACKAGEMEDIC_PROVENANCE_CREATED, 'true');

for (const file of [
  process.env.PACKAGEMEDIC_SBOM_FILE,
  process.env.PACKAGEMEDIC_PROVENANCE_FILE,
]) {
  assert.ok(file && existsSync(file), `Verification evidence is missing: ${file ?? '<unset>'}`);
  assert.doesNotThrow(() => JSON.parse(readFileSync(file, 'utf8')));
}
