import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { lstatSync, readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { trustedExecutable } from "./trusted-executable.mjs";

export const desktopRids = ["linux-x64", "linux-arm64", "win-x64", "win-arm64"];

export function readDesktopArchiveFile(archive, member) {
  const windows = archive.endsWith(".zip");
  return execFileSync(
    trustedExecutable(windows ? "unzip" : "tar"),
    windows ? ["-p", archive, `./${member}`] : ["-xOf", archive, `./${member}`],
    {
      maxBuffer: 32 * 1024 * 1024,
      timeout: 30000,
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
}

export function validateDesktopArtifacts(
  directory,
  version,
  sha,
  readArchiveFile = readDesktopArchiveFile,
) {
  if (
    !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version) ||
    !/^[0-9a-f]{40}$/.test(sha)
  )
    throw new Error("DESKTOP_RELEASE_ID_INVALID");
  const files = [];
  const records = [];
  const expectedNames = new Set();
  for (const rid of desktopRids) {
    const stem = `devtunnel-toolkit-desktop-${version}-${rid}`;
    const archiveName = `${stem}.${rid.startsWith("win-") ? "zip" : "tar.gz"}`;
    const installerName = rid.startsWith("win-") ? `${stem}-setup.exe` : null;
    const names = [
      archiveName,
      `${archiveName}.sha256`,
      `${stem}.build.json`,
      `${stem}.spdx.json`,
      ...(installerName ? [installerName, `${installerName}.sha256`] : []),
    ];
    for (const name of names) {
      expectedNames.add(name);
      const file = path.join(directory, name);
      let stat;
      try {
        stat = lstatSync(file);
      } catch {
        throw new Error("DESKTOP_ARTIFACT_MISSING");
      }
      if (
        !stat.isFile() ||
        stat.isSymbolicLink() ||
        stat.size === 0 ||
        stat.size > 1024 * 1024 * 1024
      )
        throw new Error("DESKTOP_ARTIFACT_INVALID");
      files.push(file);
    }
    const archive = path.join(directory, archiveName);
    const checksum = readFileSync(`${archive}.sha256`, "utf8");
    const digest = createHash("sha256")
      .update(readFileSync(archive))
      .digest("hex");
    if (checksum !== `${digest}  ${archiveName}\n`)
      throw new Error("DESKTOP_DIGEST_MISMATCH");
    let installerDigest = null;
    if (installerName) {
      const installer = readFileSync(path.join(directory, installerName));
      if (installer.length < 2 || installer.toString("ascii", 0, 2) !== "MZ")
        throw new Error("DESKTOP_INSTALLER_INVALID");
      const digest = createHash("sha256").update(installer).digest("hex");
      if (
        readFileSync(path.join(directory, `${installerName}.sha256`), "utf8") !==
        `${digest}  ${installerName}\n`
      )
        throw new Error("DESKTOP_INSTALLER_DIGEST_MISMATCH");
      installerDigest = `sha256:${digest}`;
    }
    const buildBytes = readFileSync(path.join(directory, `${stem}.build.json`));
    const build = JSON.parse(buildBytes);
    if (
      build.schemaVersion !== 1 ||
      build.product !== "DevTunnel Toolkit Desktop" ||
      build.version !== version ||
      build.runtimeIdentifier !== rid ||
      build.sourceRevision !== sha ||
      build.sourceWorktreeDirty !== false ||
      !Array.isArray(build.dependencies) ||
      build.dependencies.length === 0
    )
      throw new Error("DESKTOP_BUILD_MANIFEST_MISMATCH");
    if (rid.endsWith("-x64") && build.nativeCliAgentSmoke !== true)
      throw new Error("DESKTOP_NATIVE_SMOKE_REQUIRED");
    const sbomBytes = readFileSync(path.join(directory, `${stem}.spdx.json`));
    const sbom = JSON.parse(sbomBytes);
    const rootPackage = sbom.packages?.find(
      (pkg) => pkg.SPDXID === "SPDXRef-RootPackage",
    );
    if (
      sbom.spdxVersion !== "SPDX-2.2" ||
      sbom.SPDXID !== "SPDXRef-DOCUMENT" ||
      sbom.dataLicense !== "CC0-1.0" ||
      !sbom.documentNamespace?.startsWith(
        "https://github.com/matheuskshn/devtunnel-toolkit/",
      ) ||
      rootPackage?.name !== "DevTunnel Toolkit Desktop" ||
      rootPackage.versionInfo !== version ||
      !Array.isArray(sbom.files) ||
      sbom.files.length === 0
    )
      throw new Error("DESKTOP_SBOM_INVALID");
    for (const entry of sbom.files) {
      const filename = entry.fileName?.replaceAll("\\", "/");
      if (
        !filename?.startsWith("./") ||
        filename
          .split("/")
          .some((part) => ["..", ".local", ".git"].includes(part))
      )
        throw new Error("DESKTOP_SBOM_UNSAFE_PATH");
    }
    if (
      !buildBytes.equals(readArchiveFile(archive, "build-manifest.json")) ||
      !sbomBytes.equals(
        readArchiveFile(archive, "_manifest/spdx_2.2/manifest.spdx.json"),
      )
    )
      throw new Error("DESKTOP_EMBEDDED_METADATA_MISMATCH");
    records.push({
      runtimeIdentifier: rid,
      version,
      sha,
      archive: archiveName,
      digest: `sha256:${digest}`,
      installer: installerName,
      installerDigest,
      nativeCliAgentSmoke: build.nativeCliAgentSmoke === true,
    });
  }
  if (readdirSync(directory).some((name) => !expectedNames.has(name)))
    throw new Error("DESKTOP_UNEXPECTED_ARTIFACT");
  return { files: files.sort(), records };
}
