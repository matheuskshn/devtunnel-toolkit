import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("./security-plan.mjs", import.meta.url));

function plan(t, overrides) {
  const directory = mkdtempSync(path.join(tmpdir(), "toolkit-security-plan-"));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const event = path.join(directory, "event.json");
  const output = path.join(directory, "output.txt");
  writeFileSync(event, "{}");
  const result = spawnSync(process.execPath, [script], {
    cwd: directory,
    encoding: "utf8",
    env: {
      ...process.env,
      SECURITY_FULL_SUITE: "false",
      GITHUB_EVENT_NAME: "workflow_dispatch",
      GITHUB_REF: "refs/heads/main",
      GITHUB_EVENT_PATH: event,
      GITHUB_OUTPUT: output,
      ...overrides,
    },
  });
  return { result, output };
}

for (const [name, overrides] of [
  [
    "explicit suite release",
    { SECURITY_FULL_SUITE: "true", GITHUB_EVENT_NAME: "workflow_call" },
  ],
  ["manual execution", {}],
  [
    "version tag",
    { GITHUB_EVENT_NAME: "push", GITHUB_REF: "refs/tags/v1.2.3" },
  ],
]) {
  test(`security plan scans all five images on both architectures for ${name}`, (t) => {
    const { result, output } = plan(t, overrides);
    assert.equal(result.status, 0, result.stderr);
    const lines = readFileSync(output, "utf8").trim().split("\n");
    assert.equal(lines[1], "any=true");
    const { include } = JSON.parse(lines[0].slice("matrix=".length));
    assert.equal(include.length, 10);
    for (const image of include) {
      assert.equal(
        image.runner,
        image.platform === "linux/arm64" ? "ubuntu-24.04-arm" : "ubuntu-24.04",
      );
      assert.equal(
        image.machine,
        image.platform === "linux/arm64" ? "aarch64" : "x86_64",
      );
    }
    assert.equal(new Set(include.map((item) => item.image)).size, 5);
    assert.equal(
      new Set(include.map((item) => `${item.image}:${item.platform}`)).size,
      10,
    );
    for (const platform of ["linux/amd64", "linux/arm64"])
      assert.equal(
        include.filter((item) => item.platform === platform).length,
        5,
      );
  });
}

test("an unsupported security event fails before producing a successful plan", (t) => {
  const { result, output } = plan(t, { GITHUB_EVENT_NAME: "unknown" });
  assert.notEqual(result.status, 0);
  assert.equal(existsSync(output), false);
});
