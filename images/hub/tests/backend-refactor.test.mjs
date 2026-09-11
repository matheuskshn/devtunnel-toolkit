import assert from "node:assert/strict";
import test from "node:test";
import {
  connectRequest,
  greetingLength,
  SocksProtocolError,
} from "../dist/socks-protocol.js";
import { HubError, parseConfig } from "../dist/model.js";
import { WebConsole } from "../dist/web/server.js";

test("incremental SOCKS parsers consume exact frames and leave pipelined bytes untouched", () => {
  const greeting = Buffer.from([5, 3, 1, 2, 0]);
  for (let length = 0; length < greeting.length; length++) {
    assert.equal(greetingLength(greeting.subarray(0, length)), undefined);
  }
  assert.equal(
    greetingLength(Buffer.concat([greeting, Buffer.from("next")])),
    greeting.length,
  );
  const fixtures = [
    {
      bytes: Buffer.from([5, 1, 0, 1, 192, 0, 2, 10, 1, 187]),
      host: "192.0.2.10",
    },
    {
      bytes: Buffer.from([
        5, 1, 0, 4, 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
        187,
      ]),
      host: "[2001:db8:0:0:0:0:0:1]",
    },
    {
      bytes: Buffer.concat([
        Buffer.from([5, 1, 0, 3, 13]),
        Buffer.from("Example.Test."),
        Buffer.from([1, 187]),
      ]),
      host: "example.test",
    },
  ];
  for (const { bytes, host } of fixtures) {
    for (let length = 0; length < bytes.length; length++) {
      assert.equal(connectRequest(bytes.subarray(0, length)), undefined);
    }
    const input = Buffer.concat([bytes, Buffer.from([0, 255, 13, 10])]);
    const before = Buffer.from(input);
    assert.deepEqual(connectRequest(input), {
      host,
      end: bytes.length,
      port: 443,
    });
    assert.deepEqual(input, before);
  }
});

test("pure SOCKS errors retain method negotiation and CONNECT rejection codes", () => {
  for (const [bytes, code, method] of [
    [[4, 1, 0], 1, false],
    [[5, 0], 1, true],
    [[5, 1, 2], 1, true],
  ]) {
    assert.throws(
      () => greetingLength(Buffer.from(bytes)),
      (error) =>
        error instanceof SocksProtocolError &&
        error.replyCode === code &&
        error.method === method,
    );
  }
  for (const [bytes, code] of [
    [[5, 2, 0, 1], 7],
    [[5, 3, 0, 1], 7],
    [[5, 1, 1, 1], 1],
    [[5, 1, 0, 9], 8],
    [[5, 1, 0, 3, 0], 8],
    [[5, 1, 0, 1, 192, 0, 2, 1, 0, 0], 1],
  ]) {
    assert.throws(
      () => connectRequest(Buffer.from(bytes)),
      (error) =>
        error instanceof SocksProtocolError &&
        error.replyCode === code &&
        !error.method,
    );
  }
});

function fixture() {
  const user = {
    id: "admin",
    username: "admin",
    role: "admin",
    sessions: [],
    mustChange: false,
  };
  const host = {
    config: parseConfig({}),
    sessions: () => [],
    ready: () => true,
    track: (promise) => promise,
  };
  const control = {
    data: { users: [user], providers: [], revision: 0 },
    user: () => user,
  };
  const options = {
    port: 0,
    origin: "http://127.0.0.1",
    secure: false,
    requirePasswordChange: false,
  };
  return { web: new WebConsole(host, control, options), user };
}

test("authenticated GET projections never yield between authorization and producing their response", async () => {
  for (const route of [
    "/api/me",
    "/api/overview",
    "/api/logs",
    "/api/users",
    "/api/providers",
    "/api/config",
  ]) {
    const { web, user } = fixture();
    let ended = false;
    let endedBeforeMicrotask = false;
    let touched;
    web.browsers.get = (_request, touch) => {
      touched = touch;
      queueMicrotask(() => {
        endedBeforeMicrotask = ended;
      });
      return { user, session: { csrf: "synthetic-csrf" } };
    };
    const response = {
      setHeader() {},
      writeHead() {},
      end() {
        ended = true;
      },
    };
    await web.request(
      {
        method: "GET",
        url: route,
        headers: { host: "127.0.0.1", "x-hub-background": "true" },
      },
      response,
    );
    assert.equal(endedBeforeMicrotask, true, route);
    assert.equal(
      touched,
      false,
      "background projections must not extend idle expiry",
    );
    web.close();
  }
});

test("HTTP error adapter preserves every status and defaults safely for arbitrary error codes", async (t) => {
  const { web } = fixture();
  t.after(() => web.close());
  let error;
  web.request = async () => {
    throw error;
  };
  await web.open();
  const origin = `http://127.0.0.1:${web.server.address().port}`;
  for (const [code, status] of [
    ["LOGIN_REQUIRED", 401],
    ["FORBIDDEN", 403],
    ["CSRF_REJECTED", 403],
    ["NOT_FOUND", 404],
    ["RATE_LIMITED", 429],
    ["AUTH_BUSY", 429],
    ["CONFIG_CONFLICT", 409],
    ["SESSION_BUSY", 409],
    ["STOP_SESSIONS_FIRST", 409],
    ["INTERNAL_ERROR", 500],
    ["INVALID_INPUT", 400],
    ["toString", 400],
    ["constructor", 400],
  ]) {
    error = new HubError(code);
    const response = await fetch(origin);
    assert.equal(response.status, status, code);
    assert.deepEqual(await response.json(), { error: code });
  }
  error = new Error("synthetic detail must not be returned");
  const response = await fetch(origin);
  assert.equal(response.status, 500);
  assert.deepEqual(await response.json(), { error: "INTERNAL_ERROR" });
});
