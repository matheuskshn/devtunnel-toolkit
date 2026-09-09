// Synthetic credentials only. Executed inside an isolated test container.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {readdir} from 'node:fs/promises';
import pg from '/opt/hub/node_modules/pg/lib/index.js';
import {PostgresPersistence,postgresConfig} from '/opt/hub/dist/postgres.js';
import {StateStore} from '/opt/hub/dist/state.js';
import {parseConfig} from '/opt/hub/dist/model.js';
import {SessionRuntime} from '/opt/hub/dist/auth.js';
import {command} from '/opt/hub/dist/processes.js';
const env={HUB_PG_HOST:'postgres',HUB_PG_DATABASE:'hubtest',HUB_PG_USER:'postgres',HUB_PG_PASSWORD:'synthetic-test-only',HUB_PG_SSLMODE:'disable',HUB_CREDENTIAL_KEY:randomBytes(32).toString('base64')};
const config=await postgresConfig(env),control=new pg.Client(config);await control.connect();
const stores=[],runtimes=[];
async function open(settings=env){const p=new PostgresPersistence(config,'testhub',settings,()=>{});stores.push(p);await p.open('/run/hub');return p;}
async function runtime(p,s){const r=new SessionRuntime(s,p.directory,`/run/hub/service-${s.id}`,()=>{},[],p);runtimes.push(r);await r.open();return r;}
const secret=(r,op,input)=>r.withCredentials(()=>command('secret-tool',[op,...(op==='store'?['--label=fixture']:[]),'hub-fixture','same-key'],r.env,{input}));
try {
  let p=await open(),store=new StateStore(p.directory,parseConfig({hubId:'testhub'}),p);await store.load();
  const a=store.add('user-a','microsoft'),b=store.add('user-b','microsoft');
  a.identity={provider:'microsoft',user_id:'microsoft:object-a',user_login:'user-a@example.com'};
  store.resolveName(a);
  a.desired=true;a.status='ready';a.tunnel_id='testhub-user-a.use1';await store.save();
  let ra=await runtime(p,a),rb=await runtime(p,b);
  await secret(ra,'store','synthetic-a');await secret(rb,'store','synthetic-b');
  await ra.close();ra=await runtime(p,a);assert.equal((await secret(ra,'lookup')).trim(),'synthetic-a');
  await assert.rejects(ra.identity(),/AUTH_REQUIRED/);
  const contender=new PostgresPersistence(config,'testhub',env,()=>{});stores.push(contender);
  await assert.rejects(contender.open('/run/hub'),/HUB_ALREADY_ACTIVE/);await contender.close();
  const rows=(await control.query('SELECT package,dirty FROM devtunnel_hub.credentials')).rows;
  assert.ok(rows.every(r=>r.package&&!r.dirty&&!r.package.includes('synthetic-')));
  const old=p.directory;await ra.close();await rb.close();await p.close();
  await assert.rejects(readdir(old));
  p=await open();assert.notEqual(p.directory,old);store=new StateStore(p.directory,parseConfig({hubId:'testhub'}),p);await store.load();
  assert.equal(store.get('user-a').listener,18001);assert.equal(store.get('user-a').tunnel_id,'testhub-user-a.use1');
  assert.equal(store.get('user-a').tunnel_name,'testhub-user-a');
  assert.equal(store.get('user-b').tunnel_name,undefined);
  assert.equal(store.get('user-b').tunnel_name_template,'{hub_id}-{username}');
  store.get('user-a').identity.user_login='renamed@example.com';store.resolveName(store.get('user-a'));
  assert.equal(store.get('user-a').tunnel_name,'testhub-user-a');await store.save();
  ra=await runtime(p,store.get('user-a'));rb=await runtime(p,store.get('user-b'));
  assert.equal((await secret(ra,'lookup')).trim(),'synthetic-a');assert.equal((await secret(rb,'lookup')).trim(),'synthetic-b');
  // A partial/failed operation still checkpoints a coherent resulting cache.
  await assert.rejects(ra.withCredentials(async()=>{await command('secret-tool',['store','--label=fixture','hub-fixture','same-key'],ra.env,{input:'synthetic-renewed'});throw Error('synthetic-failure');}));
  await p.beginAuth('user-b'); // Simulate abrupt death between journal and checkpoint.
  await ra.close();await rb.close();await p.close();
  const wrong=new PostgresPersistence(config,'testhub',{...env,HUB_CREDENTIAL_KEY:randomBytes(32).toString('base64')},()=>{});stores.push(wrong);
  await assert.rejects(wrong.open('/run/hub'),/DECRYPTION/);await wrong.close();
  const rotated={...env,HUB_CREDENTIAL_KEY_ID:'next',HUB_CREDENTIAL_KEY:randomBytes(32).toString('base64'),HUB_CREDENTIAL_PREVIOUS_KEYS:JSON.stringify({primary:env.HUB_CREDENTIAL_KEY})};
  p=await open(rotated);store=new StateStore(p.directory,parseConfig({hubId:'testhub'}),p);await store.load();
  assert.equal(store.get('user-b').status,'reauth_required');assert.equal(store.get('user-b').desired,false);
  ra=await runtime(p,store.get('user-a'));rb=await runtime(p,store.get('user-b'));
  assert.equal((await secret(ra,'lookup')).trim(),'synthetic-renewed');await assert.rejects(secret(rb,'lookup'));
  await secret(ra,'clear');await ra.close();await rb.close();await p.close();
  p=await open(rotated);store=new StateStore(p.directory,parseConfig({hubId:'testhub'}),p);await store.load();
  ra=await runtime(p,store.get('user-a'));await assert.rejects(secret(ra,'lookup'));
  await ra.close();await p.close();
  // An unexpectedly killed keyring must leave its checkpoint dirty, not publish
  // a cache whose last writes may not have reached the home directory.
  p=await open(rotated);store=new StateStore(p.directory,parseConfig({hubId:'testhub'}),p);await store.load();
  rb=await runtime(p,store.get('user-b'));
  await assert.rejects(rb.withCredentials(async()=>{
    const closed=new Promise(resolve=>rb.keyring.once('close',resolve));
    rb.keyring.kill('SIGKILL');await closed;
  }),/SERVICE/);
  assert.equal((await control.query("SELECT dirty FROM devtunnel_hub.credentials WHERE hub_id='testhub' AND session_id='user-b'")).rows[0].dirty,true);
  await rb.close();await p.close();
  // An interrupted state write cannot be silently accepted after losing ownership.
  let lost=false;const fenced=new PostgresPersistence(config,'fencedhub',env,()=>{lost=true;});stores.push(fenced);await fenced.open('/run/hub');
  await control.query("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name='devtunnel-toolkit-hub' AND pid<>pg_backend_pid()");
  await new Promise(r=>setTimeout(r,200));assert.equal(lost,true);await assert.rejects(fenced.read(),/UNAVAILABLE/);
  console.log('PASS PostgreSQL encrypted keyrings, fresh-directory restore, isolation, stable mapping, lock exclusion, interrupted checkpoint, failed-operation checkpoint, key rotation, removal and lost ownership');
} finally {
  for(const r of runtimes)await r.close().catch(()=>{});
  for(const p of stores)await p.close().catch(()=>{});
  await control.end();
}
