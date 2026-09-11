import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { parseEnv } from 'node:util';
import os from 'node:os';
import path from 'node:path';
import { loadConfig } from '../dist/config.js';

async function fixture(t, content = '{}') {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'hub-config-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'config.json');
  await writeFile(file, content);
  return file;
}
test('public environment example is valid and contains no login credentials', async t => {
  const env = parseEnv(await readFile(new URL('../.env.example', import.meta.url), 'utf8'));
  const config = await loadConfig(await fixture(t), env);
  assert.equal(config.hubId,'devhub');
  assert.deepEqual(config.allowedProviders,['microsoft']);
  assert.deepEqual(config.allowedMicrosoftTenants,['00000000-0000-0000-0000-000000000000']);
  assert.deepEqual(config.allowedDomains,['service.example.com','.example.org']);
  assert.equal(env.HUB_CONFIG,undefined);
  assert.equal(env.HUB_DATA_DIR,'/data');
  assert.equal(env.HUB_RUN_DIR,'/run/hub');
  assert.equal(env.HUB_WEB_REQUIRE_PASSWORD_CHANGE, 'true');
  assert.ok(Object.keys(env).every(key => key === 'HUB_WEB_REQUIRE_PASSWORD_CHANGE' || !/TOKEN|PASSWORD|SECRET/.test(key)));
});
test('environment overrides file fields and preserves unmodified fields and defaults', async t => {
  const file = await fixture(t, JSON.stringify({hubId:'file-hub',maxSessions:7,allowedDomains:['old.example.com']}));
  const config = await loadConfig(undefined, {HUB_CONFIG:file,HUB_ID:'env-hub',HUB_ALLOWED_DOMAINS:'service.example.com, .example.org'});
  assert.equal(config.hubId,'env-hub'); assert.equal(config.maxSessions,7);
  assert.deepEqual(config.allowedDomains,['service.example.com','.example.org']);
  assert.deepEqual(config.allowedPorts,[80,443]);
});
test('all policy environment fields use the same validated configuration schema', async t => {
  const config = await loadConfig(await fixture(t), {
    HUB_ID:'test-hub',HUB_PROXY_PORT:'3210',HUB_LISTENER_START:'19001',HUB_LISTENER_END:'19999',HUB_MAX_SESSIONS:'10',
    HUB_ALLOWED_DOMAINS:'service.example.com',HUB_ALLOWED_PORTS:'80, 443,22',HUB_CONNECT_PORTS:'443,22',
    HUB_ALLOWED_PROVIDERS:'microsoft',HUB_ALLOWED_MICROSOFT_TENANTS:'00000000-0000-0000-0000-000000000000',
    HUB_HEALTH_PORT:'8081',HUB_MAINTENANCE_SECONDS:'120',HUB_RUN_DIR:'/tmp/hub',
  });
  assert.deepEqual(config, {hubId:'test-hub',tunnelNameTemplate:'{hub_id}-{username}',listenerStart:19001,listenerEnd:19999,maxSessions:10,
    allowedDomains:['service.example.com'],allowAllDomains:false,allowedPorts:[80,443,22],connectPorts:[443,22],
    allowedProviders:['microsoft'],allowedMicrosoftTenants:['00000000-0000-0000-0000-000000000000'],
    healthPort:8081,proxyPort:3210,socksPort:3180,socksEnabled:false,maintenanceSeconds:120});
});
test('all-domain access requires an explicit boolean and supports environment override',async t=>{
  const file=await fixture(t,'{"allowAllDomains":true}');
  assert.equal((await loadConfig(file,{})).allowAllDomains,true);
  assert.equal((await loadConfig(file,{HUB_ALLOW_ALL_DOMAINS:'false'})).allowAllDomains,false);
  assert.equal((await loadConfig(await fixture(t),{HUB_ALLOW_ALL_DOMAINS:'true'})).allowAllDomains,true);
  for(const value of ['','1','yes','TRUE','false;allow all'])await assert.rejects(loadConfig(file,{HUB_ALLOW_ALL_DOMAINS:value}),/INVALID_HUB_ALLOW_ALL_DOMAINS/);
  await assert.rejects(loadConfig(await fixture(t,'{"allowAllDomains":"true"}'),{}),/INVALID_ALLOW_ALL_DOMAINS/);
});
test('empty list overrides clear lists; empty required lists and malformed values fail closed', async t => {
  const file = await fixture(t, '{"allowedDomains":["service.example.com"]}');
  assert.deepEqual((await loadConfig(file,{HUB_ALLOWED_DOMAINS:''})).allowedDomains,[]);
  for (const env of [
    {HUB_MAX_SESSIONS:''},{HUB_MAX_SESSIONS:'1e2'},{HUB_MAX_SESSIONS:'10oops'},{HUB_MAX_SESSIONS:'0'},
    {HUB_HEALTH_PORT:'3140'},{HUB_LISTENER_START:'-1'},{HUB_ALLOWED_PORTS:'443,'},
    {HUB_ALLOWED_PORTS:''},{HUB_CONNECT_PORTS:'22'},{HUB_ALLOWED_DOMAINS:'*'},
    {HUB_ALLOWED_DOMAINS:'a.example.com,,b.example.com'},{HUB_ALLOWED_DOMAINS:'secret\nvalue'},
    {HUB_ALLOWED_PROVIDERS:''},{HUB_ALLOWED_PROVIDERS:'unknown'},{HUB_ALLOWED_MICROSOFT_TENANTS:'secret-value'},
  ]) await assert.rejects(loadConfig(file,env), e => /^INVALID_/.test(e.message) && !e.message.includes('secret'));
});
test('explicit missing paths and malformed files fail even with environment overrides', async t => {
  const file = await fixture(t, '{broken');
  await assert.rejects(loadConfig(file,{}), /INVALID_CONFIG_JSON/);
  await assert.rejects(loadConfig(`${file}.missing`,{HUB_ALLOWED_DOMAINS:'service.example.com'}), /CONFIG_FILE_NOT_FOUND/);
  await assert.rejects(loadConfig(undefined,{HUB_CONFIG:''}), /INVALID_CONFIG_PATH/);
  await writeFile(file,'{"unknown":true}');
  await assert.rejects(loadConfig(file,{}), /UNKNOWN_CONFIG_KEY/);
  await writeFile(file,'{"maxSessions":0}');
  await assert.rejects(loadConfig(file,{HUB_MAX_SESSIONS:'10'}), /INVALID_SESSION_LIMIT/);
});
