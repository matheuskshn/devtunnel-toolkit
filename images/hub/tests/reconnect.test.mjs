import test from 'node:test';
import assert from 'node:assert/strict';
import {EventEmitter} from 'node:events';
import {mkdtemp, mkdir, readFile, writeFile, rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {Manager} from '../dist/manager.js';
import {parseConfig, HubError, bindIdentity} from '../dist/model.js';
import {StagedCredentials} from '../dist/staged-credentials.js';

class Worker extends EventEmitter {
  exitCode=null;signalCode=null;connected=true;killed=false;
  relayStatus='connected';
  constructor(events, name, outcome='ready') { super();this.events=events;this.name=name;this.outcome=outcome; }
  send(message) {
    if(message.type==='health'){queueMicrotask(()=>this.emit('message',{type:'health',id:message.id,status:this.relayStatus}));return;}
    if(message.type!=='start')return;
    this.events.push('candidate-connect');
    queueMicrotask(()=>{
      if(this.outcome!=='local-failure') {this.emit('message',{type:'relay_connecting'});this.onConnect?.();}
      if(this.outcome==='ready'||this.outcome==='flap') {
        this.emit('message',{type:'ready'});
        if(this.outcome==='flap')this.emit('message',{type:'status',status:'connecting'});
      }
      else {this.exitCode=1;this.emit('exit',1);this.emit('close',1);}
    });
  }
  kill(signal) { this.killed=true;this.signalCode=signal;this.exitCode=0;this.connected=false;
    this.events.push(this.name+'-retired');this.emit('exit',0);this.emit('close',0);return true; }
}
async function fixture(t, {outcome='ready',authFailure,credentialFailure,loginGate,socks=false}={}) {
  const root=await mkdtemp(path.join(os.tmpdir(),'hub-reconnect-'));
  const m=new Manager(parseConfig({socksEnabled:socks}),root,path.join(root,'run'));
  await m.store.load();
  const s=m.store.add('user-a','github','fixture-tunnel');
  s.identity={provider:'github',user_id:'github:1001',user_login:'user-a'};
  s.tunnel_id='fixture-tunnel.use1';s.status='running';s.desired=true;
  const events=[];
  const home=path.join(root,'sessions',s.id,'home');await mkdir(home,{recursive:true});await writeFile(path.join(home,'fixture'),'old-credential');
  const previous={env:{HOME:home},abort:new AbortController(),close:async()=>events.push('old-runtime-closed'),cli:async()=>{throw Error('Must never log out original credentials');}};
  const oldWorker=new Worker(events,'old');m.workers.set(s.id,oldWorker);m.allWorkers.add(oldWorker);m.runtimes.set(s.id,previous);
  let candidate;
  m.replacementRuntime=session=>{
    candidate=session;
    return {session,env:{HOME:path.join(root,'sessions',s.id,session.credential_generation)},abort:new AbortController(),
      open:async()=>{},
      login:async()=>{events.push('candidate-auth');if(loginGate)await loginGate;if(authFailure)throw new HubError(authFailure);bindIdentity(session,{...s.identity});},
      credentials:async()=>{events.push('candidate-validate');if(credentialFailure)throw new HubError(credentialFailure);return {};},
      quiescent:async action=>action(),close:async()=>events.push('candidate-runtime-closed')};
  };
  let newWorker;
  m.createWorker=()=>{newWorker=new Worker(events,'candidate',outcome);newWorker.onConnect=()=>{if(outcome!=='failed-with-old-connected')oldWorker.relayStatus='disconnected';};m.allWorkers.add(newWorker);return newWorker;};
  m.configureSquid=async()=>{};m.log=(event)=>events.push(event);
  m.provision=async()=>{throw Error('Reconnect must never provision or mutate remote ACLs/ports');};
  m.start=async session=>{events.push('restore-old');session.status='running';session.desired=true;};
  t.after(async()=>{await m.close();await rm(root,{recursive:true,force:true});});
  return {m,s,events,home,oldWorker,previous,candidate:()=>candidate,newWorker:()=>newWorker};
}

test('new authentication and relay are ready before retiring original credentials and worker',async t=>{
  const f=await fixture(t);
  await f.m.dispatch(['session','reconnect',f.s.id],()=>{});
  assert.ok(f.events.indexOf('candidate-auth')<f.events.indexOf('candidate-validate'));
  assert.ok(f.events.indexOf('candidate-validate')<f.events.indexOf('candidate-connect'));
  assert.ok(f.events.indexOf('candidate-connect')<f.events.indexOf('old-runtime-closed'));
  assert.ok(f.events.indexOf('old-runtime-closed')<f.events.indexOf('old-retired'));
  assert.equal(f.m.workers.get(f.s.id),f.newWorker());assert.equal(f.s.status,'running');
  assert.match(f.s.credential_generation,/^[a-f0-9]{32}$/);
  await assert.rejects(readFile(path.join(f.home,'fixture')),/ENOENT/);
});

test('pending interactive authentication leaves original worker, status and credentials available',async t=>{
  let release;const gate=new Promise(resolve=>release=resolve);const f=await fixture(t,{loginGate:gate});
  const pending=f.m.dispatch(['session','reconnect',f.s.id],()=>{});
  while(!f.events.includes('candidate-auth'))await new Promise(resolve=>setImmediate(resolve));
  assert.equal(f.m.workers.get(f.s.id),f.oldWorker);assert.equal(f.oldWorker.killed,false);
  assert.equal(f.s.status,'running');assert.equal(await readFile(path.join(f.home,'fixture'),'utf8'),'old-credential');
  await assert.rejects(f.m.dispatch(['session','reconnect',f.s.id],()=>{}),/SESSION_BUSY/);
  release();await pending;
});

for(const code of ['AUTH_REQUIRED','IDENTITY_CHANGED','LOGIN_TOKEN_EXPIRED']) {
  test(`candidate ${code} leaves old session and credential generation unchanged`,async t=>{
    const f=await fixture(t,{authFailure:code});
    await assert.rejects(f.m.dispatch(['session','reconnect',f.s.id],()=>{}),new RegExp(code));
    assert.equal(f.oldWorker.killed,false);assert.equal(f.s.status,'running');assert.equal(f.s.desired,true);
    assert.equal(f.s.credential_generation,undefined);assert.equal(f.m.runtimes.get(f.s.id),f.previous);
    assert.equal(await readFile(path.join(f.home,'fixture'),'utf8'),'old-credential');
    assert.ok(!f.events.includes('candidate-connect'));
  });
}

test('failed remote preflight preserves original relay and never provisions a missing tunnel',async t=>{
  const f=await fixture(t,{credentialFailure:'TUNNEL_NOT_FOUND'});
  await assert.rejects(f.m.dispatch(['session','reconnect',f.s.id],()=>{}),/TUNNEL_NOT_FOUND/);
  assert.equal(f.oldWorker.killed,false);assert.equal(f.s.status,'running');
});

test('failed relay handoff restores the previous session using its unchanged credentials',async t=>{
  const f=await fixture(t,{outcome:'fail'});
  await assert.rejects(f.m.dispatch(['session','reconnect',f.s.id],()=>{}),/WORKER_FAILED/);
  assert.ok(f.events.includes('restore-old'));assert.equal(f.s.credential_generation,undefined);
  assert.equal(await readFile(path.join(f.home,'fixture'),'utf8'),'old-credential');
  assert.equal(f.s.status,'running');assert.equal(f.m.replacements.size,0);
});

test('a candidate that loses readiness before promotion cannot retire the previous credentials',async t=>{
  const f=await fixture(t,{outcome:'flap'});
  await assert.rejects(f.m.dispatch(['session','reconnect',f.s.id],()=>{}),/WORKER_FAILED/);
  assert.equal(f.s.credential_generation,undefined);
  assert.equal(await readFile(path.join(f.home,'fixture'),'utf8'),'old-credential');
  assert.ok(f.events.includes('restore-old'));
});

for(const outcome of ['local-failure','failed-with-old-connected']) {
  test(`${outcome} never stops an original relay that still confirms connected`,async t=>{
    const f=await fixture(t,{outcome});
    await assert.rejects(f.m.dispatch(['session','reconnect',f.s.id],()=>{}),/WORKER_FAILED/);
    assert.equal(f.oldWorker.killed,false);assert.equal(f.m.workers.get(f.s.id),f.oldWorker);
    assert.equal(await readFile(path.join(f.home,'fixture'),'utf8'),'old-credential');
    assert.ok(!f.events.includes('restore-old'));
  });
}

test('SOCKS candidate gets an independent listener and retains the HTTP audit listener',async t=>{
  const f=await fixture(t,{socks:true});const oldSocks=f.s.socks_listener;const http=f.s.listener;
  await f.m.dispatch(['session','reconnect',f.s.id],()=>{});
  assert.notEqual(f.s.socks_listener,oldSocks);assert.equal(f.s.listener,http);
  assert.equal(f.m.replacementListeners.size,0);
  assert.ok(f.s.socks_listener<f.m.store.state.next_listener);
});

test('retired runtime failures cannot stop the replacement, while its own keyring failure still fails closed',async t=>{
  const f=await fixture(t);
  await f.m.dispatch(['session','reconnect',f.s.id],()=>{});
  f.m.runtimeFailed(f.s.id,f.previous);
  assert.equal(f.newWorker().killed,false);assert.equal(f.s.status,'running');
  f.m.runtimeFailed(f.s.id,f.m.runtimes.get(f.s.id));
  while(!f.newWorker().killed)await new Promise(resolve=>setImmediate(resolve));
  assert.equal(f.s.status,'error');assert.equal(f.s.error,'SESSION_SERVICE_FAILED');
});

test('candidate credential operations do not dirty or overwrite the active PostgreSQL package',async t=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'staged-credentials-'));t.after(()=>rm(root,{recursive:true,force:true}));
  const calls=[];const target={beginAuth:async id=>calls.push(['begin',id]),checkpoint:async id=>calls.push(['checkpoint',id]),replaceCredentials:async id=>calls.push(['replace',id])};
  const staged=new StagedCredentials(target);
  await staged.restore('user-a',root);await staged.beginAuth('user-a');await staged.checkpoint('user-a',root);
  assert.deepEqual(calls,[]);
  await staged.promote('user-a',root);assert.deepEqual(calls,[['replace','user-a']]);
  await staged.beginAuth('user-a');await staged.checkpoint('user-a',root);
  assert.deepEqual(calls,[['replace','user-a'],['begin','user-a'],['checkpoint','user-a']]);
});

test('failed atomic promotion never forwards candidate checkpoints to the active store',async t=>{
  const calls=[];const staged=new StagedCredentials({replaceCredentials:async()=>{throw new HubError('POSTGRES_UNAVAILABLE');},beginAuth:async()=>calls.push('dirty'),checkpoint:async()=>calls.push('overwrite')});
  await assert.rejects(staged.promote('user-a','/unused'),/POSTGRES_UNAVAILABLE/);
  await staged.beginAuth('user-a');await staged.checkpoint('user-a','/unused');assert.deepEqual(calls,[]);
});
