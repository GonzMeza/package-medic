import process from 'node:process';

const parsed = Number(process.env.PACKAGEMEDIC_EXIT_CODE);
const exitCode = parsed === 0 || parsed === 1 || parsed === 2 ? parsed : 2;
if (exitCode === 2 && !process.env.PACKAGEMEDIC_EXIT_CODE) {
  process.stderr.write('PackageMedic action did not produce an exit code.\n');
}
process.exitCode = exitCode;
