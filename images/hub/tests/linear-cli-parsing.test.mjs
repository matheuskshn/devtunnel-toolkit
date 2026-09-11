import assert from "node:assert/strict";
import { performance } from "node:perf_hooks";
import test from "node:test";
import { parseJson } from "../dist/auth.js";
import { waitSecretService } from "../dist/processes.js";

test("CLI JSON scanner preserves anchored banners, multiline whitespace and strict JSON suffixes", () => {
  for (const prefix of [
    "",
    "license banner\n",
    "license with {inline brace}\n",
    "license\r\n",
    "license\u2028",
    "license\u2029",
    "license\n\n\t ",
  ]) {
    assert.deepEqual(parseJson(prefix + '{"ok":true}\r\n'), { ok: true });
  }
  for (const output of [
    "",
    'license {"ok":true}',
    'license\n[{"ok":true}]',
    "license\nnon-json",
  ]) {
    assert.throws(() => parseJson(output), /CLI_JSON_REQUIRED/);
  }
  for (const output of [
    "{not-json}",
    '{"ok":true}\ntrailer',
    "{}\n{}",
    "\ufeff{}",
    "\n\u00a0{}",
    "license\n\v\n{}",
    "{}\u00a0",
  ]) {
    assert.throws(() => parseJson(output), /CLI_JSON_INVALID/);
  }
});

async function serviceReply(reply, { next = "boolean true" } = {}) {
  let calls = 0;
  await waitSecretService(
    {},
    { exitCode: null, signalCode: null },
    new AbortController().signal,
    {
      timeout: 2000,
      query: async () => (++calls === 1 ? reply : next),
    },
  );
  return calls;
}

test("D-Bus scanner retains boolean line matching and true precedence without loose substring matches", async () => {
  for (const output of [
    "boolean true",
    "method return\n   boolean true\n",
    "\r\n\tboolean true\r\n",
    "\f\t boolean true \v\u00a0",
    "header\u2028boolean true\u2029footer",
    "boolean false\nboolean true",
    "boolean true\nboolean false",
  ]) {
    assert.equal(await serviceReply(output), 1);
  }
  assert.equal(await serviceReply("method return\n\tboolean false\n"), 2);
  for (const output of [
    "",
    "Boolean true",
    "boolean   true",
    "boolean\ttrue",
    "not boolean true",
    "boolean true suffix",
    'string "boolean true"',
  ]) {
    await assert.rejects(serviceReply(output), /SESSION_SERVICE_INVALID_REPLY/);
  }
});

test("one-megabyte adversarial whitespace is rejected in bounded linear time", async () => {
  const whitespace = "\n".repeat(1024 * 1024);
  const start = performance.now();
  assert.throws(() => parseJson(whitespace + "not-json"), /CLI_JSON_REQUIRED/);
  assert.deepEqual(parseJson(whitespace + '{"ok":true}'), { ok: true });
  await assert.rejects(
    serviceReply(whitespace),
    /SESSION_SERVICE_INVALID_REPLY/,
  );
  assert.ok(
    performance.now() - start < 2000,
    "bounded subprocess output must not stall the event loop",
  );
});
