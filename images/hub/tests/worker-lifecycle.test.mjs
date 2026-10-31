import test from 'node:test';
import assert from 'node:assert/strict';
import {fork} from 'node:child_process';
import {mkdtemp, writeFile, rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {once} from 'node:events';

async function fixture(t, mode) {
  const directory=await mkdtemp(path.join(os.tmpdir(),'hub-worker-lifecycle-'));
  const worker=fileURLToPath(new URL('../dist/worker.js',import.meta.url));
  const fake=path.join(directory,'host.mjs');
  await writeFile(fake,`export class MappedTunnelHost {
    isDisposed=false;relayHostId='candidate-fixture';disconnectReason=${mode==='replaced'?12:10};
    refreshingTunnelAccessToken(callback) {} refreshingTunnel(callback) {this.refresh=callback;}
    connectionStatusChanged(callback) {this.status=callback;}
    async connect() {this.status({status:'connected'});${['replaced','disconnected'].includes(mode)?"setTimeout(()=>this.status({status:'disconnected'}),40);":''}}
    async refreshPorts() {const e={};this.refresh(e);await e.tunnelPromise;}
    async dispose() {this.isDisposed=true;}
  }`);
  const bootstrap=path.join(directory,'bootstrap.mjs');
  const health=path.join(directory,'health.mjs');
  await writeFile(health,"export async function verifyHostedProxy(){throw new Error('synthetic stalled proxy');}");
  await writeFile(bootstrap,`import {registerHooks} from 'node:module';
    registerHooks({resolve(specifier,context,next){
      if(context.parentURL===${JSON.stringify(pathToFileURL(worker).href)}) {
        if(specifier==='./mapped-host.js')return {url:${JSON.stringify(pathToFileURL(fake).href)},shortCircuit:true};
        if(specifier==='./relay-health.js')return {url:${JSON.stringify(pathToFileURL(health).href)},shortCircuit:true};
      }
      return next(specifier,context);}});
    await import(${JSON.stringify(pathToFileURL(worker).href)});`);
  const child=fork(bootstrap,[],{stdio:['ignore','ignore','ignore','ipc']});
  const messages=[];let credentialRequests=0;
  child.on('message',message=>{
    messages.push(message);
    if(message.type==='credentials') {
      credentialRequests++;
      child.send({type:'credentials',id:message.id,...(mode==='metadata-failure'&&credentialRequests>1
        ? {error:'COMMAND_FAILED'}:{tunnel:{accessTokens:{host:'synthetic-host-credential'}}})});
    }
  });
  t.after(async()=>{
    if(child.exitCode===null&&child.signalCode===null){const closed=once(child,'close');child.kill('SIGTERM');await closed;}
    await rm(directory,{recursive:true,force:true});
  });
  child.send({type:'start',listener:18001,proxyPort:3140,maintenanceSeconds:0.02,verifyDataPath:mode==='data-path-failure'});
  return {child,messages};
}
for(const [mode,expected] of [['disconnected',1],['replaced',78]]) {
  test(`permanently ${mode} relay exits its worker instead of reporting a live stale session`,async t=>{
    const {child,messages}=await fixture(t,mode);
    const [code]=await once(child,'exit');
    assert.equal(code,expected);assert.ok(messages.some(m=>m.type==='ready'));
    assert.ok(messages.some(m=>m.type==='status'&&m.status==='disconnected'));
  });
}
test('transient metadata/credential failure preserves the already connected worker',async t=>{
  const {child,messages}=await fixture(t,'metadata-failure');
  const deadline=Date.now()+5000;
  while(messages.filter(m=>m.type==='maintenance').length<2&&Date.now()<deadline)
    await new Promise(resolve=>setTimeout(resolve,20));
  assert.ok(messages.some(m=>m.type==='ready'));
  assert.ok(messages.filter(m=>m.type==='maintenance').length>=2);
  assert.equal(child.exitCode,null);assert.equal(child.signalCode,null);
});

test('candidate cannot report ready when its proxy data-path validation fails',async t=>{
  const {child,messages}=await fixture(t,'data-path-failure');
  const [code]=await once(child,'exit');assert.equal(code,1);
  assert.ok(messages.some(m=>m.type==='credentials'&&m.scope==='connect'));
  assert.ok(messages.some(m=>m.type==='failure'&&m.code==='RECONNECT_DATA_PATH_FAILED'));
  assert.ok(!messages.some(m=>m.type==='ready'));
});
