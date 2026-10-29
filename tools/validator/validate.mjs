// Usage: node validate.mjs <file.vrm|file.glb> [...]
// Prints the Khronos glTF-Validator summary and exits 1 if any file has errors.
import { readFileSync } from 'node:fs';
import validator from 'gltf-validator';

let failed = false;
for (const path of process.argv.slice(2)) {
  const report = await validator.validateBytes(new Uint8Array(readFileSync(path)), {
    uri: path,
    maxIssues: 200,
    // VRM extensions are not known to the validator; they are reported as info, not errors.
  });
  const { numErrors, numWarnings, numInfos, numHints } = report.issues;
  console.log(`${path}: ${numErrors} errors, ${numWarnings} warnings, ${numInfos} infos, ${numHints} hints`);
  for (const m of report.issues.messages) {
    if (m.severity <= 2) console.log(`  [${['error', 'warning', 'info'][m.severity]}] ${m.code} ${m.pointer ?? ''}: ${m.message}`);
  }
  if (numErrors > 0) failed = true;
}
process.exit(failed ? 1 : 0);
