import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { privateTunnel } from '../dist/auth.js';
import { parseConfig } from '../dist/model.js';
import { StateStore } from '../dist/state.js';

const session = {
  id: 'user-a', provider: 'github', tunnel_name: 'synthetic-user-a',
  tunnel_id: 'synthetic-user-a.use1', listener: 18001,
  identity: { provider: 'github', user_id: 'github:1001', user_login: 'user-a' },
  desired: false, status: 'ready', created_at: '2026-01-01T00:00:00.000Z',
};
function tunnel(acl) {
  return { tunnelId: 'synthetic-user-a', clusterId: 'use1', accessControl: { entries: [] },
    ports: [{ portNumber: 3140, ...(acl === undefined ? {} : { accessControl: acl }) }] };
}

test('only omitted or explicit empty object port ACLs are owner-only', () => {
  assert.equal(privateTunnel(tunnel(undefined), session).ports[0].portNumber, 3140);
  assert.equal(privateTunnel(tunnel({ entries: [] }), session).ports[0].portNumber, 3140);
  for (const acl of [null, false, 0, '', [], [ { entries: [] } ], {}, { entries: null }, { entries: {} }, { entries: [ { type: 'anonymous', scopes: ['connect'] } ] }]) {
    assert.throws(() => privateTunnel(tunnel(acl), session), /PORT_NOT_OWNER_ONLY/);
  }
});

async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'hub-state-invariants-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const config = parseConfig({});
  return async sessions => {
    await writeFile(path.join(directory, 'state.json'), JSON.stringify({
      version: 1, hub_id: config.hubId, next_listener: 18010, sessions,
    }), { mode: 0o600 });
    const store = new StateStore(directory, config);
    await store.load();
    return store;
  };
}

test('legacy state with matching fixed tunnel identity loads without new proxy fields', async t => {
  const load = await fixture(t);
  const second = { ...session, id: 'user-b', listener: 18002, tunnel_name: 'synthetic-user-b', tunnel_id: 'synthetic-user-b.use1' };
  const store = await load([session, second]);
  assert.deepEqual(store.state.sessions.map(s => s.tunnel_id), ['synthetic-user-a.use1', 'synthetic-user-b.use1']);
  assert.equal(store.state.sessions[0].proxy_port, undefined);
  assert.equal(store.state.sessions[0].socks_listener, undefined);
});

test('persisted canonical tunnel ID must match the session-reserved tunnel name', async t => {
  const load = await fixture(t);
  for (const tunnel_id of ['another-user.use1', 'synthetic-user-b.use1', '', null, 0, false]) {
    await assert.rejects(load([{ ...session, tunnel_id }]), /INVALID_STATE/);
  }
});

test('tunnel ownership cannot be aliased across sessions or reused from a tombstone', async t => {
  const load = await fixture(t);
  const duplicate = { ...session, id: 'user-b', listener: 18002 };
  await assert.rejects(load([session, duplicate]), /DUPLICATE_STATE_MAPPING/);
  await assert.rejects(load([{ ...session, status: 'removed' }, duplicate]), /DUPLICATE_STATE_MAPPING/);
  // A different claimed name does not make the same canonical resource another owner.
  await assert.rejects(load([session, { ...duplicate, tunnel_name: 'synthetic-user-b' }]), /INVALID_STATE/);
});
