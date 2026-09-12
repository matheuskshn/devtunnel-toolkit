import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { get } from "node:http";
import { StateStore } from "../dist/state.js";
import { parseConfig } from "../dist/model.js";
import { ControlStore } from "../dist/web/control.js";
import { WebConsole } from "../dist/web/server.js";

async function until(condition) {
  const deadline = Date.now() + 3000;
  while (!condition()) {
    assert.ok(Date.now() < deadline, "SSE condition timed out");
    await new Promise((r) => setTimeout(r, 10));
  }
}
async function fixture(t) {
  const directory = await mkdtemp(path.join(tmpdir(), "hub-sse-test-"));
  const config = parseConfig({}),
    store = new StateStore(directory, config);
  await store.load();
  store.add("session-one", "github");
  store.add("session-two", "microsoft");
  const key = randomBytes(32).toString("base64"),
    control = new ControlStore(store, key);
  await control.load();
  await control.update((d) => {
    d.users.push({
      ...d.users[0],
      id: "operator",
      username: "operator",
      role: "operator",
      sessions: ["session-one"],
      mustChange: false,
    });
    d.users.push({
      ...d.users[0],
      id: "viewer",
      username: "viewer",
      role: "viewer",
      sessions: ["session-two"],
      mustChange: false,
    });
  });
  const pending = [];
  const host = {
    config,
    sessions: () => store.state.sessions,
    ready: () => true,
    track: (p) => p,
    policy: async (value, persist) => {
      Object.assign(config, parseConfig({ ...config, ...value }));
      await persist();
    },
    dispatch: async (_args, output) => {
      output(
        "https://github.com/login/device code ABCD-1234 synthetic-private-token",
      );
      await new Promise((resolve) => pending.push(resolve));
    },
  };
  const web = new WebConsole(host, control, {
    port: 0,
    origin: "http://127.0.0.1",
    secure: false,
    key,
    requirePasswordChange: false,
  });
  await web.open();
  const origin = `http://127.0.0.1:${web.server.address().port}`;
  web.options.origin = origin;
  const requests = [];
  t.after(async () => {
    requests.forEach((r) => r.destroy());
    pending.forEach((r) => r());
    web.close();
    await rm(directory, { recursive: true, force: true });
  });
  function client(id = "operator", provider) {
    const headers = {};
    web.browsers.create(
      { getHeader: (k) => headers[k], setHeader: (k, v) => (headers[k] = v) },
      control.user(id),
      provider,
    );
    const cookie = headers["Set-Cookie"][0].split(";")[0];
    const req = { headers: { cookie } };
    return { cookie, req, session: web.browsers.get(req).session };
  }
  async function http(client, route, data, extra = {}) {
    const response = await fetch(origin + route, {
      method: data ? "POST" : "GET",
      headers: {
        Cookie: client?.cookie ?? "",
        ...(data
          ? {
              Origin: origin,
              "Content-Type": "application/json",
              "X-CSRF-Token": client.session.csrf,
            }
          : {}),
        ...extra,
      },
      body: data ? JSON.stringify(data) : undefined,
    });
    return {
      status: response.status,
      headers: response.headers,
      data: await response.json(),
    };
  }
  async function stream(client, extra = {}, route = "/api/events") {
    return new Promise((resolve, reject) => {
      const request = get(
        origin + route,
        {
          headers: {
            Accept: "text/event-stream",
            Cookie: client?.cookie ?? "",
            ...extra,
          },
        },
        (response) => {
          const result = {
            request,
            response,
            status: response.statusCode,
            text: "",
            ended: false,
            events: [],
          };
          let buffer = "";
          response.setEncoding("utf8");
          response.on("data", (chunk) => {
            result.text += chunk;
            buffer += chunk;
            let at;
            while ((at = buffer.indexOf("\n\n")) >= 0) {
              const frame = buffer.slice(0, at);
              buffer = buffer.slice(at + 2);
              const event = frame.match(/^event: (.*)$/m)?.[1];
              const data = frame.match(/^data: (.*)$/m)?.[1];
              if (event)
                result.events.push({
                  event,
                  data: data ? JSON.parse(data) : undefined,
                  id: frame.match(/^id: (.*)$/m)?.[1],
                });
            }
          });
          response.on("end", () => (result.ended = true));
          response.on("error", () => (result.ended = true));
          response.on("close", () => (result.ended = true));
          resolve(result);
        },
      );
      request.once("error", reject);
      requests.push(request);
    });
  }
  return { config, store, control, host, web, origin, client, http, stream };
}

test("SSE enforces authentication, exact origin, fetch-site, no URL credentials and password policy", async (t) => {
  const f = await fixture(t),
    c = f.client();
  assert.equal((await f.stream()).status, 401);
  assert.equal(
    (await f.stream(c, { Origin: "https://attacker.example.com" })).status,
    403,
  );
  assert.equal(
    (await f.stream(c, { "Sec-Fetch-Site": "cross-site" })).status,
    403,
  );
  assert.equal(
    (await f.stream(c, { "Sec-Fetch-Site": "same-site" })).status,
    403,
  );
  assert.equal(
    (await f.stream(c, {}, "/api/events?token=synthetic")).status,
    400,
  );
  assert.equal((await f.stream(c, { Accept: "text/html" })).status, 400);
  f.web.options.requirePasswordChange = true;
  assert.equal((await f.stream(f.client("admin"))).status, 400);
  const s = await f.stream(c, {
    Origin: f.origin,
    "Sec-Fetch-Site": "same-origin",
  });
  await until(() => s.events.length);
  assert.equal(s.status, 200);
  assert.match(s.response.headers["content-type"], /^text\/event-stream/);
  assert.equal(s.response.headers["x-accel-buffering"], "no");
  assert.match(s.response.headers["cache-control"], /no-store/);
  assert.equal(s.response.headers["access-control-allow-origin"], undefined);
  assert.deepEqual(s.events[0].data, {
    reset: true,
    topics: ["overview", "logs", "catalog"],
  });
});

test("SSE invalidations are scoped, logs do not invalidate overview and secrets never enter stream", async (t) => {
  const f = await fixture(t),
    c = f.client(),
    viewer = f.client("viewer");
  const s = await f.stream(c);
  await until(() => s.events.length === 1);
  f.web.record({
    event: "hidden",
    session_id: "session-two",
    secret: "synthetic-other-user",
  });
  f.store.get("session-two").status = "running";
  f.web.checkStreams();
  await new Promise((r) => setTimeout(r, 30));
  assert.equal(s.events.length, 1);
  f.web.record({ event: "visible", session_id: "session-one" });
  f.web.checkStreams();
  await until(() => s.events.length === 2);
  assert.deepEqual(s.events.at(-1).data.topics, ["logs"]);
  f.store.get("session-one").status = "running";
  f.web.checkStreams();
  await until(() => s.events.length === 3);
  assert.deepEqual(s.events.at(-1).data.topics, ["overview"]);
  assert.equal(
    (await f.http(c, "/api/jobs", { action: "login", session: "session-one" }))
      .status,
    202,
  );
  f.web.checkStreams();
  await until(() => s.events.length === 4);
  const mine = await f.http(c, "/api/overview"),
    theirs = await f.http(viewer, "/api/overview");
  assert.equal(mine.data.jobs[0].device.code, "ABCD-1234");
  assert.equal(theirs.data.jobs.length, 0);
  assert.equal(theirs.data.sessions.length, 1);
  for (const secret of [
    "ABCD-1234",
    "synthetic-private-token",
    "synthetic-other-user",
    "session-one",
    "session-two",
    "operator",
  ])
    assert.ok(!s.text.includes(secret));
  assert.equal(
    (
      await f.http(viewer, "/api/jobs", {
        action: "start",
        session: "session-one",
      })
    ).status,
    403,
  );
});

test("SSE reconnect resnapshots regardless of Last-Event-ID and heartbeats carry no state", async (t) => {
  const f = await fixture(t),
    c = f.client();
  const s = await f.stream(c);
  await until(() => s.events.length);
  const id = s.events[0].id;
  for (const live of f.web.streams) live.heartbeat = 0;
  f.web.checkStreams();
  await until(() => s.events.some((e) => e.event === "ping"));
  assert.deepEqual(s.events.at(-1).data, {});
  s.request.destroy();
  await until(() => f.web.streams.size === 0);
  assert.equal(f.web.streamTimer, undefined);
  const replacement = await f.stream(c, { "Last-Event-ID": id });
  await until(() => replacement.events.length);
  assert.equal(replacement.events[0].event, "sync");
  assert.equal(replacement.events[0].data.reset, true);
});

test("SSE and background HTTP preserve idle/absolute expiry; logout and user/provider epochs revoke streams", async (t) => {
  const f = await fixture(t),
    c = f.client();
  c.session.used = Date.now() - 10000;
  const idle = c.session.used,
    s = await f.stream(c);
  await until(() => s.events.length);
  assert.equal(c.session.used, idle);
  assert.equal(
    (
      await f.http(c, "/api/overview", undefined, {
        "X-Hub-Background": "true",
      })
    ).status,
    200,
  );
  assert.equal(c.session.used, idle);
  f.web.checkStreams();
  assert.equal(c.session.used, idle);
  c.session.used = Date.now() - 1800001;
  f.web.checkStreams();
  await until(() => s.ended);
  assert.equal(s.events.at(-1).event, "auth");
  assert.equal((await f.http(c, "/api/overview")).status, 401);
  for (const mode of ["absolute", "logout", "user", "provider"]) {
    if (mode === "provider")
      await f.control.update((d) =>
        d.providers.push({
          id: "test-provider",
          kind: "github",
          label: "Test",
          enabled: true,
          registration: false,
          epoch: 0,
          clientId: "synthetic",
          clientSecret: "synthetic",
        }),
      );
    const c = f.client(
        "operator",
        mode === "provider" ? "test-provider" : undefined,
      ),
      s = await f.stream(c);
    await until(() => s.events.length);
    if (mode === "absolute") c.session.created = Date.now() - 28800001;
    if (mode === "logout")
      assert.equal((await f.http(c, "/api/auth/logout", {})).status, 200);
    if (mode === "user")
      await f.control.update(
        (d) => d.users.find((u) => u.id === "operator").epoch++,
      );
    if (mode === "provider")
      await f.control.update((d) => d.providers[0].epoch++);
    f.web.checkStreams();
    await until(() => s.ended);
    assert.equal(s.events.at(-1).event, "auth");
  }
});

test("SSE bounds per-user connections, total capacity and slow-client buffering", async (t) => {
  const f = await fixture(t),
    c = f.client(),
    streams = [];
  for (let i = 0; i < 4; i++) streams.push(await f.stream(c));
  assert.equal((await f.stream(c)).status, 429);
  for (const stream of streams) stream.request.destroy();
  await until(() => f.web.streams.size === 0);
  const sent = [],
    slow = {
      res: {
        writableLength: 16385,
        destroy: () => sent.push("destroy"),
        write: () => sent.push("write"),
      },
    };
  f.web.sendEvent(slow, "change", ["logs"]);
  assert.deepEqual(sent, ["destroy"]);
  const fake = Array.from({ length: 128 }, (_, i) => ({
    user: `synthetic-${i}`,
    res: { destroy() {} },
  }));
  fake.forEach((client) => f.web.streams.add(client));
  assert.equal((await f.stream(c)).status, 429);
  fake.forEach((client) => f.web.streams.delete(client));
});

test("SOCKS controls are returned and saved with web policy, while viewers cannot change them", async (t) => {
  const f = await fixture(t),
    c = f.client("admin"),
    viewer = f.client("viewer");
  const initial = await f.http(c, "/api/overview");
  assert.equal(initial.data.socksEnabled, false);
  assert.equal(initial.data.socksPort, 3180);
  const policy = { socksEnabled: true, socksPort: 3181 };
  assert.equal(
    (
      await f.http(viewer, "/api/config", {
        policy,
        revision: f.control.data.revision,
      })
    ).status,
    403,
  );
  assert.equal(
    (
      await f.http(c, "/api/config", {
        policy,
        revision: f.control.data.revision,
      })
    ).status,
    200,
  );
  assert.deepEqual(f.control.data.policy, policy);
  assert.equal((await f.http(c, "/api/overview")).data.socksPort, 3181);
  const app = await readFile(new URL("../web/app.js", import.meta.url), "utf8");
  assert.match(app, /new EventSource\("\/api\/events"\)/);
  assert.doesNotMatch(app, /\}, 3000\)/);
  assert.match(app, /Habilitar SOCKS5 TCP CONNECT/);
  assert.match(app, /Porta SOCKS5/);
});
