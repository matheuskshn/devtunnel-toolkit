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
test('CLI session creation can generate an opaque internal ID',async t=>{
  const m=await manager(t);
  const session=await m.dispatch(['session','add','--provider','github'],()=>{});
  assert.match(session.id,/^s-[a-f0-9]{30}$/);assert.equal(session.provider,'github');
});
test('session validity is configurable at creation and updates the remote inactivity window',async t=>{
  const m=await manager(t);
  const s=await m.dispatch(['session','add','user-a','--provider','github','--expiration-hours','72','--expected-auth-hours','48','--auth-warning-hours','4'],()=>{});
  assert.equal(s.tunnel_expiration_hours,72);assert.equal(s.auth_expected_hours,48);assert.equal(s.auth_warning_hours,4);
  s.tunnel_name='devhub-user-a';s.tunnel_id='devhub-user-a.use1';
  const commands=[];
  m.runtime=async()=>({
    identity:async()=>({provider:'github',user_id:'github:1001',user_login:'user-a'}),
    cli:async args=>{commands.push(args);return '{}';},
    details:async()=>({tunnelId:'devhub-user-a',clusterId:'use1',ports:[],accessControl:{entries:[]}}),
  });
  await m.dispatch(['session','configure','user-a','--expiration-hours','96','--expected-auth-hours','72','--auth-warning-hours','6'],()=>{});
  assert.deepEqual(commands,[['update','devhub-user-a.use1','--expiration','96h','--json']]);
  assert.equal(s.tunnel_expiration_hours,96);assert.equal(s.auth_expected_hours,72);assert.equal(s.auth_warning_hours,6);
  await assert.rejects(m.dispatch(['session','configure','user-a','--expiration-hours','721'],()=>{}),/INVALID_TUNNEL_EXPIRATION/);
});
test('one pending login does not block another session; duplicate operations are rejected',async t=>{
  const m=await manager(t);await m.dispatch(['session','add','user-a'],()=>{});
  let release;const barrier=new Promise(resolve=>release=resolve);
  m.runtime=async()=>({login:async()=>{await barrier;bindIdentity(m.store.get('user-a'),{provider:'microsoft',user_id:'microsoft:object-a',user_login:'user-a@example.com'});}});
  const pending=m.dispatch(['session','login','user-a'],()=>{});
  await m.dispatch(['session','add','user-b','--provider','github'],()=>{});
  assert.equal((await m.dispatch(['session','list'],()=>{})).length,2);
  await assert.rejects(m.dispatch(['session','stop','user-a'],()=>{}),/SESSION_BUSY/);
  release();await pending;
});
test('connect authenticates then starts, while reconnect clears the cache before doing both',async t=>{
  const m=await manager(t);const events=[];
  await m.dispatch(['session','add','user-a','--provider','github'],()=>{});
  const s=m.store.get('user-a');
  const active={login:async()=>{events.push('login');bindIdentity(s,{provider:'github',user_id:'github:1001',user_login:'user-a'});}};
  m.runtime=async()=>active;
  m.start=async session=>{events.push('start');session.status='running';session.desired=true;};
  await m.dispatch(['session','connect','user-a'],()=>{});
  assert.deepEqual(events,['login','start']);assert.equal(s.status,'running');

  const previous={cli:async args=>events.push(args.join(' ')),close:async()=>events.push('close')};
  const renewed={login:async()=>{events.push('reauthenticate');bindIdentity(s,{provider:'github',user_id:'github:1001',user_login:'user-a'});}};
  let calls=0;m.runtime=async()=>calls++===0?previous:renewed;
  await m.dispatch(['session','reconnect','user-a'],()=>{});
  assert.deepEqual(events.slice(2),['user logout','close','reauthenticate','start']);
  assert.equal(s.status,'running');
});
test('reconnect continues when an expired cache is already logged out',async t=>{
  const m=await manager(t);const s=m.store.add('user-a','github');
  s.identity={provider:'github',user_id:'github:1001',user_login:'user-a'};
  const previous={cli:async()=>{throw new HubError('AUTH_REQUIRED');},close:async()=>{}};
  const renewed={login:async()=>bindIdentity(s,{...s.identity})};
  let calls=0;m.runtime=async()=>calls++===0?previous:renewed;
  m.start=async session=>{session.status='running';};
  await m.dispatch(['session','reconnect','user-a'],()=>{});
  assert.equal(s.status,'running');
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
  const m=await manager(t);const s=m.store.add('user-a','github','devhub-user-a');s.tunnel_id='devhub-user-a.use1';
  const details={tunnelId:'devhub-user-a',clusterId:'use1',ports:[{portNumber:3140}],accessControl:{entries:[]}};
  let attempts=0;const commands=[];
  await m.provision(s,{details:async()=>{if(attempts++===0)throw new HubError('TUNNEL_NOT_FOUND');return details;},
    cli:async args=>{commands.push(args);return JSON.stringify({tunnel:{tunnelId:s.tunnel_id}});}});
  assert.deepEqual(commands,[['create','devhub-user-a','--expiration','48h','--json']]);
  for(const code of ['AUTH_REQUIRED','TUNNEL_ACCESS_DENIED','COMMAND_FAILED']) {
    let created=false;
    await assert.rejects(m.provision(s,{details:async()=>{throw new HubError(code);},cli:async()=>{created=true;}}),new RegExp(code));
    assert.equal(created,false);
  }
  await assert.rejects(m.provision(s,{details:async()=>{throw new HubError('TUNNEL_NOT_FOUND');},
    cli:async()=>JSON.stringify({tunnel:{tunnelId:'devhub-user-a.euw1'}})}),/CLUSTER_CHANGED/);
  assert.equal(s.tunnel_id,'devhub-user-a.use1');
});

test('web policy requires stopped idle sessions, gates CLI races and rolls back failed persistence',async t=>{
  const m=await manager(t),s=m.store.add('user-a','github');s.desired=true;
  await assert.rejects(m.applyPolicy({maxSessions:10},async()=>{}),/STOP_SESSIONS_FIRST/);
  s.desired=false;m.busy.add(s.id);
  await assert.rejects(m.applyPolicy({maxSessions:10},async()=>{}),/STOP_SESSIONS_FIRST/);m.busy.clear();
  let release;const gate=new Promise(r=>release=r);const pending=m.applyPolicy({maxSessions:10},async()=>gate);
  await assert.rejects(m.dispatch(['session','add','user-b'],()=>{}),/SESSION_BUSY/);
  release();await pending;assert.equal(m.config.maxSessions,10);
  await assert.rejects(m.applyPolicy({maxSessions:20},async()=>{throw new HubError('CONFIG_CONFLICT');}),/CONFIG_CONFLICT/);
  assert.equal(m.config.maxSessions,10);assert.equal(m.changingPolicy,false);
  await m.dispatch(['session','add','user-b'],()=>{});assert.equal(m.store.state.sessions.length,2);
});
