import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { Manager } from "../dist/manager.js";
import { parseConfig, HubError } from "../dist/model.js";
import { loadConfig } from "../dist/config.js";
import { privateTunnel } from "../dist/auth.js";
import { squidConfig } from "../dist/squid.js";
import { StateStore } from "../dist/state.js";

async function fixture(t, config = {}) {
  const dir = await mkdtemp(path.join(os.tmpdir(), "hub-port-test-"));
  const m = new Manager(parseConfig(config), dir, `${dir}/run`);
  await m.store.load();
  m.configureSquid = async () => {};
  m.log = () => {};
  t.after(async () => {
    await m.close();
    await rm(dir, { recursive: true, force: true });
  });
  const s = m.store.add("user-a", "github", "devhub-user-a");
  s.tunnel_id = "devhub-user-a.use1";
  return { m, s, dir };
}
function remote(s, port = 3140) {
  const tunnel = {
    tunnelId: s.tunnel_id.split(".")[0],
    clusterId: "use1",
    ports: port ? [{ portNumber: port }] : [],
    accessControl: { entries: [] },
  };
  const commands = [];
  const runtime = {
    details: async () => structuredClone(tunnel),
    cli: async (args) => {
      commands.push(args);
      if (args[1] === "delete") tunnel.ports = [];
      if (args[1] === "create")
        tunnel.ports = [
          { portNumber: Number(args[args.indexOf("--port-number") + 1]) },
        ];
      return "{}";
    },
  };
  return { tunnel, commands, runtime };
}
test("proxy port defaults, environment override and collision validation", async (t) => {
  const { dir } = await fixture(t);
  const file = path.join(dir, "config.json");
  await writeFile(file, JSON.stringify({ proxyPort: 3210 }));
  assert.equal(parseConfig({}).proxyPort, 3140);
  assert.equal((await loadConfig(file, {})).proxyPort, 3210);
  assert.equal(
    (await loadConfig(file, { HUB_PROXY_PORT: "3220" })).proxyPort,
    3220,
  );
  for (const value of [
    "",
    "abc",
    "1e3",
    "-1",
    "0",
    "22",
    "1023",
    "65536",
    "3210.5",
    "3210;bad",
  ])
    await assert.rejects(
      loadConfig(file, { HUB_PROXY_PORT: value }),
      /INVALID_/,
    );
  for (const proxyPort of [0, 22, 1023, 65536, 3210.5, "3210", null])
    assert.throws(() => parseConfig({ proxyPort }), /INVALID_PROXY_PORT/);
  assert.throws(() => parseConfig({ proxyPort: 18001 }), /RESERVED_PORT/);
  assert.throws(() => parseConfig({ proxyPort: 8080 }), /INVALID_HEALTH_PORT/);
  assert.equal(parseConfig({ proxyPort: 65535 }).proxyPort, 65535);
});
test("private tunnel validation uses the recorded port and rejects extra ports or ACLs", () => {
  const s = { tunnel_id: "devhub-user-a.use1", proxy_port: 3210 };
  const r = remote(s, 3210);
  assert.equal(privateTunnel(r.tunnel, s).ports[0].portNumber, 3210);
  assert.throws(
    () => privateTunnel(r.tunnel, { ...s, proxy_port: 3140 }),
    /PORT_POLICY_CHANGED/,
  );
  r.tunnel.ports.push({ portNumber: 3140 });
  assert.throws(() => privateTunnel(r.tunnel, s), /PORT_POLICY_CHANGED/);
  r.tunnel.ports = [{ portNumber: 3210, accessControl: { entries: [{}] } }];
  assert.throws(() => privateTunnel(r.tunnel, s), /PORT_NOT_OWNER_ONLY/);
});
test("custom proxy port configures only the deny-only idle Squid listener", () => {
  const c = parseConfig({ proxyPort: 3210 });
  const idle = squidConfig(c, [], "/run/hub");
  assert.match(idle, /http_port 127\.0\.0\.1:3210 name=unassigned/);
  assert.ok(!idle.includes(":3140"));
  const active = squidConfig(
    c,
    [{ id: "user-a", listener: 18001, identity: {}, status: "ready" }],
    "/run/hub",
  );
  assert.match(active, /http_port 127\.0\.0\.1:18001/);
  assert.ok(!active.includes(":3210"));
});
test("existing private port migrates on start, preserves ID/listener, and is persisted", async (t) => {
  const { m, s, dir } = await fixture(t, { proxyPort: 3210 });
  const r = remote(s);
  await m.provision(s, r.runtime);
  assert.deepEqual(r.commands, [
    ["port", "delete", s.tunnel_id, "--port-number", "3140", "--json"],
    [
      "port",
      "create",
      s.tunnel_id,
      "--port-number",
      "3210",
      "--protocol",
      "auto",
      "--json",
    ],
  ]);
  assert.equal(s.proxy_port, 3210);
  assert.equal(s.listener, 18001);
  assert.equal(s.tunnel_id, "devhub-user-a.use1");
  const restored = new StateStore(dir, m.config);
  await restored.load();
  assert.equal(restored.get("user-a").proxy_port, 3210);
  r.commands.length = 0;
  await m.provision(s, r.runtime);
  assert.deepEqual(r.commands, []);
  m.config.proxyPort = 3220;
  await m.provision(s, r.runtime);
  assert.equal(s.proxy_port, 3220);
  assert.equal(r.commands[0][4], "3210");
});
test("unknown ports, extra ports and shared ACLs are never deleted during migration", async (t) => {
  const { m, s } = await fixture(t, { proxyPort: 3210 });
  for (const mutate of [
    (r) => {
      r.tunnel.ports = [{ portNumber: 3330 }];
    },
    (r) => {
      r.tunnel.ports.push({ portNumber: 22 });
    },
    (r) => {
      r.tunnel.accessControl.entries = [{}];
    },
    (r) => {
      r.tunnel.ports[0].accessControl = { entries: [{}] };
    },
  ]) {
    const r = remote(s);
    mutate(r);
    await assert.rejects(m.provision(s, r.runtime));
    assert.deepEqual(r.commands, []);
    assert.equal(s.proxy_port, undefined);
  }
});
test("interrupted migration recovers from empty or already-updated remote port", async (t) => {
  const { m, s } = await fixture(t, { proxyPort: 3210 });
  const r = remote(s);
  const original = r.runtime.cli;
  r.runtime.cli = async (args) => {
    if (args[1] === "create") throw new HubError("COMMAND_FAILED");
    return original(args);
  };
  await assert.rejects(m.provision(s, r.runtime), /COMMAND_FAILED/);
  assert.equal(s.proxy_port, undefined);
  assert.deepEqual(r.tunnel.ports, []);
  r.runtime.cli = original;
  await m.provision(s, r.runtime);
  assert.equal(s.proxy_port, 3210);
  delete s.proxy_port;
  r.commands.length = 0;
  await m.provision(s, r.runtime);
  assert.deepEqual(r.commands, []);
  assert.equal(s.proxy_port, 3210);
});
test("migration requires read-back confirmation before advancing local port", async (t) => {
  const { m, s } = await fixture(t, { proxyPort: 3210 });
  const r = remote(s, 0);
  r.runtime.cli = async (args) => {
    r.commands.push(args);
    return "{}";
  };
  await assert.rejects(m.provision(s, r.runtime), /PORT_POLICY_CHANGED/);
  assert.equal(s.proxy_port, undefined);
});
test("port policy changes enforce stopped sessions, web collisions and rollback", async (t) => {
  const { m, s } = await fixture(t);
  m.webPort = 8082;
  s.desired = true;
  await assert.rejects(
    m.applyPolicy({ proxyPort: 3210 }, async () => {}),
    /STOP_SESSIONS_FIRST/,
  );
  s.desired = false;
  await assert.rejects(
    m.applyPolicy({ proxyPort: 8082 }, async () => {}),
    /WEB_PORT_COLLISION/,
  );
  await assert.rejects(
    m.applyPolicy({ proxyPort: 3210 }, async () => {
      throw new Error("persist failed");
    }),
    /persist failed/,
  );
  assert.equal(m.config.proxyPort, 3140);
  await m.applyPolicy({ proxyPort: 3210 }, async () => {});
  assert.equal(m.config.proxyPort, 3210);
});
test("legacy session state still loads and malformed persisted ports fail closed", async (t) => {
  const { m, s, dir } = await fixture(t);
  await m.store.save();
  await new StateStore(dir, m.config).load();
  for (const value of [0, 22, 18001, 65536, "3210"]) {
    s.proxy_port = value;
    await m.store.save();
    await assert.rejects(
      new StateStore(dir, m.config).load(),
      /INVALID_STATE_PROXY_PORT/,
    );
  }
});
