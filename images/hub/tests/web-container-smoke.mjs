import { spawnSync, execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";
import path from "node:path";
import assert from "node:assert/strict";
import { setTimeout as delay } from "node:timers/promises";
const prefix = `hub-web-test-${process.pid}`,
  app = `${prefix}-app`,
  volume = `${prefix}-data`,
  network = `${prefix}-net`;
const image = process.env.HUB_TEST_IMAGE ?? "devtunnel-toolkit-hub:local",
  tests = path.dirname(fileURLToPath(import.meta.url));
const preview = process.env.HUB_TEST_PREVIEW === "true";
const docker = (args) =>
  execFileSync("docker", args, {
    encoding: "utf8",
    timeout: 30000,
    stdio: ["ignore", "pipe", "pipe"],
  });
async function ready() {
  for (let i = 0; i < 50; i++) {
    if (
      spawnSync("docker", ["exec", app, "hub", "health"], { stdio: "ignore" })
        .status === 0
    )
      return;
    await delay(200);
  }
  throw Error("HUB_NOT_READY");
}
try {
  docker(["network", "create", ...(preview ? [] : ["--internal"]), network]);
  docker(["volume", "create", volume]);
  const environment = [
    "--env",
    "HUB_WEB_ENABLED=true",
    "--env",
    "HUB_WEB_ORIGIN=http://localhost:8082",
    "--env",
    `HUB_WEB_KEY=${randomBytes(32).toString("base64")}`,
  ];
  docker([
    "run",
    "--detach",
    "--name",
    app,
    "--network",
    network,
    "--read-only",
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges",
    "--tmpfs",
    "/run/hub:uid=1000,gid=1000,mode=0700",
    "--tmpfs",
    "/tmp:mode=1777",
    "--mount",
    `type=volume,src=${volume},dst=/data`,
    "--mount",
    `type=bind,src=${tests},dst=/tests,readonly`,
    ...(preview ? ["--publish", "127.0.0.1:8082:8082"] : []),
    ...environment,
    image,
  ]);
  await ready();
  console.log(
    docker(["exec", app, "node", "/tests/web-probe.mjs", "initial"]).trim(),
  );
  const state = docker([
    "exec",
    app,
    "node",
    "-e",
    "process.stdout.write(require('fs').readFileSync('/data/state.json','utf8'))",
  ]);
  assert.ok(!state.includes("synthetic-provider-secret"));
  assert.ok(!state.includes("synthetic-console-password"));
  assert.ok(!state.includes("scrypt1:"));
  docker(["restart", "--timeout", "15", app]);
  await ready();
  console.log(
    docker(["exec", app, "node", "/tests/web-probe.mjs", "restored"]).trim(),
  );
  const logs = docker(["logs", app]);
  assert.ok(!logs.includes("synthetic-provider-secret"));
  assert.ok(!logs.includes("synthetic-console-password"));
  if (preview) {
    console.log(
      `PREVIEW http://localhost:8082 (synthetic test fixture; container ${app})`,
    );
    const alive = setInterval(() => {}, 60000);
    try {
      await new Promise((r) => {
        process.once("SIGINT", r);
        process.once("SIGTERM", r);
      });
    } finally {
      clearInterval(alive);
    }
  }
  docker(["stop", "--timeout", "15", app]);
  assert.equal(JSON.parse(docker(["inspect", app]))[0].State.ExitCode, 0);
  console.log(
    "PASS non-root read-only single-container console, restart persistence, secret redaction and shutdown",
  );
} catch (e) {
  console.error(e.stderr?.toString() || e.message);
  process.exitCode = 1;
} finally {
  for (const args of [
    ["rm", "--force", app],
    ["volume", "rm", volume],
    ["network", "rm", network],
  ])
    spawnSync("docker", args, { stdio: "ignore" });
}
