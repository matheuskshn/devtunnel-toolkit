import assert from "node:assert/strict";
import test from "node:test";
import { execFileSync } from "node:child_process";
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  selectImages,
  changedFiles,
  selectForEvent,
} from "./image-changes.mjs";

test("each build input selects only its own image", () => {
  for (const [file, image] of [
    ["Dockerfile", "devtunnel-toolkit"],
    ["docker/devtunnel-entrypoint", "devtunnel-toolkit"],
    ["images/squid/squid.conf.template", "devtunnel-toolkit-squid"],
    ["images/tinyproxy/tinyproxy-entrypoint", "devtunnel-toolkit-route-proxy"],
    ["images/openvpn/openvpn-entrypoint", "devtunnel-toolkit-openvpn"],
  ]) {
    const result = selectImages([file]);
    assert.deepEqual(
      result.matrix.include.map((i) => i.image),
      [image],
    );
    assert.equal(result.hub, false);
  }
});
test("shared runtime inputs select all consumers, native libraries select toolkit and Hub only", () => {
  for (const file of [".dockerignore", "images/hub/bin/use-gnu-coreutils"]) {
    const result = selectImages([file]);
    assert.equal(result.matrix.include.length, 4);
    assert.equal(result.hub, file.startsWith("images/hub/"));
    assert.ok(result.matrix.include.every((i) => i.context === "."));
  }
  for (const file of [
    "build-native-libraries",
    "build-mime-backport",
    "native-mime-regression.c",
    "package-native-libraries.mjs",
    "native-metadata.mjs",
    "p11-kit-module-soname.patch",
  ]) {
    const result = selectImages([`images/hub/bin/${file}`]);
    assert.equal(result.hub, true);
    assert.deepEqual(
      result.matrix.include.map((i) => i.image),
      ["devtunnel-toolkit"],
    );
  }
});
test("Hub source, dependencies and build definition never rebuild legacy images", () => {
  for (const file of [
    "src/config.ts",
    "bin/hub",
    "Dockerfile",
    ".dockerignore",
    "package.json",
    "package-lock.json",
    "tsconfig.json",
  ]) {
    const result = selectImages([`images/hub/${file}`]);
    assert.equal(result.hub, true);
    assert.equal(result.legacy, false);
  }
});
test("documentation, examples, tests and workflow edits do not select images", () => {
  const result = selectImages([
    "README.md",
    "compose.yml",
    "Makefile",
    "images/hub/README.md",
    "images/hub/.env.example",
    "images/hub/examples/aca/containerapp.yaml",
    "images/hub/tests/core.test.mjs",
    ".github/workflows/docker.yml",
    ".github/workflows/hub.yml",
    ".github/scripts/image-changes.mjs",
  ]);
  assert.deepEqual(result, {
    matrix: { include: [] },
    legacy: false,
    hub: false,
  });
});
test("multiple image changes produce only the affected matrix entries", () => {
  const result = selectImages([
    "images/squid/Dockerfile",
    "images/openvpn/Dockerfile",
    "images/hub/src/auth.ts",
  ]);
  assert.deepEqual(
    result.matrix.include.map((i) => i.image),
    ["devtunnel-toolkit-squid", "devtunnel-toolkit-openvpn"],
  );
  assert.equal(result.hub, true);
});

function repository(t) {
  const cwd = mkdtempSync(path.join(os.tmpdir(), "image-changes-"));
  t.after(() => rmSync(cwd, { recursive: true, force: true }));
  const git = (...args) =>
    execFileSync("git", args, {
      cwd,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();
  git("init", "-b", "main");
  git("config", "user.name", "test-user");
  git("config", "user.email", "test-user@example.com");
  const commit = (file, content = "test") => {
    mkdirSync(path.dirname(path.join(cwd, file)), { recursive: true });
    writeFileSync(path.join(cwd, file), content);
    git("add", "--", file);
    git("-c", "commit.gpgsign=false", "commit", "-m", "fixture");
    return git("rev-parse", "HEAD");
  };
  return { cwd, git, commit };
}
test("push compares the entire pushed range and handles the initial tree", (t) => {
  const { cwd, commit } = repository(t);
  const base = commit("README.md");
  commit("images/squid/Dockerfile");
  commit("images/hub/src/config.ts");
  assert.deepEqual(
    changedFiles({ before: base }, "push", "refs/heads/main", cwd),
    ["images/hub/src/config.ts", "images/squid/Dockerfile"],
  );
  assert.ok(
    changedFiles(
      { before: "0".repeat(40) },
      "push",
      "refs/heads/main",
      cwd,
    ).includes("README.md"),
  );
});
test("PR comparison uses merge base and does not include unrelated base-branch changes", (t) => {
  const { cwd, git, commit } = repository(t);
  commit("README.md");
  git("checkout", "-b", "feature");
  const head = commit("images/squid/Dockerfile");
  git("checkout", "main");
  const base = commit("images/openvpn/Dockerfile");
  assert.deepEqual(
    changedFiles(
      { pull_request: { base: { sha: base }, head: { sha: head } } },
      "pull_request",
      "refs/pull/1/merge",
      cwd,
    ),
    ["images/squid/Dockerfile"],
  );
});
test("version tags and manual runs force every image without requiring a diff", () => {
  for (const [event, ref] of [
    ["push", "refs/tags/v1.0.0"],
    ["workflow_dispatch", "refs/heads/main"],
    ["workflow_dispatch", "refs/heads/feature"],
  ]) {
    const result = selectForEvent({}, event, ref, "/nonexistent");
    assert.equal(result.hub, true);
    assert.equal(result.legacy, true);
    assert.equal(result.matrix.include.length, 4);
  }
  assert.throws(
    () => selectForEvent({}, "unknown", "refs/heads/main"),
    /Unsupported/,
  );
});
test("documentation-only pushes skip builds; renames include removed inputs", (t) => {
  const { cwd, git, commit } = repository(t);
  const first = commit("images/squid/Dockerfile");
  const base = commit("README.md");
  assert.equal(
    selectForEvent({ before: first }, "push", "refs/heads/main", cwd).legacy,
    false,
  );
  git("mv", "images/squid/Dockerfile", "images/squid/old-definition");
  git("-c", "commit.gpgsign=false", "commit", "-m", "rename fixture");
  const files = changedFiles({ before: base }, "push", "refs/heads/main", cwd);
  assert.ok(files.includes("images/squid/Dockerfile"));
  assert.equal(selectImages(files).legacy, true);
  assert.throws(
    () => changedFiles({ before: "--invalid" }, "push", "refs/heads/main", cwd),
    /Invalid comparison/,
  );
});
