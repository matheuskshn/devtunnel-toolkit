import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { desktopRids, validateDesktopArtifacts } from "./desktop-release.mjs";

const version = "0.2.0-rc.1",
  sha = "a".repeat(40);
function fixture(t) {
  const directory = mkdtempSync(path.join(tmpdir(), "desktop-release-test-"));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const members = new Map();
  for (const rid of desktopRids) {
    const stem = `devtunnel-toolkit-desktop-${version}-${rid}`;
    const archiveName = `${stem}.${rid.startsWith("win-") ? "zip" : "tar.gz"}`;
    const archive = path.join(directory, archiveName);
    const contents = Buffer.from(`public test artifact ${rid}`);
    writeFileSync(archive, contents);
    writeFileSync(
      `${archive}.sha256`,
      `${createHash("sha256").update(contents).digest("hex")}  ${archiveName}\n`,
    );
    const build = Buffer.from(
      JSON.stringify({
        schemaVersion: 1,
        product: "DevTunnel Toolkit Desktop",
        version,
        runtimeIdentifier: rid,
        sourceRevision: sha,
        sourceWorktreeDirty: false,
        nativeCliAgentSmoke: rid.endsWith("-x64"),
        dependencies: [{ name: "example/1.0.0" }],
      }),
    );
    const sbom = Buffer.from(
      JSON.stringify({
        spdxVersion: "SPDX-2.2",
        SPDXID: "SPDXRef-DOCUMENT",
        dataLicense: "CC0-1.0",
        documentNamespace:
          "https://github.com/matheuskshn/devtunnel-toolkit/test",
        files: [{ fileName: "./public.dll" }],
        packages: [
          {
            SPDXID: "SPDXRef-RootPackage",
            name: "DevTunnel Toolkit Desktop",
            versionInfo: version,
          },
        ],
      }),
    );
    if (rid.startsWith("win-")) {
      const installerName = `${stem}-setup.exe`;
      const installer = Buffer.from(`MZ installer ${rid}`);
      writeFileSync(path.join(directory, installerName), installer);
      writeFileSync(
        path.join(directory, `${installerName}.sha256`),
        `${createHash("sha256").update(installer).digest("hex")}  ${installerName}\n`,
      );
    }
    writeFileSync(path.join(directory, `${stem}.build.json`), build);
    writeFileSync(path.join(directory, `${stem}.spdx.json`), sbom);
    members.set(`${archive}/build-manifest.json`, build);
    members.set(`${archive}/_manifest/spdx_2.2/manifest.spdx.json`, sbom);
  }
  return {
    directory,
    readMember: (archive, member) => members.get(`${archive}/${member}`),
    members,
  };
}
test("desktop release requires all four architectures and returns only verified files", (t) => {
  const f = fixture(t);
  const result = validateDesktopArtifacts(
    f.directory,
    version,
    sha,
    f.readMember,
  );
  assert.equal(result.files.length, 20);
  assert.deepEqual(
    result.records.filter((record) => record.installer).map((record) => record.runtimeIdentifier),
    ["win-x64", "win-arm64"],
  );
  assert.equal(result.records.length, 4);
  assert.deepEqual(
    result.records
      .filter((record) => record.nativeCliAgentSmoke)
      .map((record) => record.runtimeIdentifier),
    ["linux-x64", "win-x64"],
  );
});
test("missing architecture or a corrupt archive prevents release", (t) => {
  const missing = fixture(t);
  rmSync(
    path.join(
      missing.directory,
      `devtunnel-toolkit-desktop-${version}-win-arm64.zip`,
    ),
  );
  assert.throws(
    () =>
      validateDesktopArtifacts(
        missing.directory,
        version,
        sha,
        missing.readMember,
      ),
    /ARTIFACT_MISSING/,
  );
  const corrupt = fixture(t);
  writeFileSync(
    path.join(
      corrupt.directory,
      `devtunnel-toolkit-desktop-${version}-linux-x64.tar.gz`,
    ),
    "changed",
  );
  assert.throws(
    () =>
      validateDesktopArtifacts(
        corrupt.directory,
        version,
        sha,
        corrupt.readMember,
      ),
    /DIGEST_MISMATCH/,
  );
});
test("missing or corrupted installers prevent release", (t) => {
  const missing = fixture(t);
  rmSync(path.join(missing.directory, `devtunnel-toolkit-desktop-${version}-win-x64-setup.exe`));
  assert.throws(
    () => validateDesktopArtifacts(missing.directory, version, sha, missing.readMember),
    /ARTIFACT_MISSING/,
  );
  const corrupted = fixture(t);
  writeFileSync(path.join(corrupted.directory, `devtunnel-toolkit-desktop-${version}-win-arm64-setup.exe`), "MZ changed");
  assert.throws(
    () => validateDesktopArtifacts(corrupted.directory, version, sha, corrupted.readMember),
    /INSTALLER_DIGEST_MISMATCH/,
  );
});
test("dirty trees, mismatched revisions, versions and missing native smokes block promotion", (t) => {
  for (const mutation of [
    (manifest) => (manifest.sourceWorktreeDirty = true),
    (manifest) => (manifest.sourceRevision = "b".repeat(40)),
    (manifest) => (manifest.version = "9.0.0"),
    (manifest) => (manifest.nativeCliAgentSmoke = false),
  ]) {
    const f = fixture(t);
    const filename = path.join(
      f.directory,
      `devtunnel-toolkit-desktop-${version}-linux-x64.build.json`,
    );
    const build = JSON.parse(readFileSync(filename));
    mutation(build);
    writeFileSync(filename, JSON.stringify(build));
    assert.throws(
      () => validateDesktopArtifacts(f.directory, version, sha, f.readMember),
      /MANIFEST_MISMATCH|SMOKE_REQUIRED/,
    );
  }
});
test("SBOM must match the release and metadata embedded in the archive", (t) => {
  const invalid = fixture(t);
  const filename = path.join(
    invalid.directory,
    `devtunnel-toolkit-desktop-${version}-linux-x64.spdx.json`,
  );
  const sbom = JSON.parse(readFileSync(filename));
  sbom.packages[0].versionInfo = "9.0.0";
  writeFileSync(filename, JSON.stringify(sbom));
  assert.throws(
    () =>
      validateDesktopArtifacts(
        invalid.directory,
        version,
        sha,
        invalid.readMember,
      ),
    /SBOM_INVALID/,
  );
  const mismatch = fixture(t);
  assert.throws(
    () =>
      validateDesktopArtifacts(mismatch.directory, version, sha, () =>
        Buffer.from("other archive"),
      ),
    /EMBEDDED_METADATA/,
  );
});
test("unsafe SBOM paths, extra files and symlinks cannot enter release attachments", (t) => {
  const unsafe = fixture(t);
  const filename = path.join(
    unsafe.directory,
    `devtunnel-toolkit-desktop-${version}-linux-x64.spdx.json`,
  );
  const sbom = JSON.parse(readFileSync(filename));
  sbom.files[0].fileName = "./../private.txt";
  writeFileSync(filename, JSON.stringify(sbom));
  assert.throws(
    () =>
      validateDesktopArtifacts(
        unsafe.directory,
        version,
        sha,
        unsafe.readMember,
      ),
    /UNSAFE_PATH/,
  );
  const extra = fixture(t);
  writeFileSync(path.join(extra.directory, ".env"), "unexpected");
  assert.throws(
    () =>
      validateDesktopArtifacts(extra.directory, version, sha, extra.readMember),
    /UNEXPECTED_ARTIFACT/,
  );
  if (process.platform !== "win32") {
    const link = fixture(t);
    const target = path.join(
      link.directory,
      `devtunnel-toolkit-desktop-${version}-linux-x64.tar.gz`,
    );
    rmSync(target);
    symlinkSync(
      path.join(
        link.directory,
        `devtunnel-toolkit-desktop-${version}-win-x64.zip`,
      ),
      target,
    );
    assert.throws(
      () =>
        validateDesktopArtifacts(link.directory, version, sha, link.readMember),
      /ARTIFACT_INVALID/,
    );
  }
});
