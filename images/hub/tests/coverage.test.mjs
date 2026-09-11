import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { verifyCoverageEmissions } from "./coverage.mjs";

const hub = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

async function fixture(t) {
  const directory = await mkdtemp(path.join(tmpdir(), "hub-coverage-test-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const target = path.join(directory, "images/hub");
  await mkdir(path.join(target, "src/nested"), { recursive: true });
  await mkdir(path.join(target, "dist/nested"), { recursive: true });
  return { directory, target };
}

test("coverage refuses orphaned JS without deleting the developer's artifacts", async (t) => {
  const { target } = await fixture(t);
  await writeFile(path.join(target, "src/current.ts"), "export {};");
  await writeFile(path.join(target, "dist/current.js"), "export {};");
  await writeFile(path.join(target, "src/nested/current.ts"), "export {};");
  await writeFile(path.join(target, "dist/nested/current.js"), "export {};");
  assert.doesNotThrow(() => verifyCoverageEmissions(target));
  const orphan = path.join(target, "dist/nested/removed.js");
  await writeFile(orphan, "// synthetic obsolete build output");
  await writeFile(path.join(target, "dist/nested/removed.js.map"), "{}");
  assert.throws(
    () => verifyCoverageEmissions(target),
    /COVERAGE_STALE_EMISSIONS: dist\/nested\/removed\.js/,
  );
  assert.equal(
    await readFile(orphan, "utf8"),
    "// synthetic obsolete build output",
  );
  assert.equal(await readFile(orphan + ".map", "utf8"), "{}");
});

test("real c8 config includes unexecuted TS and remaps each compiled source exactly once", async (t) => {
  const { directory, target } = await fixture(t);
  await writeFile(path.join(directory, "package.json"), '{"type":"module"}');
  const executed = path.join(target, "src/executed.ts");
  const untouched = path.join(target, "src/untouched.ts");
  await writeFile(
    executed,
    'export function exercised() {\n  return "synthetic";\n}\nexercised();\n',
  );
  await writeFile(
    untouched,
    'export function untouched() {\n  return "not executed";\n}\n',
  );
  const scripts = path.join(directory, ".github/scripts");
  await mkdir(scripts, { recursive: true });
  await writeFile(
    path.join(scripts, "security-plan.mjs"),
    "export const productionPolicy = true;\n",
  );
  await writeFile(
    path.join(scripts, "openvpn-smoke.mjs"),
    "export const integrationFixture = true;\n",
  );
  execFileSync(
    process.execPath,
    [
      path.join(hub, "node_modules/typescript/bin/tsc"),
      "--module",
      "NodeNext",
      "--target",
      "ES2022",
      "--sourceMap",
      "--inlineSources",
      "--outDir",
      path.join(target, "dist"),
      executed,
      untouched,
    ],
    { cwd: directory, stdio: "pipe", timeout: 15000 },
  );
  verifyCoverageEmissions(target);
  const report = path.join(directory, "coverage");
  execFileSync(
    process.execPath,
    [
      path.join(hub, "node_modules/c8/bin/c8.js"),
      "--config",
      path.join(hub, ".c8rc.json"),
      "--reports-dir",
      report,
      "--temp-directory",
      path.join(directory, "v8"),
      process.execPath,
      "images/hub/dist/executed.js",
    ],
    { cwd: directory, stdio: "pipe", timeout: 15000 },
  );
  const lcov = await readFile(path.join(report, "lcov.info"), "utf8");
  const sources = [...lcov.matchAll(/^SF:(.*)$/gm)].map((match) => match[1]);
  assert.deepEqual(sources.sort(), [
    ".github/scripts/security-plan.mjs",
    "images/hub/src/executed.ts",
    "images/hub/src/untouched.ts",
  ]);
  assert.equal(new Set(sources).size, sources.length);
  assert.ok(!sources.some((file) => file.includes("/dist/")));
  const summary = JSON.parse(
    await readFile(path.join(report, "coverage-summary.json"), "utf8"),
  );
  assert.ok(summary[executed].lines.covered > 0);
  assert.ok(summary[untouched].lines.total > 0);
  assert.equal(summary[untouched].lines.covered, 0);
  assert.equal(
    summary[path.join(scripts, "security-plan.mjs")].lines.covered,
    0,
  );
  assert.equal(summary[path.join(scripts, "openvpn-smoke.mjs")], undefined);
});
