// Synthetic, read-only evidence from the exact running Ubuntu candidate.
// Does not interpret absent tools or hardening as patches for installed packages.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';

const run=(file,args)=>execFileSync(file,args,{encoding:'utf8'}).trim();
const root='/usr/local/share/devtunnel/native/evidence';
const packages=JSON.parse(fs.readFileSync(`${root}/packages.json`));
assert.equal(packages.length,5);
for(const p of packages) {
  assert.equal(run('dpkg-query',['-W','-f','${Version}',p.package]),p.version);
  for(const file of p.replaced) {
    const actual=createHash('sha256').update(fs.readFileSync(`/${file.installed}`)).digest('hex');
    assert.equal(actual,file.sha256,`Native provenance mismatch: ${file.installed}`);
  }
}
assert.equal(run('dpkg',['--audit']),'');
const directories=['/usr/bin','/usr/sbin','/usr/lib/systemd','/lib/systemd'];
const absent=['avahi-daemon','systemd-journald','genrb','eu-readelf','eu-strip','newuidmap','newgidmap'];
for(const name of absent) for(const dir of directories) assert.equal(fs.existsSync(`${dir}/${name}`),false,`${dir}/${name}`);
assert.equal(fs.readdirSync('/usr/lib').flatMap(d=>{
  const dir=`/usr/lib/${d}`;return d.endsWith('-linux-gnu')?fs.readdirSync(dir):[];
}).some(x=>/^libdw\.so/.test(x)),false);

const processes=[];
for(const pid of fs.readdirSync('/proc').filter(x=>/^\d+$/.test(x))) {
  let status;try{status=fs.readFileSync(`/proc/${pid}/status`,'utf8');}catch(e){if(e.code==='ENOENT')continue;throw e;}
  const name=status.match(/^Name:\s*(.*)$/m)?.[1];
  const uids=status.match(/^Uid:\s*(.*)$/m)?.[1].trim().split(/\s+/).map(Number);
  assert.deepEqual(uids,[1000,1000,1000,1000],`UID/saved UID for ${name}`);
  assert.match(status,/^CapEff:\s*0+$/m,`Capabilities for ${name}`);
  assert.match(status,/^NoNewPrivs:\s*1$/m,`NoNewPrivs for ${name}`);
  processes.push(name);
}
assert.ok(processes.some(x=>x.includes('squid')),'Squid must be running for UID evidence');
console.log(JSON.stringify({nativePackages:packages.length,hashes:packages.reduce((n,p)=>n+p.replaced.length,0),
  absentComponents:absent,libdwAbsent:true,allProcessUids:1000,capabilities:0,noNewPrivileges:true,
  caveat:'Component absence and runtime mitigations are not package vulnerability fixes'}));
