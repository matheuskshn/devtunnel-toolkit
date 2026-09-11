// Explicit local integration test. Never starts Compose, edits host networking,
// connects to an external VPN or uses a real account/client profile.
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import net from 'node:net';
import path from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {trustedExecutable} from './trusted-executable.mjs';

const image = process.env.OPENVPN_TEST_IMAGE ?? 'devtunnel-toolkit-openvpn:hardening-local';
const serviceImage = process.env.OPENVPN_SERVICE_TEST_IMAGE ?? 'devtunnel-toolkit-hub:web-local';
const prefix = `toolkit-vpn-smoke-${process.pid}`;
const dockerExecutable = trustedExecutable('docker');
const root = path.resolve(import.meta.dirname, '../..');
const compose = JSON.parse(docker(['compose', '--env-file', '/dev/null', '-f', path.join(root, 'compose.yml'),
  'config', '--format', 'json']).stdout);
const health = compose.services.openvpn.healthcheck.test[1].replaceAll('$$', '$');
const caps = ['--cap-drop', 'ALL', '--cap-add', 'NET_ADMIN', '--cap-add', 'SETUID', '--cap-add', 'SETGID',
  '--security-opt', 'no-new-privileges', '--device', '/dev/net/tun:/dev/net/tun'];
function docker(args, ok = true) {
  const result = spawnSync(dockerExecutable, args, {encoding: 'utf8', timeout: 120000, maxBuffer: 4 * 1024 * 1024});
  if (ok && result.status !== 0) throw new Error(`docker ${args.slice(0, 2).join(' ')}: ${result.stderr || result.stdout}`);
  return result;
}
function inspect(name) { return JSON.parse(docker(['inspect', name]).stdout)[0]; }
async function waitFor(check, label) {
  for (let attempt = 0; attempt < 60; attempt++) {
    if (check()) return;
    await delay(500);
  }
  throw new Error(`Timed out: ${label}`);
}
function vpnIdentity(container) {
  const status = docker(['exec', container, 'sh', '-c',
    'for p in /proc/[0-9]*/comm; do if [ "$(cat "$p" 2>/dev/null)" = openvpn ]; then cat "${p%/comm}/status"; fi; done']).stdout;
  assert.match(status, /^Uid:\s+65534\s+65534\s+65534\s+65534$/m);
  assert.match(status, /^Gid:\s+65534\s+65534\s+65534\s+65534$/m);
  const effective = status.match(/^CapEff:\s+([0-9a-f]+)$/m)?.[1];
  assert.ok(effective);
  // OpenVPN's SITNL implementation may retain NET_ADMIN after dropping UID.
  // It remains confined to this private network namespace, never the host.
  assert.equal(BigInt(`0x${effective}`) & ~0x1000n, 0n);
  assert.match(status, /^NoNewPrivs:\s+1$/m);
}
function httpFromClient(client, destination, timeout = 5) {
  assert.match(destination, /^\d+\.\d+\.\d+\.\d+$/);
  return docker(['exec', client, 'timeout', String(timeout), 'bash', '-c',
    'exec 3<>/dev/tcp/"$1"/18080; printf "GET / HTTP/1.1\r\nHost: service.example.com\r\nConnection: close\r\n\r\n" >&3; cat <&3',
    'probe', destination], false);
}
async function loopbackPort(port) {
  await new Promise((resolve, reject) => {
    const socket = net.connect({host: '127.0.0.1', port});
    socket.once('connect', () => { socket.destroy(); resolve(); });
    socket.once('error', reject);
    socket.setTimeout(3000, () => { socket.destroy(); reject(new Error('Loopback publication timed out')); });
  });
}
for (const [index, protocol] of ['tcp', 'udp'].entries()) {
  const server = `${prefix}-${protocol}-server`, client = `${prefix}-${protocol}-client`;
  const service = `${prefix}-${protocol}-service`, transport = `${prefix}-${protocol}-transport`;
  const backend = `${prefix}-${protocol}-backend`, volume = `${prefix}-${protocol}-data`;
  const port = 53195 + index;
  try {
    docker(['volume', 'create', volume]);
    // A normal private bridge, as in Compose, enables a loopback-only host
    // publication. No external destinations are contacted by this test.
    docker(['network', 'create', transport]);
    docker(['network', 'create', '--internal', backend]);
    const subnet = JSON.parse(docker(['network', 'inspect', backend]).stdout)[0].IPAM.Config[0].Subnet;
    const [address, bits] = subnet.split('/');
    const netmask = [0, 1, 2, 3].map(i => (0xffffffff << (32 - Number(bits))) >>> ((3 - i) * 8) & 255).join('.');
    docker(['run', '--detach', '--name', service, '--network', backend, '--read-only', '--cap-drop', 'ALL',
      '--security-opt', 'no-new-privileges', '--entrypoint', 'node', serviceImage, '-e',
      'require("node:http").createServer((q,s)=>s.end("synthetic-vpn-success")).listen(18080,"0.0.0.0")']);
    const destination = inspect(service).NetworkSettings.Networks[backend].IPAddress;
    docker(['create', '--name', server, '--network', transport, '--network-alias', 'vpn-server', ...caps,
      '--publish', `127.0.0.1::${port}/${protocol}`,
      '--sysctl', 'net.ipv4.ip_forward=1', '--mount', `type=volume,src=${volume},dst=/etc/openvpn`,
      '--env', `OVPN_PROTO=${protocol}`, '--env', `OVPN_PORT=${port}`, '--env', 'OVPN_LISTEN_ADDRESS=0.0.0.0',
      '--env', 'OVPN_REMOTE_HOST=vpn-server', '--env', `OVPN_REMOTE_PORT=${port}`,
      '--env', 'OVPN_CLIENT_NAME=synthetic-client', '--env', `OVPN_PUSH_ROUTES=${address} ${netmask}`,
      // Each test's private server namespace owns this subnet, never the host.
      '--env', 'OVPN_NETWORK=10.203.0.0', '--env', 'OVPN_CIDR=10.203.0.0/24', image]);
    docker(['network', 'connect', backend, server]);
    docker(['start', server]);
    await waitFor(() => {
      const logs = docker(['logs', server], false);
      return /Initialization Sequence Completed/.test(logs.stdout + logs.stderr);
    }, `${protocol} server initialization`);
    const serverInfo = inspect(server);
    assert.equal(serverInfo.HostConfig.Privileged, false);
    assert.notEqual(serverInfo.HostConfig.NetworkMode, 'host');
    assert.deepEqual(serverInfo.HostConfig.CapDrop, ['ALL']);
    assert.deepEqual(new Set(serverInfo.HostConfig.CapAdd.map(value => value.replace(/^CAP_/, ''))), new Set(['NET_ADMIN', 'SETGID', 'SETUID']));
    const publication = serverInfo.NetworkSettings.Ports[`${port}/${protocol}`];
    assert.equal(publication.length, 1);
    assert.equal(publication[0].HostIp, '127.0.0.1');
    if (protocol === 'tcp') await loopbackPort(Number(publication[0].HostPort));
    assert.match(docker(['exec', server, 'iptables', '-t', 'nat', '-S', 'POSTROUTING']).stdout,
      /-s 10\.203\.0\.0\/24 -j MASQUERADE/);
    assert.equal(docker(['exec', server, 'cat', '/proc/sys/net/ipv4/ip_forward']).stdout.trim(), '1');
    docker(['exec', server, 'sh', '-c', health]);
    vpnIdentity(server);
    docker(['run', '--detach', '--name', client, '--network', transport, ...caps,
      '--mount', `type=volume,src=${volume},dst=/profiles,readonly`,
      '--entrypoint', 'sleep', image, 'infinity']);
    assert.notEqual(httpFromClient(client, destination, 2).status, 0, 'Backend must be unreachable before VPN connects');
    const profile = '/profiles/clients/synthetic-client.ovpn';
    // Do not print the generated profile or private keys into diagnostics.
    assert.match(docker(['exec', client, 'sed', '-n', '/^remote /p', profile]).stdout, new RegExp(`remote vpn-server ${port}`));
    assert.match(docker(['exec', client, 'sed', '-n', '/^proto /p', profile]).stdout,
      new RegExp(protocol === 'tcp' ? 'proto tcp-client' : 'proto udp'));
    docker(['exec', client, 'openvpn', '--config', profile, '--user', 'nobody', '--group', 'nogroup',
      '--log', '/tmp/vpn-client.log', '--daemon']);
    await waitFor(() => /Initialization Sequence Completed/.test(
      docker(['exec', client, 'cat', '/tmp/vpn-client.log'], false).stdout), `${protocol} client TLS connection`);
    vpnIdentity(client);
    const response = httpFromClient(client, destination);
    assert.equal(response.status, 0, response.stderr);
    assert.match(response.stdout, /synthetic-vpn-success/);
    const keyPath = '/etc/openvpn/easy-rsa/pki/private/server.key';
    const before = docker(['exec', server, 'sha256sum', keyPath]).stdout;
    docker(['restart', '--time', '5', server]);
    assert.equal(docker(['exec', server, 'sha256sum', keyPath]).stdout, before, 'Restart must preserve the existing PKI');
    docker(['restart', '--time', '5', client]);
    docker(['exec', client, 'openvpn', '--config', profile, '--user', 'nobody', '--group', 'nogroup',
      '--log', '/tmp/vpn-client-restart.log', '--daemon']);
    await waitFor(() => /Initialization Sequence Completed/.test(
      docker(['exec', client, 'cat', '/tmp/vpn-client-restart.log'], false).stdout), `${protocol} client reconnect`);
    assert.match(httpFromClient(client, destination).stdout, /synthetic-vpn-success/);
    docker(['exec', server, 'sh', '-c', health]);
    vpnIdentity(server);
    console.log(`PASS OpenVPN ${protocol}: real TLS client/server, custom ports and loopback-only publication, private TUN/NAT, backend reachable only through VPN, PKI/reconnection preserved after restart, non-root daemons and no effective capability beyond namespaced NET_ADMIN`);
  } catch (error) {
    const logs = docker(['logs', server], false);
    console.error(logs.stdout + logs.stderr);
    console.error(docker(['exec', client, 'cat', '/tmp/vpn-client.log'], false).stdout);
    throw error;
  } finally {
    // Exact ephemeral objects created by this process; no shared resources.
    for (const name of [client, server, service]) docker(['rm', '--force', name], false);
    docker(['volume', 'rm', volume], false);
    for (const name of [transport, backend]) docker(['network', 'rm', name], false);
  }
}
