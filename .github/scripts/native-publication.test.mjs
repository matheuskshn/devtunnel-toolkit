import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { parse } from "../../images/hub/node_modules/yaml/dist/index.js";
import {
  publicationPlan,
  digestSources,
  promoteNative,
} from "./native-publication.mjs";
import { suiteImages } from "./release-policy.mjs";

const sha = "a".repeat(40),
  digest = `sha256:${"b".repeat(64)}`;
const settings = {
  image: "devtunnel-toolkit-hub",
  mode: "edge",
  sha,
  runId: "10",
  attempt: "2",
  owner: "example",
  version: "0.1.0-rc.4",
};
test("Native publication selects only known contexts and validates all interpolated identity fields", () => {
  for (const image of suiteImages) {
    const plan = publicationPlan({ ...settings, image: image.image });
    assert.equal(plan.context, image.context);
    assert.equal(plan.dockerfile, image.dockerfile);
    assert.equal(plan.unique, `${sha}-10-2`);
  }
  for (const invalid of [
    { image: "../../other" },
    { mode: "latest" },
    { sha: "-x" },
    { runId: "a\nb" },
    { attempt: "-1" },
    { owner: "example/other" },
  ])
    assert.throws(() => publicationPlan({ ...settings, ...invalid }));
});
test("Native merge rejects missing, duplicate or invalid architecture digests", () => {
  const valid = [
    { arch: "amd64", digest },
    { arch: "arm64", digest },
  ];
  assert.deepEqual(digestSources(valid, "example/image"), [
    `example/image@${digest}`,
    `example/image@${digest}`,
  ]);
  for (const invalid of [
    [],
    [valid[0]],
    [valid[0], valid[0]],
    [...valid, valid[0]],
    [valid[0], { arch: "arm64", digest: "--tag=latest" }],
  ])
    assert.throws(() => digestSources(invalid, "example/image"));
});
function promotion() {
  const registry = new Map(),
    copies = [];
  return {
    registry,
    copies,
    options: {
      source: `example/image@${digest}`,
      digest,
      targets: ["example/image:sha-test", "mirror/image:sha-test"],
      aliases: ["example/image:edge"],
      inspect: async (ref) => registry.get(ref),
      copy: async (source, target) => {
        copies.push(target);
        registry.set(target, source.split("@")[1]);
      },
    },
  };
}
test("Native publication preflights all immutable tags before aliases, preserving exact digests on retry", async () => {
  const { registry, copies, options } = promotion();
  await promoteNative(options);
  assert.deepEqual(copies, [...options.targets, ...options.aliases]);
  copies.length = 0;
  await promoteNative(options);
  assert.deepEqual(copies, options.aliases);
  registry.set(options.targets[1], `sha256:${"c".repeat(64)}`);
  copies.length = 0;
  await assert.rejects(promoteNative(options), /IMMUTABLE/);
  assert.deepEqual(copies, []);
});
test("Registry errors and digest mismatches block native aliases", async () => {
  for (const fault of ["inspect", "copy", "digest"]) {
    const { options, copies } = promotion();
    if (fault === "inspect")
      options.inspect = async () => {
        throw Error("registry unavailable");
      };
    if (fault === "copy")
      options.copy = async () => {
        throw Error("copy failed");
      };
    if (fault === "digest") options.copy = async () => {};
    await assert.rejects(promoteNative(options));
    assert.deepEqual(copies, []);
  }
});
test("Publication workflows use native runners and preserve PR isolation and release ordering", async () => {
  const workflow = async (name) =>
    parse(
      await readFile(
        new URL(`../workflows/${name}.yml`, import.meta.url),
        "utf8",
      ),
    );
  const native = await workflow("native-image");
  assert.deepEqual(native.jobs.build.strategy.matrix.include, [
    { arch: "amd64", runner: "ubuntu-24.04", machine: "x86_64" },
    { arch: "arm64", runner: "ubuntu-24.04-arm", machine: "aarch64" },
  ]);
  assert.ok(native.jobs.build.steps.some((s) => s.run?.includes("uname -m")));
  const build = native.jobs.build.steps.find((s) => s.id === "build");
  assert.equal(build.with.platforms, "linux/${{ matrix.arch }}");
  assert.equal(build.with.provenance, true);
  assert.equal(build.with.sbom, true);
  assert.match(build.with.outputs, /push-by-digest=true/);
  assert.match(
    build.with.outputs,
    /inputs.publish && github.event_name != 'pull_request'/,
  );
  assert.match(
    native.jobs.publish.if,
    /inputs.publish.*pull_request.*pull_request_target/,
  );
  const upload = native.jobs.build.steps.find((s) =>
    s.uses?.startsWith("actions/upload-artifact@"),
  );
  assert.equal(
    upload.with.name,
    "native-${{ inputs.image }}--${{ matrix.arch }}",
  );
  const steps = native.jobs.publish.steps;
  const download = steps.find((s) =>
    s.uses?.startsWith("actions/download-artifact@"),
  );
  assert.equal(download.with.pattern, "native-${{ inputs.image }}--*");
  const scanIndex = steps.findIndex(
    (s) => s.name === "Scan the exact native digests before tag promotion",
  );
  assert.ok(
    scanIndex >= 0 &&
      scanIndex <
        steps.findIndex(
          (s) => s.run === "node .github/scripts/native-publication.mjs merge",
        ),
  );
  assert.match(steps[scanIndex].run, /--severity HIGH,CRITICAL/);
  assert.match(steps[scanIndex].run, /--exit-code 1/);
  assert.doesNotMatch(steps[scanIndex].run, /ignore-unfixed/);
  for (const name of [
    "docker",
    "hub",
    "release-suite",
    "native-image",
    "security",
  ]) {
    const text = await readFile(
      new URL(`../workflows/${name}.yml`, import.meta.url),
      "utf8",
    );
    assert.doesNotMatch(text, /setup-qemu-action/);
  }
  const release = await workflow("release-suite");
  assert.equal(release.jobs.build.uses, "./.github/workflows/native-image.yml");
  assert.equal(release.jobs.build.with.mode, "release");
  assert.ok(release.jobs.record.needs.includes("build"));
  assert.ok(release.jobs.finalize.needs.includes("record"));
  const recordedScan = release.jobs.record.steps.find((s) =>
    s.name?.startsWith("Rescan the recorded candidate"),
  );
  assert.equal(
    recordedScan.env.CANDIDATE_REF,
    "${{ steps.record.outputs.ref }}",
  );
  assert.match(recordedScan.run, /--platform "linux\/\$arch"/);
  assert.match(recordedScan.run, /--severity HIGH,CRITICAL/);
  assert.match(recordedScan.run, /--exit-code 1/);
});
