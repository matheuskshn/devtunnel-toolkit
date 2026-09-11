import { readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { selectImages } from "./image-changes.mjs";

export const suiteImages = [
  ...selectImages([], true).matrix.include,
  {
    image: "devtunnel-toolkit-hub",
    title: "Hub",
    context: "images/hub",
    dockerfile: "images/hub/Dockerfile",
  },
];
const number = "(?:0|[1-9][0-9]*)";
const identifier = "(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)";
const versionPattern = new RegExp(
  String.raw`^(${number})\.(${number})\.(${number})(?:-(${identifier}(?:\.${identifier})*))?$`,
);
export function versionInfo(version) {
  const match =
    typeof version === "string" &&
    version.length <= 80 &&
    versionPattern.exec(version);
  if (!match || match[0] !== version)
    throw new Error("INVALID_RELEASE_VERSION");
  return {
    version,
    tag: `v${version}`,
    prerelease: !!match[4],
    major: match[1],
    minor: match[2],
    patch: match[3],
  };
}
export function checkVersions(root, tag) {
  if (root === undefined) root = process.cwd();
  const version = readFileSync(path.join(root, "version.txt"), "utf8").trim();
  const info = versionInfo(version);
  const pkg = JSON.parse(
    readFileSync(path.join(root, "images/hub/package.json"), "utf8"),
  );
  const lock = JSON.parse(
    readFileSync(path.join(root, "images/hub/package-lock.json"), "utf8"),
  );
  const manifest = JSON.parse(
    readFileSync(path.join(root, ".release-please-manifest.json"), "utf8"),
  );
  if (
    pkg.version !== version ||
    lock.version !== version ||
    lock.packages[""].version !== version ||
    (manifest["."] !== undefined && manifest["."] !== version) ||
    (tag !== undefined && tag !== info.tag)
  )
    throw new Error("RELEASE_VERSION_MISMATCH");
  return info;
}
export function digest(value) {
  if (!/^sha256:[a-f0-9]{64}$/.test(value ?? ""))
    throw new Error("INVALID_IMAGE_DIGEST");
  return value;
}
export function immutableTag(existing, wanted) {
  digest(wanted);
  if (existing !== undefined && existing !== wanted)
    throw new Error("IMMUTABLE_TAG_CONFLICT");
  return existing === undefined;
}
export function releaseAliases(version) {
  const v = versionInfo(version);
  return v.prerelease
    ? []
    : [
        `${v.major}.${v.minor}`,
        ...(v.major === "0" ? [] : [v.major]),
        "latest",
      ];
}
export function validateAttestation(data, kind) {
  let field;
  if (kind === "sbom") field = "SPDX";
  if (kind === "provenance") field = "SLSA";
  if (!field) throw new Error("UNKNOWN_ATTESTATION_TYPE");
  const result = {};
  for (const platform of ["linux/amd64", "linux/arm64"]) {
    const value =
      data?.[platform] ??
      (platform === "linux/arm64" ? data?.["linux/arm64/v8"] : undefined);
    if (
      !value?.[field] ||
      typeof value[field] !== "object" ||
      !Object.keys(value[field]).length
    )
      throw new Error("ATTESTATION_MISSING");
    result[platform] = value;
  }
  return result;
}
export function validateManifest(records, version, sha) {
  versionInfo(version);
  if (!/^[a-f0-9]{40}$/.test(sha ?? "")) throw new Error("INVALID_RELEASE_SHA");
  if (
    records.length !== suiteImages.length ||
    new Set(records.map((r) => r.image)).size !== suiteImages.length
  )
    throw new Error("INCOMPLETE_SUITE");
  for (const image of suiteImages) {
    const record = records.find((r) => r.image === image.image);
    if (!record || record.version !== version || record.sha !== sha)
      throw new Error("RELEASE_IMAGE_MISMATCH");
    digest(record.digest);
    validatePlatforms(record.platforms);
  }
  return records;
}
export function validatePlatforms(platforms) {
  if (
    !Array.isArray(platforms) ||
    platforms.length !== 2 ||
    !platforms.every((platform) => typeof platform === "string") ||
    !platforms.includes("linux/amd64") ||
    !platforms.includes("linux/arm64")
  ) {
    throw new Error("INCOMPLETE_ARCHITECTURES");
  }
  return platforms;
}
if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  console.log(`PASS suite version ${checkVersions().version}`);
}
