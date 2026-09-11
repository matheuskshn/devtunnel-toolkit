import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { parse } from "yaml";
const read = async (f) =>
  readFile(new URL(`../../../${f}`, import.meta.url), "utf8");
const workflow = async (name) =>
  parse(await read(`.github/workflows/${name}.yml`));
test("only the suite coordinator receives release tags; development builds cannot overwrite versions", async () => {
  const entry = await workflow("release");
  assert.deepEqual(entry.on.push.tags, ["v*.*.*"]);
  assert.equal(
    entry.jobs.release.uses,
    "./.github/workflows/release-suite.yml",
  );
  for (const name of ["docker", "hub"]) {
    const w = await workflow(name);
    assert.equal(w.on.push.tags, undefined);
    const native = Object.values(w.jobs).find(
      (j) => j.uses === "./.github/workflows/native-image.yml",
    );
    assert.ok(native);
    assert.equal(native.with.mode, undefined);
  }
  const publisher = await read(".github/scripts/native-publication.mjs");
  assert.match(publisher, /GITHUB_RUN_ATTEMPT/);
  assert.match(publisher, /GITHUB_RUN_ID/);
  assert.doesNotMatch(publisher, /\x3alatest/);
});
test("release publication waits for checks and all builds, reuses candidates, and serializes promotions", async () => {
  const w = await workflow("release-suite");
  assert.equal(w.concurrency["cancel-in-progress"], false);
  assert.deepEqual(w.jobs.build.needs, ["prepare", "checks"]);
  assert.deepEqual(w.jobs.record.needs, ["prepare", "checks", "build"]);
  assert.deepEqual(w.jobs.finalize.needs, ["prepare", "checks", "record"]);
  assert.equal(w.jobs.checks.with.container, true);
  assert.equal(w.jobs.build.uses, "./.github/workflows/native-image.yml");
  assert.equal(w.jobs.build.with.mode, "release");
  const shared = await workflow("native-image");
  assert.equal(shared.jobs.build.if, "needs.plan.outputs.exists != 'true'");
  const build = shared.jobs.build.steps.find((s) =>
    s.uses?.startsWith("docker/build-push-action"),
  );
  assert.equal(build.with.platforms, "linux/${{ matrix.arch }}");
  assert.equal(build.with.provenance, true);
  assert.equal(build.with.sbom, true);
  assert.equal(
    w.jobs.finalize.steps.at(-1).run,
    "node .github/scripts/release.mjs finalize",
  );
  assert.equal(w.on.pull_request_target, undefined);
});
test("release PR automation drafts releases, synchronizes versions, validates PRs directly and calls the coordinator", async () => {
  const w = await workflow("release-please");
  const config = JSON.parse(await read("release-please-config.json")).packages[
    "."
  ];
  assert.equal(config.draft, true);
  assert.equal(config["include-component-in-tag"], false);
  assert.equal(config["initial-version"], "0.1.0-rc.1");
  assert.equal(config["version-file"], "version.txt");
  assert.equal(config["extra-files"].length, 3);
  assert.equal(w.jobs.release.uses, "./.github/workflows/release-suite.yml");
  assert.equal(w.jobs.release.if, "needs.proposal.outputs.created == 'true'");
  assert.equal(
    w.jobs["validate-proposal"].uses,
    "./.github/workflows/release-checks.yml",
  );
  assert.equal(w.jobs["proposal-status"].permissions.statuses, "write");
});
