import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, readFile, stat, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const root = path.resolve(import.meta.dirname, '../..');
const run = (script, args = [], extra = {}) => spawnSync('bash', [path.join(root, script), ...args], {
  encoding: 'utf8', env: {PATH: '/usr/bin:/bin', ...extra},
});

test('OpenVPN refuses to destroy an incomplete PKI', async () => {
  const dir = await mkdtemp(path.join(tmpdir(), 'toolkit-pki-'));
  try {
    await mkdir(path.join(dir, 'easy-rsa'));
    const sentinel = path.join(dir, 'easy-rsa', 'recovery-marker');
    await writeFile(sentinel, 'keep');
    const result = run('images/openvpn/openvpn-entrypoint', ['client', 'test-client'], {OVPN_DIR: dir});
    assert.equal(result.status, 2);
    assert.match(result.stderr, /incomplete PKI exists/);
    assert.equal(await readFile(sentinel, 'utf8'), 'keep');
  } finally { await rm(dir, {recursive: true, force: true}); }
});

test('OpenVPN rejects path traversal and writes exported client keys privately', async () => {
  const dir = await mkdtemp(path.join(tmpdir(), 'toolkit-client-'));
  try {
    for (const name of ['../escape', 'bad/name', 'bad\nname', '-option']) {
      const result = run('images/openvpn/openvpn-entrypoint', ['client', name], {OVPN_DIR: dir});
      assert.equal(result.status, 2);
      assert.match(result.stderr, /client name/);
    }
    for (const folder of ['issued', 'private']) await mkdir(path.join(dir, 'easy-rsa/pki', folder), {recursive: true});
    for (const name of ['ca.crt', 'ta.key', 'issued/server.crt', 'private/server.key', 'issued/test-client.crt', 'private/test-client.key'])
      await writeFile(path.join(dir, 'easy-rsa/pki', name), 'synthetic fixture, not a credential');
    const result = run('images/openvpn/openvpn-entrypoint', ['client', 'test-client'], {OVPN_DIR: dir});
    assert.equal(result.status, 0, result.stderr);
    assert.equal((await stat(path.join(dir, 'clients/test-client.ovpn'))).mode & 0o777, 0o600);
  } finally { await rm(dir, {recursive: true, force: true}); }
});

test('Squid rejects multiline or sed-substitution injection in generated settings', () => {
  for (const env of [
    {SQUID_VISIBLE_HOSTNAME: 'proxy\nhttp_access allow all'},
    {SQUID_LISTEN_ADDRESS: '127.0.0.1|e touch /tmp/not-allowed'},
    {SQUID_SSL_PORTS: '443\nhttp_access allow all'},
    {SQUID_SAFE_PORTS: '80;443'},
  ]) {
    const result = run('images/squid/squid-entrypoint', ['squid'], env);
    assert.equal(result.status, 2);
  }
});

test('Kubernetes example disables API credentials and bounds disk and memory-backed storage separately', async () => {
  const manifest = await readFile(path.join(root, 'images/hub/examples/kubernetes/hub.yaml'), 'utf8');
  assert.match(manifest, /automountServiceAccountToken: false/);
  assert.match(manifest, /requests: \{cpu: 250m, memory: 512Mi, ephemeral-storage: 128Mi\}/);
  assert.match(manifest, /limits: \{cpu: "1", memory: 1Gi, ephemeral-storage: 512Mi\}/);
  assert.match(manifest, /emptyDir: \{medium: Memory, sizeLimit: 256Mi\}/);
  assert.match(manifest, /emptyDir: \{medium: Memory, sizeLimit: 128Mi\}/);
});

test('OpenVPN Compose confines network administration and drops unnecessary privileges', async () => {
  const compose = await readFile(path.join(root, 'compose.yml'), 'utf8');
  const vpn = compose.split('\n  openvpn:\n')[1].split('\nvolumes:\n')[0];
  assert.doesNotMatch(vpn, /network_mode:\s*host|privileged:\s*true/);
  assert.match(vpn, /cap_drop:\n\s+- ALL/);
  assert.match(vpn, /cap_add:\n\s+- NET_ADMIN\n\s+- SETUID\n\s+- SETGID/);
  assert.match(vpn, /no-new-privileges:true/);
  assert.match(vpn, /host_ip: "\$\{OVPN_LISTEN_ADDRESS:-127\.0\.0\.1\}"/);
  assert.match(vpn, /protocol: \$\{OVPN_PROTO:-tcp\}/);
  assert.match(vpn, /net\.ipv4\.ip_forward: "1"/);
  const entrypoint = await readFile(path.join(root, 'images/openvpn/openvpn-entrypoint'), 'utf8');
  assert.doesNotMatch(entrypoint, /sysctl -w/);
  assert.match(entrypoint, /IPv4 forwarding is disabled/);
  assert.match(entrypoint, /user nobody/);
  assert.match(entrypoint, /group nogroup/);
});

test('Images select smaller packaged providers without removing authentication services or package inventory', async () => {
  for (const file of ['images/hub/Dockerfile', 'images/squid/Dockerfile']) {
    const dockerfile = await readFile(path.join(root, file), 'utf8');
    assert.match(dockerfile, /systemd-standalone-sysusers/);
    assert.doesNotMatch(dockerfile, /--force-depends|--allow-remove-essential|rm[^\n]*\/var\/lib\/dpkg/);
    if (file.includes('hub')) {
      assert.match(dockerfile, /dbus-x11 gnome-keyring/);
      assert.match(dockerfile, /libsecret-1-0 libsecret-tools/);
    }
  }
});
