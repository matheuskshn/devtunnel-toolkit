import assert from 'node:assert/strict';
import test from 'node:test';
import {mkdtempSync,mkdirSync,readFileSync,writeFileSync,rmSync} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {versionInfo,checkVersions,releaseAliases,suiteImages,immutableTag,validateAttestation,validateManifest} from './release-policy.mjs';
import {prepareRelease,promoteSuite,remoteTag} from './release.mjs';
const sha='a'.repeat(40),hash=`sha256:${'b'.repeat(64)}`;
const records=(version='0.1.0-rc.1')=>suiteImages.map(({image})=>({image,version,sha,digest:hash,platforms:['linux/amd64','linux/arm64']}));

test('strict release versions, prereleases and non-overwriting aliases',()=>{
  assert.equal(versionInfo('0.1.0-rc.1').prerelease,true);
  assert.deepEqual(releaseAliases('0.1.0-rc.1'),[]);
  assert.deepEqual(releaseAliases('0.1.0'),['0.1','latest']);
  assert.deepEqual(releaseAliases('1.2.3'),['1.2','1','latest']);
  for(const value of ['v1.0.0','01.0.0','1.0','1.0.0-01','1.0.0+build','1.0.0\n','../tag',''])assert.throws(()=>versionInfo(value));
  assert.equal(immutableTag(undefined,hash),true);assert.equal(immutableTag(hash,hash),false);
  assert.throws(()=>immutableTag(`sha256:${'c'.repeat(64)}`,hash),/IMMUTABLE/);
});
test('suite versions stay synchronized, including both package-lock root fields',t=>{
  const dir=mkdtempSync(path.join(os.tmpdir(),'suite-version-'));t.after(()=>rmSync(dir,{recursive:true,force:true}));
  mkdirSync(path.join(dir,'images/hub'),{recursive:true});
  for(const f of ['version.txt','.release-please-manifest.json','images/hub/package.json','images/hub/package-lock.json']){
    writeFileSync(path.join(dir,f),readFileSync(new URL(`../../${f}`,import.meta.url)));
  }
  assert.equal(checkVersions(dir).version,readFileSync(path.join(dir,'version.txt'),'utf8').trim());
  assert.throws(()=>checkVersions(dir,'v9.0.0'),/MISMATCH/);
  const file=path.join(dir,'images/hub/package-lock.json'),lock=JSON.parse(readFileSync(file));
  lock.packages[''].version='9.0.0';writeFileSync(file,JSON.stringify(lock));
  assert.throws(()=>checkVersions(dir),/MISMATCH/);
});
test('release manifest requires exactly five images, exact revision, version, digest and architectures',()=>{
  assert.equal(validateManifest(records(),'0.1.0-rc.1',sha).length,5);
  for(const mutation of [r=>r.pop(),r=>{r[1]=r[0];},r=>{r[0].sha='c'.repeat(40);},
    r=>{r[0].version='9.0.0';},r=>{r[0].digest='invalid';},r=>{r[0].platforms=['linux/amd64'];}]){
    const value=records();mutation(value);assert.throws(()=>validateManifest(value,'0.1.0-rc.1',sha));
  }
});
test('attestations require actual content for both architectures and normalize ARM64 variants',()=>{
  const valid={'linux/amd64':{SPDX:{spdxVersion:'SPDX-2.3'}},'linux/arm64/v8':{SPDX:{spdxVersion:'SPDX-2.3'}}};
  assert.deepEqual(Object.keys(validateAttestation(valid,'sbom')),['linux/amd64','linux/arm64']);
  for(const data of [null,{}, {'linux/amd64':{}}, {'linux/amd64':{SPDX:null},'linux/arm64':{SPDX:{}}}]){
    assert.throws(()=>validateAttestation(data,'sbom'),/ATTESTATION_MISSING/);
  }
  assert.throws(()=>validateAttestation(valid,'provenance'),/ATTESTATION_MISSING/);
});
function fakeApi({tagSha,release,older=[]}={}){
  const calls=[];
  const api=async(endpoint,options={})=>{
    calls.push({endpoint,...options});
    if(endpoint.startsWith('git/ref/tags/'))return tagSha?{object:{type:'commit',sha:tagSha}}:undefined;
    if(endpoint.startsWith('releases?'))return [...older,...(release?[release]:[])];
    if(endpoint==='git/tags')return {sha:'c'.repeat(40)};
    if(endpoint==='git/refs')return {};
    if(endpoint==='releases')return {id:1,...options.body};
    throw Error('UNEXPECTED_API_CALL');
  };
  return {api,calls};
}
test('release preparation creates an annotated tag and a draft, never publishes early',async()=>{
  const {api,calls}=fakeApi();
  const result=await prepareRelease({api,version:'0.1.0-rc.1',sha,notes:'Example changes'});
  assert.equal(result.draft,true);assert.equal(result.prerelease,true);
  assert.deepEqual(calls.filter(c=>c.method==='POST').map(c=>c.endpoint),['git/tags','git/refs','releases']);
  assert.ok(!calls.some(c=>c.method==='PATCH'));
});
test('published versions, moved tags and older stable releases are rejected before any write',async()=>{
  for(const state of [
    {tagSha:'d'.repeat(40)},
    {tagSha:sha,release:{id:1,tag_name:'v1.0.0',draft:false}},
    {release:{id:1,tag_name:'v1.0.0',draft:true,target_commitish:'d'.repeat(40)}},
    {tagSha:sha,older:[{tag_name:'v2.0.0',draft:false,prerelease:false}]},
  ]){
    const {api,calls}=fakeApi(state);
    await assert.rejects(prepareRelease({api,version:'1.0.0',sha,notes:''}));
    assert.ok(calls.every(c=>!c.method));
  }
});
test('draft retry keeps the same release and tag; annotated tags resolve to a commit',async()=>{
  const release={id:1,tag_name:'v0.1.0-rc.1',draft:true};const {api,calls}=fakeApi({tagSha:sha,release});
  assert.equal(await prepareRelease({api,version:'0.1.0-rc.1',sha,notes:''}),release);
  assert.ok(calls.every(c=>!c.method));
  assert.equal(await remoteTag(async endpoint=>endpoint.startsWith('git/ref/')?{object:{type:'tag',sha:'b'.repeat(40)}}:{object:{type:'commit',sha}},'v1.0.0'),sha);
});
function promotion(version='0.1.0-rc.1'){
  const events=[],registry=new Map();
  const options={records:records(version),version,sha,repoFor:image=>[`ghcr.io/example/${image}`,`docker.io/example/${image}`],
    inspect:async ref=>registry.get(ref),
    copy:async(source,target)=>{events.push(`copy:${target}`);registry.set(target,source.split('@')[1]);},
    attach:async()=>events.push('assets'),publish:async()=>events.push('published'),verifyTag:async()=>events.push('tag-checked')};
  return {options,events,registry};
}
test('all immutable references and assets are verified before aliases and final publication',async()=>{
  const {options,events}=promotion('1.2.3');await promoteSuite(options);
  assert.equal(events.at(-1),'published');assert.equal(events.filter(e=>e==='tag-checked').length,2);
  assert.ok(events.indexOf('assets')>events.findLastIndex(e=>e.endsWith(':1.2.3')||e.endsWith(':v1.2.3')));
  assert.ok(events.indexOf('assets')<events.findIndex(e=>e.endsWith(':latest')));
});
test('prereleases do not move stable aliases and retries reuse exact existing digests',async()=>{
  const {options,events,registry}=promotion();await promoteSuite(options);
  assert.ok(events.every(e=>!e.endsWith(':latest')));events.length=0;
  await promoteSuite(options);assert.ok(events.every(e=>!e.startsWith('copy:')));assert.equal(registry.size,20);
});
test('a conflicting version anywhere in the suite blocks every write',async()=>{
  const {options,events,registry}=promotion();
  registry.set('docker.io/example/devtunnel-toolkit-hub:0.1.0-rc.1',`sha256:${'c'.repeat(64)}`);
  await assert.rejects(promoteSuite(options),/IMMUTABLE/);assert.deepEqual(events,['tag-checked']);
});
test('partial registry failure, asset failure, wrong digest or moved tag never publish the release',async()=>{
  for(const failure of ['copy','assets','digest','tag']){
    const {options,events}=promotion('1.2.3');
    if(failure==='copy')options.copy=async()=>{throw Error('registry down');};
    if(failure==='assets')options.attach=async()=>{throw Error('upload failed');};
    if(failure==='digest')options.copy=async()=>{};
    if(failure==='tag')options.verifyTag=async()=>{throw Error('tag moved');};
    await assert.rejects(promoteSuite(options));assert.ok(!events.includes('published'));
    assert.ok(!events.some(e=>e.endsWith(':latest')));
  }
});
