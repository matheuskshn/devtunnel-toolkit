import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
const base = "http://localhost:8082",
  password = "synthetic-console-password-01",
  changed = "synthetic-console-password-02";
let cookie = "",
  csrf = "";
async function request(route, data) {
  const r = await fetch(base + route, {
    method: data === undefined ? "GET" : "POST",
    headers: {
      Cookie: cookie,
      ...(data === undefined
        ? {}
        : {
            "Content-Type": "application/json",
            Origin: base,
            "X-CSRF-Token": csrf,
          }),
    },
    body: data === undefined ? undefined : JSON.stringify(data),
  });
  for (const c of r.headers.getSetCookie())
    if (c.startsWith("hub-local=")) cookie = c.split(";")[0];
  const result = await r.json();
  assert.equal(r.ok, true, JSON.stringify(result));
  return result;
}
if (process.argv[2] === "initial") {
  const reset = spawnSync(
    "hub",
    ["admin", "reset-password", "admin", "--password-stdin"],
    { input: password + "\n", encoding: "utf8" },
  );
  assert.equal(reset.status, 0, reset.stderr);
  assert.ok(!reset.stdout.includes(password));
  await request("/api/auth/login", { username: "admin", password });
  csrf = (await request("/api/me")).csrf;
  await request("/api/me/password", { current: password, password: changed });
  csrf = (await request("/api/me")).csrf;
  let revision = (await request("/api/providers")).revision;
  await request("/api/providers", {
    id: "github-fixture",
    kind: "github",
    label: "GitHub test",
    enabled: false,
    registration: true,
    clientId: "synthetic-client",
    clientSecret: "synthetic-provider-secret",
    revision,
  });
  revision = (await request("/api/users")).revision;
  await request("/api/users", {
    username: "viewer-test",
    name: "Test viewer",
    role: "viewer",
    disabled: false,
    password,
    sessions: [],
    revision,
  });
  revision = (await request("/api/config")).revision;
  await request("/api/config", {
    policy: {
      allowedDomains: ["service.example.com"],
      maxSessions: 25,
      proxyPort: 3210,
      socksEnabled: true,
      socksPort: 3280,
    },
    revision,
  });
  await request("/api/jobs", {
    action: "add",
    session: "console-session",
    provider: "github",
  });
  for (let i = 0; i < 30; i++) {
    const data = await request("/api/overview");
    if (data.jobs.every((j) => j.status !== "running")) break;
    await new Promise((r) => setTimeout(r, 100));
  }
} else {
  await request("/api/auth/login", { username: "admin", password: changed });
  const me = await request("/api/me");
  csrf = me.csrf;
  assert.equal(me.user.mustChange, false);
  assert.ok(
    (await request("/api/users")).users.some(
      (u) => u.username === "viewer-test",
    ),
  );
  const providers = await request("/api/providers");
  assert.equal(providers.providers[0].clientSecretConfigured, true);
  assert.ok(!JSON.stringify(providers).includes("synthetic-provider-secret"));
  assert.deepEqual((await request("/api/config")).config.allowedDomains, [
    "service.example.com",
  ]);
  assert.equal((await request("/api/config")).config.maxSessions, 25);
  assert.equal((await request("/api/config")).config.proxyPort, 3210);
  assert.equal((await request("/api/overview")).proxyPort, 3210);
  assert.equal((await request("/api/config")).config.socksPort, 3280);
  assert.equal((await request("/api/overview")).socksEnabled, true);
  assert.equal((await request("/api/overview")).socksPort, 3280);
  assert.ok((await request("/api/overview")).sessions.find(s => s.id === "console-session").socks_listener);
  assert.ok(
    (await request("/api/overview")).sessions.some(
      (s) => s.id === "console-session",
    ),
  );
}
console.log(
  `PASS web ${process.argv[2]}: CLI recovery, local auth, encrypted provider/users/policy and structured actions`,
);
