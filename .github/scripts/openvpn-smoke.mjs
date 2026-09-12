// Explicit local integration test. Never starts Compose, edits host networking,
// connects to an external VPN or uses a real account/client profile.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import net from "node:net";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { trustedExecutable } from "./trusted-executable.mjs";

const image =
  process.env.OPENVPN_TEST_IMAGE ?? "devtunnel-toolkit-openvpn:rootless-local";
const serviceImage =
  process.env.OPENVPN_SERVICE_TEST_IMAGE ?? "devtunnel-toolkit-hub:web-local";
const prefix = `toolkit-vpn-smoke-${process.pid}`;
const dockerExecutable = trustedExecutable("docker");
const root = path.resolve(import.meta.dirname, "../..");
const compose = JSON.parse(
  docker([
    "compose",
    "--env-file",
    "/dev/null",
    "-f",
    path.join(root, "compose.yml"),
    "config",
    "--format",
    "json",
  ]).stdout,
);
const health = compose.services.openvpn.healthcheck.test[1].replaceAll(
  "$$",
  "$",
);
const caps = [
  "--cap-drop",
  "ALL",
  "--security-opt",
  "no-new-privileges",
  "--device",
  "/dev/net/tun:/dev/net/tun",
];
function docker(args, ok = true) {
  const result = spawnSync(dockerExecutable, args, {
    encoding: "utf8",
    timeout: 120000,
    maxBuffer: 4 * 1024 * 1024,
  });
  if (ok && result.status !== 0)
    throw new Error(
      `docker ${args.slice(0, 2).join(" ")}: ${result.stderr || result.stdout}`,
    );
  return result;
}
function inspect(name) {
  return JSON.parse(docker(["inspect", name]).stdout)[0];
}
async function waitFor(check, label) {
  for (let attempt = 0; attempt < 60; attempt++) {
    if (check()) return;
    await delay(500);
  }
  throw new Error(`Timed out: ${label}`);
}
function vpnIdentity(container) {
  const status = docker([
    "exec",
    container,
    "sh",
    "-c",
    'for p in /proc/[0-9]*/comm; do if [ "$(cat "$p" 2>/dev/null)" = openvpn ]; then cat "${p%/comm}/status"; fi; done',
  ]).stdout;
  assert.match(status, /^Uid:\s+10001\s+10001\s+10001\s+10001$/m);
  assert.match(status, /^Gid:\s+10001\s+10001\s+10001\s+10001$/m);
  const effective = status.match(/^CapEff:\s+([0-9a-f]+)$/m)?.[1];
  assert.ok(effective);
  assert.equal(BigInt(`0x${effective}`), 0n);
  assert.match(status, /^NoNewPrivs:\s+1$/m);
}
function httpFromClient(client, destination, timeout = 5) {
  assert.match(destination, /^\d+\.\d+\.\d+\.\d+$/);
  return docker(
    [
      "exec",
      client,
      "timeout",
      String(timeout),
      "bash",
      "-c",
      'exec 3<>/dev/tcp/"$1"/18080; printf "GET / HTTP/1.1\r\nHost: service.example.com\r\nConnection: close\r\n\r\n" >&3; cat <&3',
      "probe",
      destination,
    ],
    false,
  );
}
async function loopbackPort(port) {
  await new Promise((resolve, reject) => {
    const socket = net.connect({ host: "127.0.0.1", port });
    socket.once("connect", () => {
      socket.destroy();
      resolve();
    });
    socket.once("error", reject);
    socket.setTimeout(3000, () => {
      socket.destroy();
      reject(new Error("Loopback publication timed out"));
    });
  });
}
// Fail before creating or rewriting any private state.
for (const [args, message] of [
  [
    [
      "--user",
      "0:0",
      "--cap-drop",
      "ALL",
      "--security-opt",
      "no-new-privileges",
    ],
    /server must run as UID\/GID 10001/,
  ],
  [["--cap-drop", "ALL"], /requires cap-drop ALL and no-new-privileges/],
]) {
  const rejected = docker(
    ["run", "--rm", "--network", "none", ...args, image, "server"],
    false,
  );
  assert.equal(rejected.status, 2);
  assert.match(rejected.stderr, message);
}
const initializer = docker(
  ["run", "--rm", "--network", "none", ...caps, image, "prepare-network"],
  false,
);
assert.equal(initializer.status, 2);
assert.match(initializer.stderr, /requires an isolated initializer/);
for (const [index, protocol] of ["tcp", "udp"].entries()) {
  const server = `${prefix}-${protocol}-server`,
    client = `${prefix}-${protocol}-client`;
  const service = `${prefix}-${protocol}-service`,
    transport = `${prefix}-${protocol}-transport`;
  const backend = `${prefix}-${protocol}-backend`,
    volume = `${prefix}-${protocol}-data`;
  const keeper = `${prefix}-${protocol}-network`;
  const port = 53195 + index;
  try {
    docker(["volume", "create", volume]);
    if (index === 0) {
      const migration = [
        "run",
        "--rm",
        "--network",
        "none",
        "--user",
        "0:0",
        "--cap-drop",
        "ALL",
        "--cap-add",
        "CHOWN",
        "--security-opt",
        "no-new-privileges",
        "--mount",
        `type=volume,src=${volume},dst=/etc/openvpn`,
        "--entrypoint",
        "sh",
        image,
      ];
      docker([
        ...migration,
        "-ec",
        "chown 0:0 /etc/openvpn; printf synthetic-migration-marker > /etc/openvpn/migration-marker",
      ]);
      const rejected = docker(
        [
          "run",
          "--rm",
          "--network",
          "none",
          ...caps,
          "--mount",
          `type=volume,src=${volume},dst=/etc/openvpn`,
          image,
          "server",
        ],
        false,
      );
      assert.equal(rejected.status, 2);
      assert.match(rejected.stderr, /data directory is not owned by this user/);
      assert.equal(
        docker([
          ...migration,
          "-ec",
          "test ! -e /etc/openvpn/easy-rsa; cat /etc/openvpn/migration-marker",
        ]).stdout,
        "synthetic-migration-marker",
      );
      // Only this new test volume is migrated, never an existing operator volume.
      docker([
        ...migration,
        "-ec",
        "chown 10001:10001 /etc/openvpn /etc/openvpn/migration-marker",
      ]);
    }
    // A normal private bridge, as in Compose, enables a loopback-only host
    // publication. No external destinations are contacted by this test.
    docker(["network", "create", transport]);
    docker(["network", "create", "--internal", backend]);
    const subnet = JSON.parse(docker(["network", "inspect", backend]).stdout)[0]
      .IPAM.Config[0].Subnet;
    const [address, bits] = subnet.split("/");
    const netmask = [0, 1, 2, 3]
      .map((i) => ((0xffffffff << (32 - Number(bits))) >>> ((3 - i) * 8)) & 255)
      .join(".");
    docker([
      "run",
      "--detach",
      "--name",
      service,
      "--network",
      backend,
      "--read-only",
      "--cap-drop",
      "ALL",
      "--security-opt",
      "no-new-privileges",
      "--entrypoint",
      "node",
      serviceImage,
      "-e",
      'require("node:http").createServer((q,s)=>s.end("synthetic-vpn-success")).listen(18080,"0.0.0.0")',
    ]);
    const destination =
      inspect(service).NetworkSettings.Networks[backend].IPAddress;
    docker([
      "create",
      "--name",
      keeper,
      "--network",
      transport,
      "--network-alias",
      "vpn-server",
      ...caps,
      "--user",
      "0:0",
      "--cap-add",
      "NET_ADMIN",
      "--cap-add",
      "SETUID",
      "--cap-add",
      "SETGID",
      "--cap-add",
      "SETPCAP",
      "--publish",
      `127.0.0.1::${port}/${protocol}`,
      "--sysctl",
      "net.ipv4.ip_forward=1",
      "--read-only",
      "--tmpfs",
      "/run:mode=755",
      "--tmpfs",
      "/etc/openvpn:mode=000",
      "--env",
      "OVPN_NETWORK=10.203.0.0",
      "--env",
      "OVPN_CIDR=10.203.0.0/24",
      "--entrypoint",
      "/usr/local/bin/openvpn-entrypoint",
      image,
      "network",
    ]);
    docker(["network", "connect", backend, keeper]);
    docker(["start", keeper]);
    await waitFor(
      () =>
        /Uid:\s+10001/.test(
          docker(["exec", keeper, "cat", "/proc/1/status"], false).stdout,
        ),
      "namespace keeper drops root",
    );
    const keeperStatus = docker([
      "exec",
      keeper,
      "cat",
      "/proc/1/status",
    ]).stdout;
    for (const cap of ["CapEff", "CapPrm", "CapBnd", "CapAmb", "CapInh"])
      assert.match(keeperStatus, new RegExp(`^${cap}:\\s+0+$`, "m"));
    assert.match(keeperStatus, /^NoNewPrivs:\s+1$/m);
    assert.ok(!inspect(keeper).Mounts.some((m) => m.Name === volume));
    // Preparation is idempotent and has no server PKI volume or host namespace.
    docker([
      "run",
      "--rm",
      "--network",
      `container:${keeper}`,
      "--user",
      "0:0",
      ...caps,
      "--cap-add",
      "NET_ADMIN",
      "--env",
      "OVPN_NETWORK=10.203.0.0",
      "--env",
      "OVPN_CIDR=10.203.0.0/24",
      image,
      "prepare-network",
    ]);
    docker([
      "create",
      "--name",
      server,
      "--network",
      `container:${keeper}`,
      ...caps,
      "--mount",
      `type=volume,src=${volume},dst=/etc/openvpn`,
      "--env",
      `OVPN_PROTO=${protocol}`,
      "--env",
      `OVPN_PORT=${port}`,
      "--env",
      "OVPN_LISTEN_ADDRESS=0.0.0.0",
      "--env",
      "OVPN_REMOTE_HOST=vpn-server",
      "--env",
      `OVPN_REMOTE_PORT=${port}`,
      "--env",
      "OVPN_CLIENT_NAME=synthetic-client",
      "--env",
      `OVPN_PUSH_ROUTES=${address} ${netmask}`,
      // Each test's private server namespace owns this subnet, never the host.
      "--env",
      "OVPN_NETWORK=10.203.0.0",
      "--env",
      "OVPN_CIDR=10.203.0.0/24",
      image,
    ]);
    docker(["start", server]);
    await waitFor(() => {
      const logs = docker(["logs", server], false);
      return /Initialization Sequence Completed/.test(
        logs.stdout + logs.stderr,
      );
    }, `${protocol} server initialization`);
    const serverInfo = inspect(server);
    assert.equal(serverInfo.HostConfig.Privileged, false);
    assert.notEqual(serverInfo.HostConfig.NetworkMode, "host");
    assert.deepEqual(serverInfo.HostConfig.CapDrop, ["ALL"]);
    assert.equal(serverInfo.Config.User, "10001:10001");
    assert.equal((serverInfo.HostConfig.CapAdd ?? []).length, 0);
    const publication =
      inspect(keeper).NetworkSettings.Ports[`${port}/${protocol}`];
    assert.equal(publication.length, 1);
    assert.equal(publication[0].HostIp, "127.0.0.1");
    if (protocol === "tcp") await loopbackPort(Number(publication[0].HostPort));
    assert.notEqual(
      docker(["exec", server, "iptables", "-t", "nat", "-S"], false).status,
      0,
      "Server cannot administer NAT",
    );
    assert.equal(
      docker([
        "exec",
        server,
        "cat",
        "/proc/sys/net/ipv4/ip_forward",
      ]).stdout.trim(),
      "1",
    );
    docker(["exec", server, "sh", "-c", health]);
    assert.notEqual(
      docker(
        [
          "exec",
          "--env",
          "OVPN_PORT=53999",
          server,
          "/usr/local/bin/openvpn-entrypoint",
          "healthcheck",
        ],
        false,
      ).status,
      0,
    );
    vpnIdentity(server);
    docker([
      "run",
      "--detach",
      "--name",
      client,
      "--network",
      transport,
      ...caps,
      "--mount",
      `type=volume,src=${volume},dst=/profiles,readonly`,
      "--entrypoint",
      "sleep",
      image,
      "infinity",
    ]);
    // A synthetic client uses its own pre-created TUN too, with no PKI in the initializer.
    docker([
      "run",
      "--rm",
      "--network",
      `container:${client}`,
      "--user",
      "0:0",
      ...caps,
      "--cap-add",
      "NET_ADMIN",
      "--entrypoint",
      "sh",
      image,
      "-ec",
      'ip tuntap add dev tun0 mode tun user 10001 group 10001; ip addr add 10.203.0.2/24 dev tun0; ip link set tun0 up; ip route add "$1" via 10.203.0.1 dev tun0',
      "prepare",
      subnet,
    ]);
    assert.notEqual(
      httpFromClient(client, destination, 2).status,
      0,
      "Backend must be unreachable before VPN connects",
    );
    const profile = "/profiles/clients/synthetic-client.ovpn";
    // Do not print the generated profile or private keys into diagnostics.
    assert.match(
      docker(["exec", client, "sed", "-n", "/^remote /p", profile]).stdout,
      new RegExp(`remote vpn-server ${port}`),
    );
    assert.match(
      docker(["exec", client, "sed", "-n", "/^proto /p", profile]).stdout,
      new RegExp(protocol === "tcp" ? "proto tcp-client" : "proto udp"),
    );
    docker([
      "exec",
      client,
      "openvpn",
      "--config",
      profile,
      "--dev",
      "tun0",
      "--ifconfig-noexec",
      "--route-noexec",
      "--disable-dco",
      "--log",
      "/tmp/vpn-client.log",
      "--daemon",
    ]);
    await waitFor(
      () =>
        /Initialization Sequence Completed/.test(
          docker(["exec", client, "cat", "/tmp/vpn-client.log"], false).stdout,
        ),
      `${protocol} client TLS connection`,
    );
    vpnIdentity(client);
    const response = httpFromClient(client, destination);
    assert.equal(response.status, 0, response.stderr);
    assert.match(response.stdout, /synthetic-vpn-success/);
    const keyPath = "/etc/openvpn/easy-rsa/pki/private/server.key";
    const before = docker(["exec", server, "sha256sum", keyPath]).stdout;
    docker(["restart", "--time", "5", server]);
    assert.equal(
      docker(["exec", server, "sha256sum", keyPath]).stdout,
      before,
      "Restart must preserve the existing PKI",
    );
    docker(["exec", client, "sh", "-c", "kill $(pidof openvpn)"]);
    docker([
      "exec",
      client,
      "openvpn",
      "--config",
      profile,
      "--dev",
      "tun0",
      "--ifconfig-noexec",
      "--route-noexec",
      "--disable-dco",
      "--log",
      "/tmp/vpn-client-restart.log",
      "--daemon",
    ]);
    await waitFor(
      () =>
        /Initialization Sequence Completed/.test(
          docker(["exec", client, "cat", "/tmp/vpn-client-restart.log"], false)
            .stdout,
        ),
      `${protocol} client reconnect`,
    );
    assert.match(
      httpFromClient(client, destination).stdout,
      /synthetic-vpn-success/,
    );
    docker(["exec", server, "sh", "-c", health]);
    vpnIdentity(server);
    console.log(
      `PASS OpenVPN ${protocol}: TLS and tunneled HTTP, stable private TUN/NAT across server restart, PKI/reconnection preserved, server/client and namespace keeper non-root with zero capabilities`,
    );
  } catch (error) {
    const logs = docker(["logs", server], false);
    console.error(logs.stdout + logs.stderr);
    console.error(
      docker(["exec", client, "cat", "/tmp/vpn-client.log"], false).stdout,
    );
    throw error;
  } finally {
    // Exact ephemeral objects created by this process; no shared resources.
    for (const name of [client, server, keeper, service])
      docker(["rm", "--force", "--volumes", name], false);
    docker(["volume", "rm", volume], false);
    for (const name of [transport, backend])
      docker(["network", "rm", name], false);
  }
}
