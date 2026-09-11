// Preserve Ubuntu package ownership, licenses, triggers and configuration.
// Replacement is fail-closed: every packaged ELF must have a rebuilt counterpart,
// and every public symbol of each existing shared library must remain available.
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';

const run = (cmd, args, options = {}) => execFileSync(cmd, args, {encoding:'utf8', ...options}).trim();
const stage = '/native/stage';
const arch = run('dpkg', ['--print-architecture']);
const families = [
  {source:'expat', version:'2.8.4-0devtunnel1', packages:['libexpat1']},
  {source:'glib2.0', version:'2.88.3-0devtunnel1', packages:['libglib2.0-0t64']},
  {source:'p11-kit', version:'0.26.5-0devtunnel1', packages:['libp11-kit0','p11-kit','p11-kit-modules']},
];
function files(dir) {
  return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e => {
    const file=path.join(dir,e.name);
    return e.isDirectory() ? files(file) : [file];
  });
}
const staged=files(stage);
fs.mkdirSync('/native/control/debian',{recursive:true});
fs.writeFileSync('/native/control/debian/control','Source: devtunnel-native-validation\nSection: libs\nPriority: optional\nMaintainer: DevTunnel Toolkit <matheuskshn@gmail.com>\n\nPackage: devtunnel-native-validation\nArchitecture: any\nDescription: Local native dependency validation\n');
function elf(file) {
  return !fs.lstatSync(file).isSymbolicLink() && fs.readFileSync(file).subarray(0,4).equals(Buffer.from([127,69,76,70]));
}
function symbols(file) {
  return new Set(run('nm',['-D','--defined-only','--format=posix',file]).split('\n').filter(Boolean)
    .map(line=>line.split(' ')[0]));
}
// Keep the dynamic ABI, but remove static/debug symbols from runtime payloads.
for (const file of staged.filter(elf)) run('strip',['--strip-unneeded',file]);
const evidence=[];
for(const family of families) for(const name of family.packages) {
  const dir=`/native/package-${name}`;
  const originalVersion=run('dpkg-query',['-W','-f','${Version}',name]);
  const download=`/native/download-${name}`;fs.mkdirSync(download);
  run('apt-get',['download',`${name}=${originalVersion}`],{cwd:download});
  const debs=fs.readdirSync(download).filter(x=>x.endsWith('.deb'));assert.equal(debs.length,1);
  run('dpkg-deb',['--raw-extract',path.join(download,debs[0]),dir]);
  const replaced=[];
  for(const old of files(dir).filter(f=>!f.includes('/DEBIAN/')&&elf(f))) {
    const relative=path.relative(dir,old);
    const soname=run('readelf',['-d',old]).match(/\(SONAME\).*\[([^\]]+)\]/)?.[1];
    let next, installed=relative;
    if(soname) {
      const matches=staged.filter(f=>path.basename(f)===soname);
      assert.equal(matches.length,1,`Ambiguous/missing SONAME ${soname}`);
      next=fs.realpathSync(matches[0]);
      const rebuiltSoname=run('readelf',['-d',next]).match(/\(SONAME\).*\[([^\]]+)\]/)?.[1];
      assert.equal(rebuiltSoname,soname,`ABI SONAME changed for ${relative}`);
      const before=symbols(old), after=symbols(next);
      assert.deepEqual([...before].filter(s=>!after.has(s)),[],`ABI symbols removed from ${relative}`);
      // Delete only this old library, inside this newly extracted package.
      fs.unlinkSync(old);
      const target=path.join(path.dirname(old),path.basename(next));
      installed=path.relative(dir,target);
      fs.copyFileSync(next,target);fs.chmodSync(target,0o644);
      const link=path.join(path.dirname(old),soname);
      // PKCS#11 modules can have SONAME == filename, not a versioned symlink.
      if(link!==target) {
        if(fs.existsSync(link)||fs.lstatSync(link,{throwIfNoEntry:false})) fs.unlinkSync(link);
        fs.symlinkSync(path.basename(next),link);
      }
    } else {
      next=path.join(stage,relative);
      if(!fs.existsSync(next)) {
        const matches=staged.filter(f=>path.basename(f)===path.basename(old)&&elf(f));
        assert.equal(matches.length,1,`Ambiguous/missing executable ${relative}`);next=matches[0];
      }
      fs.copyFileSync(next,old);
    }
    replaced.push({file:relative,installed,rebuilt:path.relative(stage,next),sha256:createHash('sha256').update(fs.readFileSync(next)).digest('hex')});
  }
  assert.ok(replaced.length,`No native replacements for ${name}`);
  const control=path.join(dir,'DEBIAN/control');
  let metadata=fs.readFileSync(control,'utf8');
  metadata=metadata.replace(/^Version:.*$/m,`Version: ${family.version}`)
    .replace(/^Source:.*$/m,`Source: ${family.source} (${family.version})`)
    .replaceAll(`= ${originalVersion}`,`= ${family.version}`);
  if(!/^Source:/m.test(metadata)) metadata+=`Source: ${family.source} (${family.version})\n`;
  metadata+='Origin: DevTunnel Toolkit local source build\n';
  // Calculate actual dependencies of the rebuilt ELFs, keeping original package
  // dependencies as well. No dependency removal or forced dpkg installation.
  const elfs=files(dir).filter(f=>!f.includes('/DEBIAN/')&&elf(f));
  assert.equal(elfs.length,replaced.length,`Lost ELF payload in ${name}`);
  const subst=run('dpkg-shlibdeps',['-O',`-x${name}`,...elfs.map(f=>`-e${f}`)],{cwd:'/native/control'});
  const deps=subst.match(/^shlibs:Depends=(.*)$/m)?.[1];assert.ok(deps,`Missing dependencies for ${name}`);
  metadata=metadata.replace(/^Depends: (.*)$/m,(_,old)=>`Depends: ${old}, ${deps}`);
  fs.writeFileSync(control,metadata);
  const provenance={package:name,source:family.source,version:family.version,originalVersion,arch,replaced};
  // Some distro packages use a doc-directory symlink into another package.
  // Never follow it while assembling this independent replacement package.
  const doc=path.join(dir,'usr/share/doc/devtunnel-native');fs.mkdirSync(doc,{recursive:true});
  fs.writeFileSync(path.join(doc,`${name}.json`),JSON.stringify(provenance,null,2)+'\n');
  const payload=files(dir).filter(f=>!f.includes('/DEBIAN/')&&!fs.lstatSync(f).isSymbolicLink());
  const installedSize=Math.ceil(payload.reduce((n,f)=>n+fs.statSync(f).size,0)/1024);
  fs.writeFileSync(control,metadata.replace(/^Installed-Size:.*$/m,`Installed-Size: ${installedSize}`));
  fs.writeFileSync(path.join(dir,'DEBIAN/md5sums'),payload.map(f=>`${createHash('md5').update(fs.readFileSync(f)).digest('hex')}  ${path.relative(dir,f)}`).join('\n')+'\n');
  run('dpkg-deb',['--root-owner-group','--build',dir,`/native/debs/${name}_${family.version}_${arch}.deb`]);
  evidence.push(provenance);
}
fs.writeFileSync('/native/evidence/packages.json',JSON.stringify(evidence,null,2)+'\n');
