import assert from 'node:assert/strict';
import test from 'node:test';
import {mkdtemp,rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {Manager} from '../dist/manager.js';
import {parseConfig,HubError,bindIdentity} from '../dist/model.js';

async function manager(t) {
  const dir=await mkdtemp(path.join(os.tmpdir(),'hub-manager-test-'));
  const m=new Manager(parseConfig({}),dir,`${dir}/run`);await m.store.load();
  // Test substitutes are injected in-process only. There is no fake-auth switch
  // in the image, runtime configuration, environment or administrative protocol.
  m.configureSquid=async()=>{};m.log=()=>{};
  t.after(async()=>{await m.close();await rm(dir,{recursive:true,force:true});});return m;
}
test('sessions enroll, stop, logout and remove without reassigning audit identity',async t=>{
  const m=await manager(t);const calls=[];
  await m.dispatch(['session','add','user-a','--provider','github'],()=>{});
  const s=m.store.get('user-a');
  m.runtime=async()=>({login:async output=>{
    output('synthetic-device-instruction');bindIdentity(s,{provider:'github',user_id:'github:1001',user_login:'user-a'});
  },cli:async args=>calls.push(args),close:async()=>{}});
  let instruction='';await m.dispatch(['session','login','user-a'],value=>instruction+=value);
  assert.equal(s.status,'ready');assert.equal(instruction,'synthetic-device-instruction');
  await m.dispatch(['session','stop','user-a'],()=>{});assert.equal(s.desired,false);
  await m.dispatch(['session','logout','user-a'],()=>{});
  assert.equal(s.status,'login_required');assert.equal(s.identity.user_id,'github:1001');
  await m.dispatch(['session','remove','user-a'],()=>{});assert.equal(s.status,'removed');
  assert.deepEqual(calls,[['user','logout'],['user','logout']]);
  await assert.rejects(m.dispatch(['session','add','user-a'],()=>{}),/RESERVED/);
});
test('one pending login does not block another session; duplicate operations are rejected',async t=>{
  const m=await manager(t);await m.dispatch(['session','add','user-a'],()=>{});
  let release;const barrier=new Promise(resolve=>release=resolve);
  m.runtime=async()=>({login:()=>barrier});
  const pending=m.dispatch(['session','login','user-a'],()=>{});
  await m.dispatch(['session','add','user-b','--provider','github'],()=>{});
  assert.equal((await m.dispatch(['session','list'],()=>{})).length,2);
  await assert.rejects(m.dispatch(['session','stop','user-a'],()=>{}),/SESSION_BUSY/);
  release();await pending;
});
test('expired auth fails only its session and never retries indefinitely',async t=>{
  const m=await manager(t);const a=m.store.add('user-a','github');const b=m.store.add('user-b','github');
  a.identity={provider:'github',user_id:'github:1001',user_login:'user-a'};
  b.status='running';b.desired=true;a.desired=true;
  m.runtime=async()=>({identity:async()=>{throw new HubError('AUTH_REQUIRED');}});
  await assert.rejects(m.dispatch(['session','start','user-a'],()=>{}),/AUTH_REQUIRED/);
  assert.equal(a.status,'reauth_required');assert.equal(b.status,'running');assert.equal(m.retries.size,0);
  assert.equal(m.stopping,false);
});
test('cached identity mismatch never provisions a tunnel',async t=>{
  const m=await manager(t);const s=m.store.add('user-a','github');
  s.identity={provider:'github',user_id:'github:1001',user_login:'user-a'};
  let provisioned=false;m.provision=async()=>{provisioned=true;};
  m.runtime=async()=>({identity:async()=>({...s.identity,user_id:'github:1002'})});
  await assert.rejects(m.dispatch(['session','start','user-a'],()=>{}),/IDENTITY_CHANGED/);
  assert.equal(provisioned,false);assert.equal(s.identity.user_id,'github:1001');
});
test('malformed commands cannot inject CLI flags or external paths',async t=>{
  const m=await manager(t);
  for(const args of [['session','add','user-a','--access-token','fake'],['session','add','../user'],
    ['session','add','user-a','--provider','github','--provider','microsoft'],['raw','delete-all']]) {
    await assert.rejects(m.dispatch(args,()=>{}));
  }
  assert.equal(m.store.state.sessions.length,0);
});
test('only a confirmed missing resource permits recreation with the exact name and cluster',async t=>{
  const m=await manager(t);const s=m.store.add('user-a','github');s.tunnel_id='devhub-user-a.use1';
  const details={tunnelId:'devhub-user-a',clusterId:'use1',ports:[{portNumber:3140}],accessControl:{entries:[]}};
  let attempts=0;const commands=[];
  await m.provision(s,{details:async()=>{if(attempts++===0)throw new HubError('TUNNEL_NOT_FOUND');return details;},
    cli:async args=>{commands.push(args);return JSON.stringify({tunnel:{tunnelId:s.tunnel_id}});}});
  assert.deepEqual(commands,[['create','devhub-user-a','--expiration','2d','--json']]);
  for(const code of ['AUTH_REQUIRED','TUNNEL_ACCESS_DENIED','COMMAND_FAILED']) {
    let created=false;
    await assert.rejects(m.provision(s,{details:async()=>{throw new HubError(code);},cli:async()=>{created=true;}}),new RegExp(code));
    assert.equal(created,false);
  }
  await assert.rejects(m.provision(s,{details:async()=>{throw new HubError('TUNNEL_NOT_FOUND');},
    cli:async()=>JSON.stringify({tunnel:{tunnelId:'devhub-user-a.euw1'}})}),/CLUSTER_CHANGED/);
  assert.equal(s.tunnel_id,'devhub-user-a.use1');
});
