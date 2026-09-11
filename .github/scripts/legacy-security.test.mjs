import test from "node:test";
import assert from "node:assert/strict";
import {
  mkdtemp,
  mkdir,
  writeFile,
  readFile,
  stat,
  rm,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { parse } from "../../images/hub/node_modules/yaml/dist/index.js";

const root = path.resolve(import.meta.dirname, "../..");
const run = (script, args = [], extra = {}) =>
  spawnSync("bash", [path.join(root, script), ...args], {
    encoding: "utf8",
    env: { PATH: "/usr/bin:/bin", ...extra },
  });

test("OpenVPN refuses to destroy an incomplete PKI", async () => {
  const dir = await mkdtemp(path.join(tmpdir(), "toolkit-pki-"));
  try {
    await mkdir(path.join(dir, "easy-rsa"));
    const sentinel = path.join(dir, "easy-rsa", "recovery-marker");
    await writeFile(sentinel, "keep");
    const result = run(
      "images/openvpn/openvpn-entrypoint",
      ["client", "test-client"],
      { OVPN_DIR: dir },
    );
    assert.equal(result.status, 2);
    assert.match(result.stderr, /incomplete PKI exists/);
    assert.equal(await readFile(sentinel, "utf8"), "keep");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("OpenVPN rejects path traversal and writes exported client keys privately", async () => {
  const dir = await mkdtemp(path.join(tmpdir(), "toolkit-client-"));
  try {
    for (const name of ["../escape", "bad/name", "bad\nname", "-option"]) {
      const result = run(
        "images/openvpn/openvpn-entrypoint",
        ["client", name],
        { OVPN_DIR: dir },
      );
      assert.equal(result.status, 2);
      assert.match(result.stderr, /client name/);
    }
    for (const folder of ["issued", "private"])
      await mkdir(path.join(dir, "easy-rsa/pki", folder), { recursive: true });
    for (const name of [
      "ca.crt",
      "ta.key",
      "issued/server.crt",
      "private/server.key",
      "issued/test-client.crt",
      "private/test-client.key",
    ])
      await writeFile(
        path.join(dir, "easy-rsa/pki", name),
        "synthetic fixture, not a credential",
      );
    const result = run(
      "images/openvpn/openvpn-entrypoint",
      ["client", "test-client"],
      { OVPN_DIR: dir },
    );
    assert.equal(result.status, 0, result.stderr);
    assert.equal(
      (await stat(path.join(dir, "clients/test-client.ovpn"))).mode & 0o777,
      0o600,
    );
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("Squid rejects multiline or sed-substitution injection in generated settings", () => {
  for (const env of [
    { SQUID_VISIBLE_HOSTNAME: "proxy\nhttp_access allow all" },
    { SQUID_LISTEN_ADDRESS: "127.0.0.1|e touch /tmp/not-allowed" },
    { SQUID_SSL_PORTS: "443\nhttp_access allow all" },
    { SQUID_SAFE_PORTS: "80;443" },
  ]) {
    const result = run("images/squid/squid-entrypoint", ["squid"], env);
    assert.equal(result.status, 2);
  }
});

test("Kubernetes example disables API credentials and bounds disk and memory-backed storage separately", async () => {
  const manifest = await readFile(
    path.join(root, "images/hub/examples/kubernetes/hub.yaml"),
    "utf8",
  );
  assert.match(manifest, /automountServiceAccountToken: false/);
  assert.match(
    manifest,
    /requests: \{cpu: 250m, memory: 512Mi, ephemeral-storage: 128Mi\}/,
  );
  assert.match(
    manifest,
    /limits: \{cpu: "1", memory: 1Gi, ephemeral-storage: 512Mi\}/,
  );
  assert.match(manifest, /emptyDir: \{medium: Memory, sizeLimit: 256Mi\}/);
  assert.match(manifest, /emptyDir: \{medium: Memory, sizeLimit: 128Mi\}/);
});

test("OpenVPN Compose confines network administration and drops unnecessary privileges", async () => {
  const compose = await readFile(path.join(root, "compose.yml"), "utf8");
  const services = parse(compose).services;
  const vpn = services.openvpn,
    network = services["openvpn-network"];
  assert.equal(vpn.network_mode, "service:openvpn-network");
  assert.equal(vpn.depends_on["openvpn-network"].condition, "service_healthy");
  assert.equal(vpn.depends_on["openvpn-network"].restart, true);
  assert.deepEqual(vpn.cap_drop, ["ALL"]);
  assert.equal(vpn.cap_add, undefined);
  assert.equal(vpn.privileged, undefined);
  assert.ok(vpn.security_opt.includes("no-new-privileges:true"));
  assert.equal(network.network_mode, undefined);
  assert.equal(network.privileged, undefined);
  assert.equal(network.user, "0:0");
  assert.deepEqual(network.cap_drop, ["ALL"]);
  assert.deepEqual(network.cap_add, [
    "NET_ADMIN",
    "SETUID",
    "SETGID",
    "SETPCAP",
  ]);
  assert.deepEqual(network.command, ["network"]);
  assert.deepEqual(network.entrypoint, ["/usr/local/bin/openvpn-entrypoint"]);
  assert.equal(
    network.volumes,
    undefined,
    "Network initializer must not access the PKI",
  );
  assert.equal(network.read_only, true);
  assert.ok(network.tmpfs.includes("/etc/openvpn:mode=000"));
  assert.equal(network.ports[0].host_ip, "${OVPN_LISTEN_ADDRESS:-127.0.0.1}");
  assert.equal(network.ports[0].protocol, "${OVPN_PROTO:-tcp}");
  assert.equal(network.sysctls["net.ipv4.ip_forward"], "1");
  const entrypoint = await readFile(
    path.join(root, "images/openvpn/openvpn-entrypoint"),
    "utf8",
  );
  assert.doesNotMatch(entrypoint, /sysctl -w/);
  assert.match(entrypoint, /IPv4 forwarding is disabled/);
  assert.match(
    entrypoint,
    /--bounding-set=-all --inh-caps=-all --ambient-caps=-all/,
  );
  assert.match(entrypoint, /--no-new-privs sleep infinity/);
  assert.match(entrypoint, /ifconfig-noexec/);
  assert.match(entrypoint, /route-noexec/);
  assert.doesNotMatch(entrypoint, /user nobody|group nogroup/);
  const dockerfile = await readFile(
    path.join(root, "images/openvpn/Dockerfile"),
    "utf8",
  );
  assert.match(dockerfile, /^USER 10001:10001$/m);
});

test("OpenVPN validates subnet alignment and bounded initialization before network mutation", async () => {
  const script = (
    await readFile(path.join(root, "images/openvpn/openvpn-entrypoint"), "utf8")
  ).split('\ncase "${1:-server}" in')[0];
  for (const [cidr, network, mask, accepted] of [
    ["10.8.0.0/24", "10.8.0.0", "255.255.255.0", true],
    ["10.8.0.0/16", "10.8.0.0", "255.255.0.0", true],
    ["10.8.0.1/24", "10.8.0.1", "255.255.255.0", false],
    ["10.8.0.0/24", "10.9.0.0", "255.255.255.0", false],
    ["10.8.0.0/24", "10.8.0.0", "255.255.0.0", false],
    ["10.999.0.0/24", "10.999.0.0", "255.255.255.0", false],
    ["10.8.0.0/0", "10.8.0.0", "0.0.0.0", false],
    ["10.8.0.0/32", "10.8.0.0", "255.255.255.255", false],
  ]) {
    const result = spawnSync("bash", [], {
      input: `${script}\nnetwork_parameters\nprintf '%s' "$OVPN_SERVER_ADDRESS/$OVPN_PREFIX"`,
      encoding: "utf8",
      env: {
        PATH: "/usr/bin:/bin",
        OVPN_CIDR: cidr,
        OVPN_NETWORK: network,
        OVPN_NETMASK: mask,
      },
    });
    assert.equal(result.status === 0, accepted, `${cidr}: ${result.stderr}`);
    if (accepted) assert.equal(result.stdout, `10.8.0.1/${cidr.split("/")[1]}`);
  }
  const result = spawnSync("bash", [], {
    input: `${script}\nwait_for_network`,
    encoding: "utf8",
    env: { PATH: "/usr/bin:/bin", OVPN_NETWORK_WAIT_SECONDS: "-1" },
  });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /invalid network wait/);
});

test("Images select smaller packaged providers without removing authentication services or package inventory", async () => {
  for (const file of ["images/hub/Dockerfile", "images/squid/Dockerfile"]) {
    const dockerfile = await readFile(path.join(root, file), "utf8");
    assert.match(dockerfile, /systemd-standalone-sysusers/);
    assert.doesNotMatch(dockerfile, /--force-depends|rm[^\n]*\/var\/lib\/dpkg/);
    if (dockerfile.includes("--allow-remove-essential")) {
      assert.match(
        dockerfile,
        /validate-plan[\s\S]*apt-get install -y --no-install-recommends --allow-remove-essential[\s\S]*verify-installed/,
      );
    }
    if (file.includes("hub")) {
      assert.match(dockerfile, /dbus-x11 gnome-keyring/);
      assert.match(dockerfile, /libsecret-1-0 libsecret-tools/);
    }
  }
});
