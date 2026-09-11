import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { createServer } from "node:net";
import { get, request as httpRequest } from "node:http";
import { StateStore } from "../dist/state.js";
import { parseConfig } from "../dist/model.js";
import {
  ControlStore,
  hashPassword,
  verifyPassword,
  validateProvider,
  publicProvider,
} from "../dist/web/control.js";
import { WebConsole } from "../dist/web/server.js";
import { webOptions, Sessions, RateLimit } from "../dist/web/security.js";
import { beginLogin, finishLogin, ldapLogin } from "../dist/web/providers.js";

const password = `synthetic-${randomBytes(18).toString("hex")}`,
  newPassword = `synthetic-${randomBytes(18).toString("hex")}`;
const key = () => randomBytes(32).toString("base64");
async function fixture(t) {
  const directory = await mkdtemp(path.join(tmpdir(), "hub-web-test-"));
  const config = parseConfig({}),
    store = new StateStore(directory, config);
  await store.load();
  const encryptionKey = key(),
    control = new ControlStore(store, encryptionKey);
  await control.load();
  t.after(() => rm(directory, { recursive: true, force: true }));
  return { directory, store, config, control, encryptionKey };
}
test("password hashing, encrypted control state, restart, wrong key and recovery admin", async (t) => {
  const f = await fixture(t);
  await f.control.resetPassword("admin", password);
  const hash = f.control.data.users[0].password;
  assert.notEqual(hash, password);
  assert.equal(await verifyPassword(password, hash), true);
  assert.equal(await verifyPassword("bad", hash), false);
  assert.equal(await verifyPassword(password), false);
  await assert.rejects(hashPassword("short"), { code: "PASSWORD_TOO_WEAK" });
  const state = await readFile(path.join(f.directory, "state.json"), "utf8");
  assert.ok(!state.includes(password));
  assert.ok(!state.includes(hash));
  const restored = new ControlStore(f.store, f.encryptionKey);
  await restored.load();
  assert.equal(restored.data.users[0].password, hash);
  await assert.rejects(new ControlStore(f.store, key()).load());
  await assert.rejects(
    restored.update((d) => {
      d.users[0].disabled = true;
    }),
    { code: "RECOVERY_ADMIN_PROTECTED" },
  );
  await assert.rejects(
    restored.update(() => {}, 0),
    { code: "CONFIG_CONFLICT" },
  );
});
test("storage failure fences control plane and future authentication", async (t) => {
  const f = await fixture(t);
  let fenced = false;
  const control = new ControlStore(f.store, f.encryptionKey, () => {
    fenced = true;
  });
  await control.load();
  f.store.save = async () => {
    throw new Error("synthetic failure");
  };
  await assert.rejects(control.update(() => {}));
  assert.equal(fenced, true);
  assert.throws(() => control.user("admin"), {
    code: "CONSOLE_STORAGE_FAILED",
  });
  await assert.rejects(control.update(() => {}));
});
test("external enrolment is pending, stable-subject only, never automatic email linking", async (t) => {
  const { control } = await fixture(t);
  const provider = {
    id: "github-test",
    kind: "github",
    label: "GitHub",
    enabled: true,
    registration: true,
    epoch: 1,
  };
  await control.update((d) => {
    d.providers.push(provider);
  });
  await assert.rejects(control.external(provider, "subject-one", "admin"), {
    code: "ACCOUNT_PENDING_APPROVAL",
  });
  const pending = control.data.users.find((u) => !u.local);
  assert.equal(pending.role, "viewer");
  assert.equal(pending.disabled, true);
  assert.equal(pending.identities[0].subject, "subject-one");
  assert.notEqual(pending.id, "admin");
  await control.update((d) => {
    d.users.find((u) => u.id === pending.id).disabled = false;
  });
  assert.equal(
    (await control.external(provider, "subject-one", "renamed")).id,
    pending.id,
  );
  await assert.rejects(
    control.external(
      { ...provider, registration: false },
      "subject-two",
      "admin",
    ),
    { code: "ACCOUNT_NOT_ENROLLED" },
  );
});
test("session epochs, provider revocation, secure cookies, expiry and rate limits", async (t) => {
  const { control } = await fixture(t),
    sessions = new Sessions(control, true),
    headers = {};
  const res = {
    getHeader: (k) => headers[k],
    setHeader: (k, v) => (headers[k] = v),
  };
  sessions.create(res, control.user("admin"));
  const set = headers["Set-Cookie"][0];
  assert.match(set, /__Host-hub=/);
  assert.match(set, /HttpOnly/);
  assert.match(set, /SameSite=Lax/);
  assert.match(set, /Secure/);
  const req = { headers: { cookie: set.split(";")[0] } };
  assert.equal(sessions.get(req).user.id, "admin");
  sessions.get(req).session.created = Date.now() - 28800001;
  assert.throws(() => sessions.get(req), { code: "LOGIN_REQUIRED" });
  sessions.create(res, control.user("admin"));
  req.headers.cookie = headers["Set-Cookie"].at(-1).split(";")[0];
  await control.resetPassword("admin", password);
  assert.throws(() => sessions.get(req), { code: "LOGIN_REQUIRED" });
  await control.update((d) =>
    d.providers.push({ id: "test-provider", epoch: 1, enabled: true }),
  );
  sessions.create(res, control.user("admin"), "test-provider");
  req.headers.cookie = headers["Set-Cookie"].at(-1).split(";")[0];
  await control.update((d) => {
    d.providers[0].enabled = false;
  });
  assert.throws(() => sessions.get(req), { code: "LOGIN_REQUIRED" });
  const limiter = new RateLimit();
  limiter.take("a", 1);
  assert.throws(() => limiter.take("a", 1), { code: "RATE_LIMITED" });
  assert.equal(webOptions({}), undefined);
  assert.throws(
    () =>
      webOptions({
        HUB_WEB_ENABLED: "true",
        HUB_WEB_ORIGIN: "http://hub.example.com",
        HUB_WEB_KEY: key(),
      }),
    { code: "INVALID_WEB_ORIGIN" },
  );
  assert.throws(
    () =>
      webOptions({
        HUB_WEB_ENABLED: "true",
        HUB_WEB_ORIGIN: "https://hub.example.com",
      }),
    { code: "WEB_KEY_REQUIRED" },
  );
});
test("provider validation enforces tenant-specific issuer, LDAPS and write-only secrets", () => {
  const oauth = {
    id: "test-provider",
    kind: "oidc",
    label: "OIDC",
    enabled: true,
    registration: false,
    epoch: 1,
    issuer: "https://identity.example.com",
    clientId: "synthetic-client",
    clientSecret: "synthetic-secret",
  };
  validateProvider(oauth);
  assert.ok(
    !JSON.stringify(publicProvider(oauth)).includes("synthetic-secret"),
  );
  assert.throws(
    () => validateProvider({ ...oauth, issuer: "http://identity.example.com" }),
    { code: "INVALID_PROVIDER_URL" },
  );
  assert.throws(
    () =>
      validateProvider({
        ...oauth,
        kind: "microsoft",
        issuer: "https://login.microsoftonline.com/common/v2.0",
      }),
    { code: "MICROSOFT_TENANT_ISSUER_REQUIRED" },
  );
  validateProvider({
    ...oauth,
    kind: "microsoft",
    issuer:
      "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0",
  });
  assert.throws(
    () =>
      validateProvider({
        ...oauth,
        kind: "ldap",
        url: "ldap://directory.example.com",
      }),
    { code: "INVALID_PROVIDER_URL" },
  );
  assert.throws(() => validateProvider({ ...oauth, clientId: 42 }), {
    code: "INVALID_AUTH_PROVIDER",
  });
});
test("GitHub authorization uses state and PKCE, rejects wrong/expired/replaced flows without exchange", async () => {
  const p = {
    id: "github-test",
    kind: "github",
    clientId: "synthetic-client",
    clientSecret: "synthetic-secret",
    enabled: true,
    epoch: 1,
  };
  const { url, flow } = await beginLogin(
      p,
      "https://hub.example.com/auth/callback",
    ),
    u = new URL(url);
  assert.equal(u.hostname, "github.com");
  assert.equal(u.searchParams.get("code_challenge_method"), "S256");
  assert.equal(u.searchParams.get("state"), flow.state);
  assert.ok(!url.includes(p.clientSecret));
  assert.ok(!url.includes(flow.verifier));
  await assert.rejects(
    finishLogin(
      p,
      flow,
      new URL("https://hub.example.com/auth/callback?state=wrong"),
    ),
    { code: "LOGIN_FAILED" },
  );
  await assert.rejects(
    finishLogin(
      { ...p, epoch: 2 },
      flow,
      new URL("https://hub.example.com/auth/callback"),
    ),
    { code: "LOGIN_EXPIRED" },
  );
  await assert.rejects(
    finishLogin(
      p,
      { ...flow, expires: 0 },
      new URL("https://hub.example.com/auth/callback"),
    ),
    { code: "LOGIN_EXPIRED" },
  );
});
test("LDAP binds separately, escapes filter, requires exact unique entry and TLS certificate verification", async () => {
  const p = {
    id: "ldap-test",
    kind: "ldap",
    enabled: true,
    url: "ldaps://directory.example.com",
    baseDN: "dc=example,dc=com",
    bindDN: "cn=reader,dc=example,dc=com",
    bindPassword: "synthetic-bind",
    loginAttribute: "uid",
    idAttribute: "entryUUID",
  };
  const calls = [];
  let count = 0;
  const factory = (options) => {
    const id = ++count;
    assert.equal(options.tlsOptions.rejectUnauthorized, true);
    assert.equal(options.tlsOptions.minVersion, "TLSv1.2");
    return {
      bind: async (dn, secret) => {
        calls.push({ id, dn, secret });
      },
      search: async (base, options) => {
        assert.ok(!options.filter.includes("*)(uid=*"));
        return {
          searchReferences: [],
          searchEntries: [
            { dn: "uid=user,dc=example,dc=com", entryUUID: "stable-one" },
          ],
        };
      },
      unbind: async () => {
        calls.push({ unbind: id });
      },
    };
  };
  assert.deepEqual(await ldapLogin(p, "*)(uid=*", password, factory), {
    subject: "stable-one",
    name: "*)(uid=*",
  });
  assert.equal(count, 2);
  assert.equal(calls[0].secret, "synthetic-bind");
  assert.equal(calls[1].secret, password);
  assert.equal(calls.filter((c) => c.unbind).length, 2);
  await assert.rejects(ldapLogin(p, "user", "", factory), {
    code: "LOGIN_FAILED",
  });
  await assert.rejects(
    ldapLogin(
      { ...p, url: "ldap://directory.example.com" },
      "user",
      password,
      factory,
    ),
    { code: "LDAP_TLS_REQUIRED" },
  );
  await assert.rejects(
    ldapLogin(p, "user", password, () => ({
      bind: async () => {},
      unbind: async () => {},
      search: async () => ({ searchReferences: [], searchEntries: [] }),
    })),
    { code: "LOGIN_FAILED" },
  );
});

async function serverFixture(t, requirePasswordChange = true) {
  const f = await fixture(t);
  await f.control.resetPassword("admin", password);
  f.store.add("session-one", "github");
  f.store.add("session-two", "microsoft");
  const socket = createServer();
  await new Promise((r) => socket.listen(0, "127.0.0.1", r));
  const port = socket.address().port;
  await new Promise((r) => socket.close(r));
  const pending = [],
    dispatches = [];
  const host = {
    config: f.config,
    sessions: () => f.store.state.sessions,
    ready: () => true,
    track: (p) => p,
    policy: async (value, persist) => {
      Object.assign(f.config, parseConfig({ ...f.config, ...value }));
      await persist();
    },
    dispatch: async (args, output) => {
      dispatches.push(args);
      if (args[1] === "login") {
        output(
          "To sign in, use https://github.com/login/device and enter the code ABCD-1234. synthetic-private-token",
        );
        await new Promise((r) => pending.push(r));
      }
      return {};
    },
  };
  const web = new WebConsole(host, f.control, {
    port,
    origin: `http://127.0.0.1:${port}`,
    secure: false,
    key: f.encryptionKey,
    requirePasswordChange,
  });
  await web.open();
  t.after(() => {
    pending.forEach((r) => r());
    web.close();
  });
  const client = () => ({
    cookie: "",
    csrf: "",
    async request(route, data, extra = {}) {
      const response = await fetch(`http://127.0.0.1:${port}${route}`, {
        method: data === undefined ? "GET" : "POST",
        headers: {
          Cookie: this.cookie,
          ...(data === undefined
            ? {}
            : {
                Origin: `http://127.0.0.1:${port}`,
                "Content-Type": "application/json",
                "X-CSRF-Token": this.csrf,
              }),
          ...extra,
        },
        body: data === undefined ? undefined : JSON.stringify(data),
        redirect: "manual",
      });
      for (const cookie of response.headers.getSetCookie())
        if (cookie.startsWith("hub-local=")) this.cookie = cookie.split(";")[0];
      const type = response.headers.get("Content-Type"),
        result = type?.includes("application/json")
          ? await response.json()
          : await response.text();
      return { status: response.status, headers: response.headers, result };
    },
    async login(username = "admin", pass = password) {
      const response = await this.request("/api/auth/login", {
        username,
        password: pass,
      });
      assert.equal(response.status, 200);
      const me = await this.request("/api/me");
      this.csrf = me.result.csrf;
      return me.result.user;
    },
  });
  return { ...f, web, client, dispatches, pending };
}

test("password-change environment policy is strict and defaults to required", () => {
  const env = {
    HUB_WEB_ENABLED: "true",
    HUB_WEB_ORIGIN: "https://hub.example.com",
    HUB_WEB_KEY: key(),
  };
  assert.equal(webOptions(env).requirePasswordChange, true);
  assert.equal(
    webOptions({ ...env, HUB_WEB_REQUIRE_PASSWORD_CHANGE: "true" })
      .requirePasswordChange,
    true,
  );
  assert.equal(
    webOptions({ ...env, HUB_WEB_REQUIRE_PASSWORD_CHANGE: "false" })
      .requirePasswordChange,
    false,
  );
  for (const value of ["", "TRUE", "1", "0", "yes", " false "])
    assert.throws(
      () => webOptions({ ...env, HUB_WEB_REQUIRE_PASSWORD_CHANGE: value }),
      { code: "INVALID_WEB_REQUIRE_PASSWORD_CHANGE" },
    );
});

test("optional password change allows access but retains pending status until changed", async (t) => {
  const f = await serverFixture(t, false),
    admin = f.client();
  assert.equal((await admin.login()).mustChange, true);
  const me = (await admin.request("/api/me")).result;
  assert.equal(me.requirePasswordChange, false);
  assert.equal(me.user.mustChange, true);
  assert.equal((await admin.request("/api/overview")).status, 200);
  assert.equal(
    (
      await admin.request("/api/jobs", {
        action: "stop",
        session: "session-one",
      })
    ).status,
    202,
  );
  const restored = new ControlStore(f.store, f.encryptionKey);
  await restored.load();
  assert.equal(restored.user("admin").mustChange, true);
  // Switching enforcement back on must not require resetting the account again.
  f.web.options.requirePasswordChange = true;
  assert.equal(
    (await admin.request("/api/overview")).result.error,
    "PASSWORD_CHANGE_REQUIRED",
  );
  assert.equal(
    (
      await admin.request("/api/me/password", {
        current: password,
        password: newPassword,
      })
    ).status,
    200,
  );
  admin.csrf = (await admin.request("/api/me")).result.csrf;
  assert.equal((await admin.request("/api/me")).result.user.mustChange, false);
  assert.equal((await admin.request("/api/overview")).status, 200);
  f.web.options.requirePasswordChange = false;
  await f.control.resetPassword("admin", password);
  assert.equal((await admin.request("/api/overview")).status, 401);
  assert.equal((await admin.login()).mustChange, true);
  assert.equal((await admin.request("/api/overview")).status, 200);
});
test("HTTP console: unauthenticated/CSRF/Host denial, mandatory change, secrets, roles, IDOR, jobs, revocation", async (t) => {
  const f = await serverFixture(t),
    admin = f.client(),
    outsider = f.client();
  assert.equal((await admin.request("/api/overview")).status, 401);
  const badHost = await new Promise((resolve) =>
    get(
      f.web.options.origin + "/api/auth/options",
      { headers: { Host: "evil.example.com" } },
      (res) => {
        let data = "";
        res.on("data", (c) => (data += c));
        res.on("end", () => resolve(JSON.parse(data)));
      },
    ),
  );
  assert.equal(badHost.error, "INVALID_HOST");
  const page = await admin.request("/");
  assert.equal(page.status, 200);
  assert.match(
    page.headers.get("Content-Security-Policy"),
    /frame-ancestors 'none'/,
  );
  assert.match(page.result, /app.js/);
  const theme = await admin.request("/theme.js");
  assert.equal(theme.status, 200);
  assert.match(theme.headers.get("Content-Type"), /text\/javascript/);
  assert.match(theme.result, /devtunnel-toolkit-theme/);
  assert.equal(
    (
      await admin.request(
        "/api/auth/login",
        { username: "admin", password },
        { Origin: "https://evil.example.com" },
      )
    ).status,
    403,
  );
  assert.equal((await admin.login()).mustChange, true);
  assert.equal(
    (await admin.request("/api/overview")).result.error,
    "PASSWORD_CHANGE_REQUIRED",
  );
  assert.equal(
    (
      await admin.request(
        "/api/me/password",
        { current: password, password: newPassword },
        { "X-CSRF-Token": "bad" },
      )
    ).status,
    403,
  );
  assert.equal(
    (
      await admin.request("/api/me/password", {
        current: password,
        password: newPassword,
      })
    ).status,
    200,
  );
  admin.csrf = (await admin.request("/api/me")).result.csrf;
  const createUser = async (username, role, sessions = []) =>
    admin.request("/api/users", {
      username,
      name: username,
      role,
      disabled: false,
      password,
      sessions,
      revision: f.control.data.revision,
    });
  assert.equal(
    (await createUser("operator", "operator", ["session-one"])).status,
    200,
  );
  assert.equal(
    (await createUser("viewer", "viewer", ["session-two"])).status,
    200,
  );
  const operator = f.client();
  await operator.login("operator");
  await operator.request("/api/me/password", {
    current: password,
    password: newPassword,
  });
  operator.csrf = (await operator.request("/api/me")).result.csrf;
  const viewer = f.client();
  await viewer.login("viewer");
  await viewer.request("/api/me/password", {
    current: password,
    password: newPassword,
  });
  viewer.csrf = (await viewer.request("/api/me")).result.csrf;
  assert.equal(
    (await operator.request("/api/overview")).result.sessions.length,
    1,
  );
  assert.equal((await operator.request("/api/users")).status, 403);
  assert.equal((await viewer.request("/api/providers")).status, 403);
  assert.equal(
    (
      await operator.request("/api/jobs", {
        action: "start",
        session: "session-two",
      })
    ).status,
    403,
  );
  assert.equal(
    (
      await viewer.request("/api/jobs", {
        action: "start",
        session: "session-two",
      })
    ).status,
    403,
  );
  assert.equal(
    (
      await operator.request("/api/jobs", {
        action: "add",
        session: "session-new",
        provider: "github",
      })
    ).status,
    403,
  );
  assert.equal(
    (
      await admin.request("/api/jobs", {
        action: "shell",
        session: "session-one",
      })
    ).result.error,
    "INVALID_ACTION",
  );
  assert.equal(
    (
      await admin.request("/api/jobs", {
        action: "stop",
        session: "session-one",
        command: "whoami",
      })
    ).result.error,
    "UNKNOWN_FIELD",
  );
  const job = await operator.request("/api/jobs", {
    action: "login",
    session: "session-one",
  });
  assert.equal(job.status, 202);
  const own = (await operator.request("/api/overview")).result.jobs[0];
  assert.equal(own.device.code, "ABCD-1234");
  assert.ok(!JSON.stringify(own).includes("synthetic-private-token"));
  assert.equal(
    (await admin.request("/api/overview")).result.jobs[0].device,
    undefined,
  );
  assert.equal((await viewer.request("/api/overview")).result.jobs.length, 0);
  f.web.record({
    event: "proxy_access",
    session_id: "session-two",
    destination: "service.example.com",
  });
  assert.ok(
    !(await operator.request("/api/logs")).result.records.some(
      (r) => r.destination,
    ),
  );
  assert.ok(
    (await viewer.request("/api/logs")).result.records.some(
      (r) => r.destination,
    ),
  );
  const provider = {
    id: "github-test",
    kind: "github",
    label: "Test",
    enabled: true,
    registration: true,
    clientId: "synthetic-client",
    clientSecret: "synthetic-client-secret",
    revision: f.control.data.revision,
  };
  assert.equal((await admin.request("/api/providers", provider)).status, 200);
  const listed = await admin.request("/api/providers");
  assert.ok(!JSON.stringify(listed.result).includes("synthetic-client-secret"));
  assert.equal(
    (
      await admin.request("/api/providers", {
        ...provider,
        clientId: "changed",
        revision: f.control.data.revision,
      })
    ).result.error,
    "PROVIDER_IDENTITY_IMMUTABLE",
  );
  assert.equal(
    (
      await admin.request("/api/config", {
        policy: { hubId: "other" },
        revision: f.control.data.revision,
      })
    ).result.error,
    "UNKNOWN_FIELD",
  );
  const op = f.control.data.users.find((u) => u.username === "operator");
  assert.equal(
    (
      await admin.request("/api/users", {
        id: op.id,
        username: op.username,
        name: op.name,
        role: "viewer",
        disabled: true,
        sessions: [],
        revision: f.control.data.revision,
      })
    ).status,
    200,
  );
  assert.equal((await operator.request("/api/overview")).status, 401);
  assert.equal(
    (await outsider.request("/auth/callback?state=unsolicited")).headers.get(
      "Location",
    ),
    "/?login=LOGIN_FAILED",
  );
  await f.control.resetPassword("admin", password);
  assert.equal((await admin.request("/api/overview")).status, 401);
  assert.equal((await admin.login()).mustChange, true);
  f.pending.forEach((r) => r());
});
test("HTTP input limits, unknown fields and non-JSON fail closed", async (t) => {
  const f = await serverFixture(t),
    client = f.client();
  assert.equal(
    (
      await client.request("/api/auth/login", {
        username: "admin",
        password,
        extra: true,
      })
    ).result.error,
    "UNKNOWN_FIELD",
  );
  assert.equal(
    (
      await client.request(
        "/api/auth/login",
        { username: "admin", password },
        { "Content-Type": "text/plain" },
      )
    ).result.error,
    "JSON_REQUIRED",
  );
  assert.equal(
    (
      await client.request("/api/auth/login", {
        username: "admin",
        password: "a".repeat(70000),
      })
    ).result.error,
    "BODY_TOO_LARGE",
  );
});

test("revocation while a request body arrives prevents command execution", async (t) => {
  const f = await serverFixture(t),
    client = f.client();
  await f.control.update((d) => {
    d.users[0].mustChange = false;
  });
  await client.login();
  let admitted;
  const entered = new Promise((resolve) => {
    admitted = resolve;
  });
  const original = f.web.browsers.get.bind(f.web.browsers);
  f.web.browsers.get = (req) => {
    const result = original(req);
    admitted();
    return result;
  };
  let request;
  const response = new Promise((resolve, reject) => {
    request = httpRequest(
      f.web.options.origin + "/api/jobs",
      {
        method: "POST",
        headers: {
          Cookie: client.cookie,
          Origin: f.web.options.origin,
          "Content-Type": "application/json",
          "X-CSRF-Token": client.csrf,
        },
      },
      (res) => {
        let data = "";
        res.on("data", (chunk) => {
          data += chunk;
        });
        res.on("end", () =>
          resolve({ status: res.statusCode, ...JSON.parse(data) }),
        );
      },
    );
    request.on("error", reject);
    request.write('{"action":"stop",');
  });
  await entered;
  await f.control.resetPassword("admin", newPassword);
  request.end('"session":"session-one"}');
  assert.equal((await response).status, 401);
  assert.equal(f.dispatches.length, 0);
});
