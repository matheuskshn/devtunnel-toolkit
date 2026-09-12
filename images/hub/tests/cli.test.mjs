import test from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { createServer as unixServer } from "node:net";
import { createServer as httpServer } from "node:http";
import { fileURLToPath } from "node:url";

const cli = fileURLToPath(new URL("../dist/cli.js", import.meta.url));
async function fixture(t) {
  const dir = await mkdtemp(path.join(tmpdir(), "hub-cli-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const config = path.join(dir, "hub.json");
  await writeFile(config, "{}");
  return {
    dir,
    env: {
      PATH: "/usr/bin:/bin",
      HUB_CONFIG: config,
      HUB_RUN_DIR: dir,
      HUB_DATA_DIR: dir,
      ...(process.env.NODE_V8_COVERAGE
        ? { NODE_V8_COVERAGE: process.env.NODE_V8_COVERAGE }
        : {}),
    },
  };
}
async function run(args, env, input = "") {
  const child = spawn(process.execPath, [cli, ...args], {
    env,
    stdio: ["pipe", "pipe", "pipe"],
  });
  let stdout = "",
    stderr = "";
  child.stdout.on("data", (chunk) => (stdout += chunk));
  child.stderr.on("data", (chunk) => (stderr += chunk));
  child.stdin.on("error", () => {});
  child.stdin.end(input);
  const timer = setTimeout(() => child.kill("SIGKILL"), 8000);
  try {
    const status = await new Promise((resolve, reject) => {
      child.on("error", reject);
      child.on("close", (code, signal) =>
        signal ? reject(new Error("CLI did not terminate")) : resolve(code),
      );
    });
    return { status, stdout, stderr };
  } finally {
    clearTimeout(timer);
  }
}
async function manager(t, dir, response, onCommand = () => {}) {
  const server = unixServer((socket) => {
    let received = "";
    socket.on("data", (chunk) => {
      received += chunk;
      if (received.includes("\n")) {
        onCommand(JSON.parse(received.trim()));
        socket.end(response);
      }
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(path.join(dir, "manager.sock"), resolve);
  });
  t.after(() => new Promise((resolve) => server.close(resolve)));
}

test("CLI help, missing manager and unlocked server fail without external services", async (t) => {
  const { env } = await fixture(t);
  assert.equal((await run(["help"], env)).status, 0);
  assert.equal((await run(["unknown"], env)).status, 1);
  const missing = await run(["session", "list"], env);
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /MANAGER_UNAVAILABLE/);
  assert.equal((await run(["serve"], env)).status, 1);
  const backend = await run(["serve"], {
    ...env,
    HUB_LOCKED: "1",
    HUB_STORAGE_BACKEND: "invalid",
  });
  assert.equal(backend.status, 1);
  assert.match(backend.stderr, /INVALID_STORAGE_BACKEND/);
});

test("CLI health reports local readiness, service failure and refused connection", async (t) => {
  const { env } = await fixture(t);
  let status = 200;
  const server = httpServer((req, res) => {
    assert.equal(req.url, "/ready");
    res.writeHead(status);
    res.end();
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const settings = { ...env, HUB_HEALTH_PORT: String(server.address().port) };
  assert.equal((await run(["health"], settings)).status, 0);
  status = 503;
  assert.equal((await run(["health"], settings)).status, 1);
  await new Promise((resolve) => server.close(resolve));
  assert.equal((await run(["health"], settings)).status, 1);
});

test("CLI streams output and structured results through its local manager socket", async (t) => {
  const { env, dir } = await fixture(t);
  let request;
  await manager(
    t,
    dir,
    '{"output":"synthetic progress\\n"}\n{"result":{"ok":true}}\n',
    (args) => (request = args),
  );
  const result = await run(["session", "list"], env);
  assert.deepEqual(request, ["session", "list"]);
  assert.equal(result.status, 0);
  assert.match(result.stdout, /synthetic progress/);
  assert.match(result.stdout, /"ok": true/);
});

for (const [name, response, expected] of [
  ["structured error", '{"error":"AUTH_REQUIRED"}\n', "AUTH_REQUIRED"],
  ["malformed response", "not-json\n", ""],
  ["incomplete response", '{"output":"incomplete"}\n', ""],
])
  test(`CLI rejects ${name}`, async (t) => {
    const { env, dir } = await fixture(t);
    await manager(t, dir, response);
    const result = await run(["session", "list"], env);
    assert.equal(result.status, 1);
    if (expected) assert.ok(result.stderr.includes(expected));
  });

test("CLI password reset uses stdin only, validates length and never prints the password", async (t) => {
  const { env, dir } = await fixture(t);
  let request;
  await manager(t, dir, '{"result":{"ok":true}}\n', (args) => (request = args));
  const password = "synthetic-only-cli-fixture";
  const result = await run(
    ["admin", "reset-password", "test-admin", "--password-stdin"],
    env,
    password + "\r\n",
  );
  assert.equal(result.status, 0);
  assert.deepEqual(request, [
    "admin",
    "reset-password",
    "test-admin",
    password,
  ]);
  assert.ok(!(result.stdout + result.stderr).includes(password));
  assert.match(
    (
      await run(
        ["admin", "reset-password", "test-admin", "--unsafe-argument"],
        env,
      )
    ).stderr,
    /USAGE/,
  );
  assert.match(
    (
      await run(
        ["admin", "reset-password", "test-admin", "--password-stdin"],
        env,
        "x".repeat(4097),
      )
    ).stderr,
    /PASSWORD_TOO_LONG/,
  );
});
