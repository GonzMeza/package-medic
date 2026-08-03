import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import process from 'node:process';

assert.equal(process.env.PACKAGEMEDIC_EXIT_CODE, '0');
assert.equal(process.env.PACKAGEMEDIC_ERRORS, '0');
assert.equal(process.env.PACKAGEMEDIC_WARNINGS, '0');
assert.ok(existsSync(process.env.PACKAGEMEDIC_JSON_FILE), 'JSON report is missing.');
assert.ok(existsSync(process.env.PACKAGEMEDIC_SARIF_FILE), 'SARIF report is missing.');
assert.doesNotThrow(() => JSON.parse(readFileSync(process.env.PACKAGEMEDIC_JSON_FILE, 'utf8')));
assert.doesNotThrow(() => JSON.parse(readFileSync(process.env.PACKAGEMEDIC_SARIF_FILE, 'utf8')));
