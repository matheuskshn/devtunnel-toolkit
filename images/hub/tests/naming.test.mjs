import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { parseConfig, resolveTunnelName, bindIdentity } from '../dist/model.js';
import { loadConfig } from '../dist/config.js';
import { StateStore } from '../dist/state.js';
import { Manager } from '../dist/manager.js';

const microsoft = (login = 'Alice.Smith@example.com', id = 'object-a') => ({
  provider: 'microsoft', user_id: `microsoft:tenant-a:${id}`, user_login: login,
});
async function fixture(t, config = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'hub-naming-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const store = new StateStore(directory, parseConfig(config));
  await store.load();
  return store;
}

test('automatic names use authenticated provider login, normalize and reject invalid lengths', () => {
  assert.equal(resolveTunnelName('{hub_id}-{username}', 'devhub', microsoft()), 'devhub-alice-smith');
  assert.equal(resolveTunnelName('team-{username}', 'devhub', {
    provider: 'github', user_id: 'github:1001', user_login: 'Alice-Dev',
  }), 'team-alice-dev');
  assert.equal(resolveTunnelName('{username}-{hub_id}', 'devhub', microsoft('ALICE_..Smith@example.com')), 'alice-smith-devhub');
  for (const login of ['@example.com', '!!!@example.com', `${'a'.repeat(44)}@example.com`]) {
    assert.throws(() => resolveTunnelName('{hub_id}-{username}', 'devhub', microsoft(login)), /INVALID_TUNNEL/);
  }
});

test('template configuration is validated in JSON and environment', async t => {
  const store = await fixture(t);
  const file = path.join(store.directory, 'config.json');
  await writeFile(file, JSON.stringify({ tunnelNameTemplate: 'file-{username}' }));
  assert.equal((await loadConfig(file, {})).tunnelNameTemplate, 'file-{username}');
  assert.equal((await loadConfig(file, { HUB_TUNNEL_NAME_TEMPLATE: '{hub_id}-{username}' })).tunnelNameTemplate, '{hub_id}-{username}');
  for (const template of ['', null, 42, '{hostname}-{username}', '{hub_id}', '{username}.use1', '../{username}', 'UPPER-{username}', '{username}\n', 'x'.repeat(201)]) {
    assert.throws(() => parseConfig({ tunnelNameTemplate: template }), /INVALID_TUNNEL_NAME_TEMPLATE/);
  }
  await assert.rejects(loadConfig(file, { HUB_TUNNEL_NAME_TEMPLATE: '' }), /INVALID_TUNNEL_NAME_TEMPLATE/);
  await writeFile(file, JSON.stringify({ tunnelNameTemplate: '{unknown}' }));
  await assert.rejects(loadConfig(file, { HUB_TUNNEL_NAME_TEMPLATE: '{username}' }), /INVALID_TUNNEL_NAME_TEMPLATE/);
});

test('pending template survives restart and configuration changes; resolved name is immutable', async t => {
  const store = await fixture(t);
  const session = store.add('operator-alias', 'microsoft');
  assert.equal(session.tunnel_name, undefined);
  assert.throws(() => store.resolveName(session), /AUTH_REQUIRED/);
  await store.save();
  const restored = new StateStore(store.directory, parseConfig({ tunnelNameTemplate: 'new-{username}' }));
  await restored.load();
  const current = restored.get(session.id);
  bindIdentity(current, microsoft()); restored.resolveName(current);
  assert.equal(current.tunnel_name, 'devhub-alice-smith');
  assert.equal(current.tunnel_name_template, undefined);
  current.status = 'ready'; current.tunnel_id = 'devhub-alice-smith.use1';
  bindIdentity(current, microsoft('renamed@example.com')); restored.resolveName(current);
  await restored.save();
  const next = new StateStore(store.directory, restored.config); await next.load();
  assert.equal(next.get(session.id).tunnel_name, 'devhub-alice-smith');
  assert.equal(next.get(session.id).listener, 18001);
  assert.equal(next.get(session.id).tunnel_id, 'devhub-alice-smith.use1');
});

test('legacy and explicit names remain unchanged without requiring a new login', async t => {
  const store = await fixture(t);
  const legacy = store.add('user-a', 'microsoft', 'legacy-fixed');
  legacy.identity = microsoft(); legacy.tunnel_id = 'legacy-fixed.use1'; legacy.status = 'stopped';
  await store.save();
  const next = new StateStore(store.directory, parseConfig({ tunnelNameTemplate: 'new-{username}' }));
  await next.load(); next.resolveName(next.get('user-a'));
  assert.equal(next.get('user-a').tunnel_name, 'legacy-fixed');
  assert.equal(next.get('user-a').tunnel_id, 'legacy-fixed.use1');
  const manual = next.add('user-b', 'github', 'manual-fixed'); next.resolveName(manual);
  assert.equal(manual.tunnel_name, 'manual-fixed');
});

test('normalized names collide across accounts/providers and tombstones, without reassignment', async t => {
  const store = await fixture(t);
  const a = store.add('user-a', 'microsoft'); a.identity = microsoft(); store.resolveName(a);
  a.status = 'removed';
  const b = store.add('user-b', 'microsoft'); b.identity = microsoft('alice_smith@another.example', 'object-b');
  const c = store.add('user-c', 'github'); c.identity = { provider: 'github', user_id: 'github:1002', user_login: 'alice-smith' };
  for (const session of [b, c]) {
    assert.throws(() => store.resolveName(session), /SESSION_OR_NAME_RESERVED/);
    assert.equal(session.tunnel_name, undefined);
    assert.equal(session.tunnel_name_template, '{hub_id}-{username}');
  }
  assert.throws(() => store.add('user-d', 'github', a.tunnel_name), /SESSION_OR_NAME_RESERVED/);
  await store.save(); await new StateStore(store.directory, store.config).load();
});

test('malformed pending state cannot bypass fixed-name validation', async t => {
  const store = await fixture(t); const s = store.add('user-a', 'microsoft');
  for (const change of [
    { tunnel_name_template: undefined }, { tunnel_name_template: '{unknown}' },
    { tunnel_name: 'invalid.name' }, { tunnel_name: 'valid-name' },
    { tunnel_id: 'fixed-name.use1' }, { desired: true }, { status: 'ready' },
  ]) {
    await writeFile(path.join(store.directory, 'state.json'), JSON.stringify({ ...store.state, sessions: [{ ...s, ...change }] }));
    await assert.rejects(new StateStore(store.directory, store.config).load(), /INVALID_STATE/);
  }
});

test('concurrent verified logins reserve a name once and persist the conflict', async t => {
  const store = await fixture(t);
  const manager = new Manager(store.config, store.directory, path.join(store.directory, 'run'));
  await manager.store.load(); manager.configureSquid = async () => {}; manager.log = () => {};
  manager.runtime = async s => ({ login: async () => {
    await Promise.resolve(); bindIdentity(s, microsoft('same@example.com', s.id));
  }});
  await manager.dispatch(['session', 'add', 'user-a'], () => {});
  await manager.dispatch(['session', 'add', 'user-b'], () => {});
  const results = await Promise.allSettled(['user-a', 'user-b'].map(id => manager.dispatch(['session', 'login', id], () => {})));
  assert.equal(results.filter(r => r.status === 'fulfilled').length, 1);
  assert.equal(results.find(r => r.status === 'rejected').reason.code, 'SESSION_OR_NAME_RESERVED');
  const restored = new StateStore(store.directory, store.config); await restored.load();
  assert.equal(restored.state.sessions.filter(s => s.tunnel_name === 'devhub-same').length, 1);
  assert.equal(restored.state.sessions.find(s => !s.tunnel_name).status, 'error');
});

test('remote provisioning cannot run before name reservation is durably saved', async t => {
  const store = await fixture(t);
  const manager = new Manager(store.config, store.directory, path.join(store.directory, 'run'));
  await manager.store.load();
  const s = manager.store.add('user-a', 'microsoft'); s.identity = microsoft();
  manager.runtime = async () => ({ identity: async () => s.identity });
  let provisioned = false; manager.provision = async () => { provisioned = true; };
  manager.store.save = async () => { throw Error('synthetic-persistence-failure'); };
  await assert.rejects(manager.start(s), /synthetic-persistence-failure/);
  assert.equal(provisioned, false);
});
