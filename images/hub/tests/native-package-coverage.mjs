// Optional coverage of the real packager, ONLY in a disposable native build
// stage with lintian/abigail-tools installed. Never run against a runtime volume.
import fs from "node:fs";
import path from "node:path";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

assert.equal(process.env.HUB_NATIVE_COVERAGE_DISPOSABLE, "1");
assert.ok(fs.existsSync("/.dockerenv"));
assert.ok(fs.existsSync("/native/stage"));
const packages = [
  "libexpat1",
  "libglib2.0-0t64",
  "libp11-kit0",
  "p11-kit",
  "p11-kit-modules",
];
assert.deepEqual(
  JSON.parse(fs.readFileSync("/native/evidence/packages.json")).map(
    (p) => p.package,
  ),
  packages,
);
const output = process.env.HUB_NATIVE_COVERAGE_OUTPUT;
assert.ok(
  output && path.isAbsolute(output),
  "Provide a dedicated mounted report directory",
);

// These exact directories belong to this disposable build stage. Regenerate
// them from the staged ELFs, keeping source archives, regressions and dpkg data.
for (const name of packages)
  for (const prefix of ["package", "download"])
    fs.rmSync(`/native/${prefix}-${name}`, { recursive: true });

execFileSync(
  process.execPath,
  [
    fileURLToPath(
      new URL("../bin/package-native-libraries.mjs", import.meta.url),
    ),
  ],
  {
    env: { ...process.env, NODE_V8_COVERAGE: path.join(output, "tmp") },
    stdio: "inherit",
  },
);
execFileSync(
  process.execPath,
  [fileURLToPath(new URL("./native-package-audit.mjs", import.meta.url))],
  {
    env: {
      ...process.env,
      HUB_NATIVE_AUDIT_OUTPUT: path.join(output, "native-audit"),
    },
    stdio: "inherit",
  },
);
