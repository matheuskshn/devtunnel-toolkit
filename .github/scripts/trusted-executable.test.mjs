import assert from "node:assert/strict";
import test from "node:test";
import { execFileSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { trustedExecutable } from "./trusted-executable.mjs";
import { changedFiles } from "./image-changes.mjs";
import { validatePlatforms } from "./release-policy.mjs";

test("automation ignores a writable PATH and invokes only system-managed Git", (t) => {
  const directory = mkdtempSync(path.join(os.tmpdir(), "toolkit-path-"));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const git = trustedExecutable("git");
  assert.ok(path.isAbsolute(git));
  execFileSync(git, ["init", "-b", "main"], {
    cwd: directory,
    stdio: "ignore",
  });
  writeFileSync(path.join(directory, "README.md"), "Synthetic fixture\n");
  execFileSync(git, ["add", "README.md"], { cwd: directory });
  execFileSync(
    git,
    [
      "-c",
      "commit.gpgsign=false",
      "-c",
      "user.name=test-user",
      "-c",
      "user.email=test-user@example.com",
      "commit",
      "-m",
      "fixture",
    ],
    { cwd: directory, stdio: "ignore" },
  );
  writeFileSync(path.join(directory, "git"), "#!/bin/sh\nexit 91\n", {
    mode: 0o755,
  });
  const previous = process.env.PATH;
  try {
    process.env.PATH = directory;
    assert.equal(trustedExecutable("git"), git);
    assert.deepEqual(
      changedFiles(
        { before: "0".repeat(40) },
        "push",
        "refs/heads/main",
        directory,
      ),
      ["README.md"],
    );
    assert.throws(() => trustedExecutable("../git"), /UNSUPPORTED/);
    assert.throws(
      () => trustedExecutable(path.join(directory, "git")),
      /UNSUPPORTED/,
    );
  } finally {
    if (previous === undefined) delete process.env.PATH;
    else process.env.PATH = previous;
  }
});

test("release platforms are exactly two allowed strings, without implicit coercion or sorting", () => {
  for (const platforms of [
    ["linux/amd64", "linux/arm64"],
    ["linux/arm64", "linux/amd64"],
  ]) {
    assert.equal(validatePlatforms(platforms), platforms);
  }
  for (const platforms of [
    undefined,
    null,
    "linux/amd64,linux/arm64",
    [],
    ["linux/amd64"],
    ["linux/amd64", "linux/amd64"],
    ["linux/amd64", "linux/arm64", "linux/386"],
    ["linux/amd64", { toString: () => "linux/arm64" }],
    [1, 2],
  ]) {
    assert.throws(
      () => validatePlatforms(platforms),
      /INCOMPLETE_ARCHITECTURES/,
    );
  }
});
