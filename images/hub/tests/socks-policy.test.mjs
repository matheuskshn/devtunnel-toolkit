import assert from 'node:assert/strict';
import test from 'node:test';
import {mkdtemp, rm, writeFile} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {parseConfig} from '../dist/model.js';
import {loadConfig} from '../dist/config.js';
import {StateStore} from '../dist/state.js';
import {Manager} from '../dist/manager.js';
import {privateTunnel} from '../dist/auth.js';

async function fixture(t, input={}) {
  const dir=await mkdtemp(path.join(os.tmpdir(),'hub-socks-policy-'));
  const manager=new Manager(parseConfig(input),dir,`${dir}/run`);
  await manager.store.load();manager.configureSquid=async()=>{};manager.log=()=>{};
  t.after(async()=>{await manager.close();await rm(dir,{recursive:true,force:true});});
  const session=manager.store.add('user-a','github','devhub-user-a');
  session.tunnel_id='devhub-user-a.use1';
  return {manager,session,dir};
}
function remote(s, ports=[3140]) {
  const value={tunnelId:'devhub-user-a',clusterId:'use1',accessControl:{entries:[]},ports:ports.map(portNumber=>({portNumber}))};
  const commands=[];
  return {value,commands,runtime:{details:async()=>structuredClone(value),cli:async args=>{
    commands.push(args);const portNumber=Number(args[args.indexOf('--port-number')+1]);
    if(args[1]==='delete')value.ports=value.ports.filter(p=>p.portNumber!==portNumber);
    if(args[1]==='create')value.ports.push({portNumber});return '{}';
  }}};
}
test('SOCKS defaults disabled/3180, env override and validation reserve all service ports',async t=>{
  const {dir}=await fixture(t);const file=path.join(dir,'config.json');await writeFile(file,'{}');
  assert.equal(parseConfig({}).socksPort,3180);assert.equal(parseConfig({}).socksEnabled,false);
  const config=await loadConfig(file,{HUB_SOCKS_ENABLED:'true',HUB_SOCKS_PORT:'3280'});
  assert.equal(config.socksEnabled,true);assert.equal(config.socksPort,3280);
  for(const socksPort of [0,1023,65536,'3180',3180.5,3140])assert.throws(()=>parseConfig({socksPort}),/INVALID_SOCKS_PORT/);
  assert.throws(()=>parseConfig({socksPort:18001}),/RESERVED_PORT/);
  assert.throws(()=>parseConfig({socksPort:8080}),/INVALID_HEALTH_PORT/);
  for(const value of ['yes','1','TRUE',''])await assert.rejects(loadConfig(file,{HUB_SOCKS_ENABLED:value}),/INVALID_/);
  for(const value of ['3e3','22','-1','3180;'])await assert.rejects(loadConfig(file,{HUB_SOCKS_PORT:value}),/INVALID_/);
});
test('SOCKS listeners are unique immutable reservations across legacy migration, restart and removal',async t=>{
  const {manager:m,session:s,dir}=await fixture(t);
  const second=m.store.add('user-b','github');assert.equal(second.listener,18002);
  m.store.ensureSocksListener(s);m.store.ensureSocksListener(second);m.store.ensureSocksListener(s);
  assert.equal(s.socks_listener,18003);assert.equal(second.socks_listener,18004);
  s.status='removed';await m.store.save();
  const next=new StateStore(dir,m.config);await next.load();
  assert.equal(next.state.sessions[0].socks_listener,18003);
  m.config.socksEnabled=true;
  const third=next.add('user-c','github');assert.equal(third.listener,18005);assert.equal(third.socks_listener,18006);
});
test('insufficient listener capacity does not half-allocate and policy enable refuses exhaustion',async t=>{
  const {manager:m,session:s}=await fixture(t,{listenerEnd:18001});
  assert.throws(()=>m.store.ensureSocksListener(s),/POOL_EXHAUSTED/);
  assert.equal(s.socks_listener,undefined);
  await assert.rejects(m.applyPolicy({socksEnabled:true},async()=>{}),/POOL_EXHAUSTED/);
  assert.equal(m.config.socksEnabled,false);
});
test('new sessions cannot consume listener capacity reserved for legacy SOCKS enrollment',async t=>{
  const {manager:m,session:s}=await fixture(t,{listenerEnd:18003});
  await m.applyPolicy({socksEnabled:true},async()=>{});
  assert.throws(()=>m.store.add('user-b','github'),/POOL_EXHAUSTED/);
  assert.equal(m.store.state.next_listener,18002);
  m.store.ensureSocksListener(s);assert.equal(s.socks_listener,18002);
});
test('enabling, migrating and disabling SOCKS preserves HTTP, identities and reserved listeners',async t=>{
  const {manager:m,session:s,dir}=await fixture(t,{socksEnabled:true});const r=remote(s);
  await m.provision(s,r.runtime);assert.deepEqual(r.value.ports.map(p=>p.portNumber),[3140,3180]);
  assert.equal(s.socks_port,3180);assert.equal(s.socks_listener,18002);
  assert.equal(privateTunnel(r.value,s).ports.length,2);
  assert.equal((await new StateStore(dir,m.config).load()),undefined);
  m.config.socksPort=3280;await m.provision(s,r.runtime);
  assert.deepEqual(r.value.ports.map(p=>p.portNumber),[3140,3280]);
  m.config.socksEnabled=false;await m.provision(s,r.runtime);
  assert.deepEqual(r.value.ports.map(p=>p.portNumber),[3140]);
  assert.equal(s.socks_port,undefined);assert.equal(s.socks_listener,18002);
  assert.equal(s.listener,18001);assert.equal(s.tunnel_id,'devhub-user-a.use1');
});
test('known partial multi-port migrations recover and exact readbacks precede state advancement',async t=>{
  const {manager:m,session:s}=await fixture(t,{socksEnabled:true,socksPort:3280,proxyPort:3240});
  for(const ports of [[],[3240],[3280],[3140],[3280,3240]]){
    s.proxy_port=3140;delete s.socks_port;const r=remote(s,ports);await m.provision(s,r.runtime);
    assert.deepEqual(r.value.ports.map(p=>p.portNumber).sort(),[3240,3280]);
    assert.equal(s.proxy_port,3240);assert.equal(s.socks_port,3280);
  }
  s.proxy_port=3140;delete s.socks_port;const r=remote(s,[3140]);
  r.runtime.cli=async()=> '{}';await assert.rejects(m.provision(s,r.runtime),/PORT_POLICY_CHANGED/);
  assert.equal(s.proxy_port,3140);assert.equal(s.socks_port,undefined);
});
test('unknown ports, duplicated ports and explicit ACL grants fail before any remote mutation',async t=>{
  const {manager:m,session:s}=await fixture(t,{socksEnabled:true});
  for(const mutate of [r=>r.value.ports.push({portNumber:22}),r=>r.value.ports.push({portNumber:3140}),r=>r.value.ports.push({portNumber:3180,accessControl:{entries:[{}]}}),r=>r.value.accessControl.entries.push({})]){
    const r=remote(s);mutate(r);await assert.rejects(m.provision(s,r.runtime));assert.equal(r.commands.length,0);
  }
});
test('SOCKS persisted mappings reject collisions, missing allocation and malformed cursor',async t=>{
  const {manager:m,session:s,dir}=await fixture(t,{socksEnabled:true});
  const good=structuredClone(m.store.state);
  for(const mutate of [s=>s.socks_listener=s.listener,s=>s.socks_listener=3140,s=>s.socks_listener=20000,s=>s.socks_port=3180,s=>{s.proxy_port=3140;s.socks_port=3140},s=>{s.proxy_port=3140;s.socks_port=s.socks_listener}]){
    m.store.state=structuredClone(good);mutate(m.store.state.sessions[0]);await m.store.save();
    await assert.rejects(new StateStore(dir,m.config).load());
  }
});
test('SOCKS policy requires stopped sessions, rejects web collision and rolls back failed persistence',async t=>{
  const {manager:m,session:s}=await fixture(t);m.webPort=8082;
  await assert.rejects(m.applyPolicy({socksPort:8082},async()=>{}),/WEB_PORT_COLLISION/);
  s.desired=true;await assert.rejects(m.applyPolicy({socksEnabled:true},async()=>{}),/STOP_SESSIONS_FIRST/);s.desired=false;
  await assert.rejects(m.applyPolicy({socksEnabled:true,socksPort:3280},async()=>{throw Error('persist failed')}),/persist failed/);
  assert.equal(m.config.socksEnabled,false);assert.equal(m.config.socksPort,3180);
});
