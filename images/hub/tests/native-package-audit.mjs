import fs from 'node:fs';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import assert from 'node:assert/strict';

// Run only in a disposable native build stage with lintian and abigail-tools.
// ELF-only comparison is not a full debug-type ABI or independent human review.
const output = process.env.HUB_NATIVE_AUDIT_OUTPUT ?? '/tmp/package-audit';
fs.mkdirSync(output, {recursive:true});
const run = (command, args) => {
  const result = spawnSync(command, args, {encoding:'utf8', timeout:120000});
  if (result.error) throw result.error;
  assert.equal(result.signal,null,`${command} terminated by signal`);
  assert.ok(Number.isInteger(result.status),`${command} did not return an exit code`);
  return {status:result.status, stdout:result.stdout, stderr:result.stderr};
};
const evidence = JSON.parse(fs.readFileSync('/native/evidence/packages.json', 'utf8'));
assert.deepEqual(evidence.map(item=>item.package).sort(),
  ['libexpat1','libglib2.0-0t64','libp11-kit0','p11-kit','p11-kit-modules']);
assert.equal(evidence.flatMap(item=>item.replaced).length,17,'Incomplete ELF inventory');
const results = [];
for (const item of evidence) {
  const originalDir = `/native/download-${item.package}`;
  const originalDeb = path.join(originalDir,fs.readdirSync(originalDir).find(x=>x.endsWith('.deb')));
  const original = `${output}/original-${item.package}`;
  const extracted = run('dpkg-deb',['-x',originalDeb,original]);
  assert.equal(extracted.status,0,extracted.stderr);
  const replacement = `/native/debs/${item.package}_${item.version}_${item.arch}.deb`;
  const lintOriginal = run('lintian',['--no-cfg',originalDeb]);
  const lintReplacement = run('lintian',['--no-cfg',replacement]);
  fs.writeFileSync(`${output}/${item.package}-original-lintian.log`,lintOriginal.stdout+lintOriginal.stderr);
  fs.writeFileSync(`${output}/${item.package}-replacement-lintian.log`,lintReplacement.stdout+lintReplacement.stderr);
  const abi = [];
  for (const entry of item.replaced) {
    const before = path.join(original,entry.file);
    const after = path.join(`/native/package-${item.package}`,entry.installed);
    const dynamic = run('readelf',['-d',before]);
    assert.equal(dynamic.status,0,`readelf failed for ${entry.file}`);
    if (!dynamic.stdout.includes('(SONAME)')) continue;
    const compared = run('abidiff',['--no-default-suppression',before,after]);
    const filename = `${item.package}-${path.basename(entry.installed)}-abidiff.log`;
    fs.writeFileSync(path.join(output,filename),compared.stdout+compared.stderr);
    abi.push({file:entry.installed,status:compared.status,log:filename,
      incompatible:!!(compared.status & 8),toolError:!!(compared.status & 3)});
  }
  results.push({package:item.package,version:item.version,
    lintOriginal:lintOriginal.status,lintReplacement:lintReplacement.status,abi});
}
fs.writeFileSync(`${output}/summary.json`,JSON.stringify(results,null,2)+'\n');
console.log(JSON.stringify(results,null,2));
assert.equal(results.flatMap(item=>item.abi).length,10,'Incomplete shared-library comparison');
for (const item of results) {
  assert.equal(item.lintReplacement,0,`Lintian rejected ${item.package}`);
  for (const comparison of item.abi)
    assert.ok(!comparison.incompatible && !comparison.toolError, `ABI check failed: ${comparison.log}`);
}
