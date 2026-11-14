import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdtempSync,mkdirSync,readFileSync,writeFileSync,rmSync,existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {serviceCliLibraries} from '../bin/service-cli-libraries.mjs';
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const manifest=JSON.parse(readFileSync(new URL('../bin/cli-library-patches.json',import.meta.url)));
function fixture(){
 const work=mkdtempSync(path.join(tmpdir(),'cli-servicing-test-'));const input=path.join(work,'input');mkdirSync(input);
 const core=Buffer.from('fixture-original-cli-core'),old=Buffer.from('fixture-original-webutilities');writeFileSync(path.join(input,'devtunnel.dll'),core);writeFileSync(path.join(input,'Microsoft.AspNetCore.WebUtilities.dll'),old);
 const deps={libraries:{'devtunnel/1.0.2115':{type:'project'},'Microsoft.AspNetCore.WebUtilities/10.0.7':{type:'package'}},targets:{empty:{},rid:{'devtunnel/1.0.2115':{dependencies:{'Microsoft.AspNetCore.WebUtilities':'10.0.7'}},'Microsoft.AspNetCore.WebUtilities/10.0.7':{runtime:{'Microsoft.AspNetCore.WebUtilities.dll':{}}}}}};
 writeFileSync(path.join(input,'devtunnel.deps.json'),JSON.stringify(deps));
 const evidence={cliVersion:'1.0.2115',applicationFiles:[{name:'devtunnel.dll',sha256:hash(core)},{name:'Microsoft.AspNetCore.WebUtilities.dll',sha256:hash(old)}]};
 const assets=new Map(manifest.packages.map((p,i)=>{const b=Buffer.alloc(4096,i);b.write('MZ');return[p.name,b];}));
 return {work,input,output:path.join(work,'output'),evidence,assets};
}
test('services the official dependency closure while retaining original core and source provenance',()=>{
 const f=fixture();try{
  const result=serviceCliLibraries(f.input,f.output,f.evidence,manifest,f.assets);
  assert.equal(hash(readFileSync(path.join(f.output,'devtunnel.dll'))),f.evidence.applicationFiles[0].sha256);
  assert.equal(result.applicationFiles[1].sha256,f.evidence.applicationFiles[1].sha256);
  assert.equal(result.servicedLibraries[0].originalSha256,f.evidence.applicationFiles[1].sha256);
  assert.equal(result.servicedLibraries.length,3);
  const deps=JSON.parse(readFileSync(path.join(f.output,'devtunnel.deps.json')));
  assert(!deps.libraries['Microsoft.AspNetCore.WebUtilities/10.0.7']);
  for(const p of manifest.packages){assert(deps.libraries[p.name+'/10.0.15']);assert.equal(hash(readFileSync(path.join(f.output,p.name+'.dll'))),hash(f.assets.get(p.name)));}
  assert.equal(deps.targets.rid['devtunnel/1.0.2115'].dependencies['Microsoft.AspNetCore.WebUtilities'],'10.0.15');
  assert.deepEqual(deps.targets.empty,{});
 }finally{rmSync(f.work,{recursive:true,force:true});}
});
test('rejects changed core, unexpected source versions and untrusted manifests before writing output',()=>{
 for(const change of [
  f=>writeFileSync(path.join(f.input,'devtunnel.dll'),'modified-core'),
  f=>{const d=JSON.parse(readFileSync(path.join(f.input,'devtunnel.deps.json')));delete d.libraries['Microsoft.AspNetCore.WebUtilities/10.0.7'];writeFileSync(path.join(f.input,'devtunnel.deps.json'),JSON.stringify(d));},
  f=>f.assets.set(manifest.packages[0].name,Buffer.from('not-a-managed-assembly'))
 ]){
  const f=fixture();try{change(f);assert.throws(()=>serviceCliLibraries(f.input,f.output,f.evidence,manifest,f.assets),/CLI_SERVICING_/);assert(!existsSync(f.output));}finally{rmSync(f.work,{recursive:true,force:true});}
 }
 const f=fixture();try{const bad=structuredClone(manifest);bad.packages[0].source='https://example.test/untrusted';assert.throws(()=>serviceCliLibraries(f.input,f.output,f.evidence,bad,f.assets),/CLI_SERVICING_PIN/);assert(!existsSync(f.output));}finally{rmSync(f.work,{recursive:true,force:true});}
});
