import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  chmodSync,
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));

test("package refuses unsupported targets before attempting a publish", () => {
  const result = spawnSync(
    process.execPath,
    [join(root, "scripts/package.mjs"), "--rid", "../outside"],
    { encoding: "utf8" },
  );
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Usage:/);
});

test(
  "Linux startup delegates exact arguments to its packaged agent without a shell",
  { skip: process.platform !== "linux" || process.getuid?.() === 0 },
  () => {
    const fixture = mkdtempSync(join(tmpdir(), "devtunnel-startup-test-"));
    try {
      const packageDir = join(fixture, "portable path%with$dollar");
      const startup = join(packageDir, "startup");
      for (const directory of [startup, join(packageDir, "agent")])
        mkdirSync(directory, { recursive: true });
      const installer = join(startup, "configure-startup.sh");
      copyFileSync(
        join(root, "packaging/linux/configure-startup.sh"),
        installer,
      );
      writeFileSync(
        join(packageDir, "agent/DevTunnelToolkit.Agent"),
        '#!/bin/sh\nprintf "<%s>\\n" "$@"\n',
      );
      chmodSync(join(packageDir, "agent/DevTunnelToolkit.Agent"), 0o700);
      const invoke = (...args) =>
        spawnSync("bash", [installer, ...args], { encoding: "utf8" });
      const enabled = invoke(
        "--enable",
        "--data-dir",
        "/tmp/data with $value %i",
      );
      assert.equal(enabled.status, 0, enabled.stderr);
      assert.equal(
        enabled.stdout,
        "<--configure-startup>\n<enable>\n<--data-dir>\n</tmp/data with $value %i>\n",
      );
      assert.equal(
        invoke("--disable").stdout,
        "<--configure-startup>\n<disable>\n",
      );
      assert.equal(invoke("unexpected").status, 2);
    } finally {
      rmSync(fixture, { recursive: true, force: true });
    }
  },
);
