import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, rm, readFile, symlink, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { parseConfig, bindIdentity } from '../dist/model.js';
import { StateStore } from '../dist/state.js';
import { parseIdentity, parseJson, privateTunnel, canonicalTunnel, SessionRuntime } from '../dist/auth.js';
import { squidConfig, auditRecord } from '../dist/squid.js';
import { cleanEnvironment, command } from '../dist/processes.js';

const identity = { provider: 'github', user_id: 'github:1001', user_login: 'user-a' };
async function store(t, config = {}) {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'hub-test-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const store = new StateStore(dir, parseConfig(config)); await store.load(); return store;
}
test('fixed names and immutable listeners survive restart, tombstones prevent reuse', async t => {
  const first = await store(t);
  const a = first.add('user-a', 'github'); const b = first.add('user-b', 'microsoft', 'private-user-b');
  assert.equal(a.listener, 18001); assert.equal(b.listener, 18002);
  a.identity = identity; first.resolveName(a);
  assert.equal(a.tunnel_name, 'devhub-user-a');
  a.identity = identity; a.tunnel_id = 'devhub-user-a.use1'; a.status = 'removed';
  await first.save();
  const next = new StateStore(first.directory, first.config); await next.load();
  assert.deepEqual(next.state, first.state);
  assert.throws(() => next.add('user-a','github'), /RESERVED/);
  assert.throws(() => next.add('user-c','github','devhub-user-a'), /RESERVED/);
  assert.equal(next.add('user-c','github').listener, 18003);
  assert.equal((await stat(path.join(first.directory,'state.json'))).mode & 0o777, 0o600);
});
test('allocation limits, concurrent persistence and path traversal fail closed', async t => {
  const s = await store(t, { maxSessions: 2, listenerEnd: 18002 });
  for (const id of ['../user', '/tmp/user', 'USER', 'user\nfoo', '--help']) assert.throws(() => s.add(id,'github'));
  s.add('user-a','github'); const first = s.save();
  s.add('user-b','github'); await Promise.all([first, s.save()]);
  assert.equal(JSON.parse(await readFile(path.join(s.directory,'state.json'))).sessions.length, 2);
  assert.throws(() => s.add('user-c','github'), /SESSION_LIMIT/);
  s.get('user-a').status = 'removed'; assert.throws(() => s.add('user-c','github'), /POOL_EXHAUSTED/);
});
test('state refuses symlinks and incompatible port configuration', async t => {
  const s = await store(t); s.add('user-a','github'); await s.save();
  await assert.rejects(new StateStore(s.directory, parseConfig({listenerStart:19001,listenerEnd:19999})).load(), /MISMATCH/);
  const alias = `${s.directory}-alias`; await symlink(s.directory, alias); t.after(() => rm(alias));
  await assert.rejects(new StateStore(alias, s.config).load(), /UNSAFE/);
});
test('configuration rejects injection, wildcard internet, unsafe/reserved ports and unknown keys', () => {
  for (const c of [{allowedDomains:['all\nhttp_access allow all']}, {allowedDomains:['*']},
    {allowedDomains:['127.0.0.1']}, {listenerStart:3000,listenerEnd:4000}, {healthPort:18001},
    {allowedPorts:[0]}, {connectPorts:[22]}, {proxy:'http://example.com'}, {maxSessions:0},
    {allowedProviders:['unknown']}, {allowedMicrosoftTenants:['not-a-tenant-id']}]) {
    assert.throws(() => parseConfig(c));
  }
  assert.deepEqual(parseConfig({}).allowedDomains, []);
});
test('audit identity must come from CLI and stay bound to the stable provider identity', async () => {
  assert.deepEqual(parseIdentity({status:'Logged in',provider:'GitHub',username:'user-a',userId:1001},'github'), identity);
  const microsoft = parseIdentity({provider:'Microsoft',username:'user-a@example.com',userId:'object-a',tenantId:'tenant-a'},'microsoft');
  assert.equal(microsoft.user_id,'microsoft:tenant-a:object-a');
  const actualCliSchema = parseIdentity({status:'Logged in',provider:'microsoft',username:'user-a@example.com',objectId:'object-a',tenantId:'tenant-a'},'microsoft');
  assert.deepEqual(actualCliSchema,microsoft);
  for (const input of [{status:'Not logged in'}, {provider:'github',username:'alias-only'},
    {provider:'unknown',userId:'1001',username:'user-a'}]) assert.throws(() => parseIdentity(input,'github'));
  const session = {provider:'github'}; bindIdentity(session,identity);
  assert.throws(() => bindIdentity(session,{...identity,user_id:'github:1002'}), /IDENTITY_CHANGED/);
  bindIdentity(session,{...identity,user_login:'renamed-user-a'});
  assert.equal(session.identity.user_id,identity.user_id);
  assert.throws(() => bindIdentity(session,microsoft), /PROVIDER/);
  const runtime = new SessionRuntime({id:'user-a',provider:'microsoft'},'/data','/run/hub/test',()=>{},['tenant-a']);
  runtime.cli = async () => JSON.stringify({status:'Logged in',provider:'microsoft',username:'user-a@example.com',objectId:'object-a',tenantId:'tenant-a'});
  assert.equal((await runtime.identity()).tenant_id,'tenant-a');
  runtime.cli = async () => JSON.stringify({status:'Logged in',provider:'microsoft',username:'user-a@example.com',objectId:'object-a',tenantId:'tenant-b'});
  await assert.rejects(runtime.identity(),/TENANT_NOT_ALLOWED/);
});
test('private tunnel validation rejects shared ACLs, extra ports and changed canonical ID', () => {
  const tunnel = { tunnelId:'devhub-user-a',clusterId:'use1',ports:[{portNumber:3140}],accessControl:{entries:[]} };
  const s = { tunnel_id:'devhub-user-a.use1' };
  assert.equal(canonicalTunnel({tunnel}),s.tunnel_id);
  assert.equal(privateTunnel(tunnel,s).ports[0].portNumber,3140);
  assert.throws(() => privateTunnel({...tunnel,accessControl:undefined},s), /ACL_SCHEMA/);
  assert.throws(() => privateTunnel({...tunnel,accessControl:{entries:[{type:'Anonymous'}]}},s), /OWNER_ONLY/);
  assert.throws(() => privateTunnel({...tunnel,ports:[{portNumber:3140,accessControl:{entries:[{}]}}]},s), /OWNER_ONLY/);
  assert.throws(() => privateTunnel({...tunnel,ports:[{portNumber:8080}]},s), /PORT_POLICY/);
  assert.throws(() => privateTunnel({...tunnel,clusterId:'euw1'},s), /ID_CHANGED/);
  assert.deepEqual(parseJson('Welcome to dev tunnels!\n{"status":"Not logged in"}\n'),{status:'Not logged in'});
});
test('Squid config fails closed and logs authority only; enrichment cannot be forged by client', () => {
  const sessions = [{id:'user-a',listener:18001,identity,tunnel_id:'devhub-user-a.use1',status:'ready'}];
  const c = squidConfig(parseConfig({allowedDomains:['service.example.com']}),sessions,'/run/hub');
  assert.match(c,/http_port 127.0.0.1:18001 name=s18001/);
  assert.ok(c.indexOf('http_access allow session_listener allowed_destinations') < c.indexOf('http_access deny all'));
  assert.doesNotMatch(c,/%ru|%>ru|%un|credentials|http_access allow all|user-a/);
  const denied = squidConfig(parseConfig({}),sessions,'/run/hub');
  assert.doesNotMatch(denied,/http_access allow/);
  const broad=squidConfig(parseConfig({allowAllDomains:true}),sessions,'/run/hub');
  assert.match(broad,/http_access allow session_listener\n/);
  for(const rule of ['deny metadata','deny loopback_destination','deny !local_client','deny !allowed_ports','deny CONNECT !connect_ports']){
    assert.ok(broad.indexOf(`http_access ${rule}`)<broad.indexOf('http_access allow session_listener'));
  }
  assert.doesNotMatch(squidConfig(parseConfig({allowAllDomains:true}),[],'/run/hub'),/http_access allow/);
  const record = auditRecord('1700000000.123 18001 CONNECT service.example.com 443 200 1024 42',sessions);
  assert.equal(record.user_login,'user-a'); assert.equal(record.duration_ms,42);
  assert.equal(record.attribution,'session_listener'); assert.equal(record.destination,'service.example.com');
  assert.equal(auditRecord('1700000000.123 18002 CONNECT service.example.com 443 200 1024 42',sessions),undefined);
  assert.equal(auditRecord('unexpected raw output containing sensitive values',sessions),undefined);
});
test('subprocess environment excludes inherited credentials and errors redact command output', async () => {
  process.env.HUB_TEST_SECRET = 'fake-secret';
  assert.equal(cleanEnvironment().HUB_TEST_SECRET,undefined); delete process.env.HUB_TEST_SECRET;
  await assert.rejects(command(process.execPath,['-e',"console.error('fake-sensitive-value'); process.exit(1)"],cleanEnvironment()), /^Error: COMMAND_FAILED$/);
  await assert.rejects(command(process.execPath,['-e',"console.error('Tunnel not found in use1: devhub-user-a'); process.exit(1)"],cleanEnvironment()), /TUNNEL_NOT_FOUND/);
  await assert.rejects(command(process.execPath,['-e',"console.error('Tunnel service error: Conflict with existing entity. Retry tunnel operation.'); process.exit(1)"],cleanEnvironment()), /TUNNEL_NAME_CONFLICT/);
  await assert.rejects(command(process.execPath,['-e','setInterval(()=>{},1000)'],cleanEnvironment(),{timeout:50}), /TIMEOUT/);
});
