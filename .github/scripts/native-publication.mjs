import { execFileSync } from "node:child_process";
import { appendFileSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import {
  checkVersions,
  suiteImages,
  immutableTag,
  validateAttestation,
} from "./release-policy.mjs";
import {
  candidate,
  registryDigest,
  repositories,
  verifyImage,
} from "./release.mjs";
import { trustedExecutable } from "./trusted-executable.mjs";

const run = (command, args) =>
  execFileSync(trustedExecutable(command), args, {
    encoding: "utf8",
    maxBuffer: 32 * 1024 * 1024,
    stdio: ["ignore", "pipe", "pipe"],
  });
const output = (key, value) =>
  appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${value}\n`);

export function publicationPlan({
  image,
  mode,
  sha,
  runId,
  attempt,
  version,
  owner,
}) {
  const selected = suiteImages.find((item) => item.image === image);
  if (!selected) throw new Error("UNKNOWN_PUBLICATION_IMAGE");
  if (!["edge", "release"].includes(mode))
    throw new Error("INVALID_PUBLICATION_MODE");
  if (
    !/^[a-f0-9]{40}$/.test(sha) ||
    !/^\d+$/.test(runId ?? "") ||
    !/^\d+$/.test(attempt ?? "")
  )
    throw new Error("INVALID_PUBLICATION_IDENTITY");
  if (!/^[a-z0-9][a-z0-9-]*$/.test(owner ?? ""))
    throw new Error("INVALID_REGISTRY_OWNER");
  const unique = `${sha}-${runId}-${attempt}`;
  return {
    ...selected,
    mode,
    sha,
    version,
    repository: `ghcr.io/${owner}/${image}`,
    unique,
  };
}

export function digestSources(records, repository) {
  if (records.length !== 2) throw new Error("NATIVE_DIGEST_COUNT");
  return ["amd64", "arm64"].map((arch) => {
    const entries = records.filter((item) => item.arch === arch);
    if (
      entries.length !== 1 ||
      !/^sha256:[a-f0-9]{64}$/.test(entries[0].digest ?? "")
    )
      throw new Error("INVALID_NATIVE_DIGEST");
    return `${repository}@${entries[0].digest}`;
  });
}

// All immutable destinations are checked before any tag or alias is moved.
export async function promoteNative({
  source,
  digest,
  targets,
  aliases,
  inspect,
  copy,
}) {
  for (const target of targets) immutableTag(await inspect(target), digest);
  for (const target of targets) {
    if (immutableTag(await inspect(target), digest)) await copy(source, target);
    if ((await inspect(target)) !== digest)
      throw new Error("NATIVE_DIGEST_MISMATCH");
  }
  for (const target of aliases) {
    await copy(source, target);
    if ((await inspect(target)) !== digest)
      throw new Error("NATIVE_ALIAS_MISMATCH");
  }
}

async function main() {
  const mode = process.env.PUBLICATION_MODE;
  const info = checkVersions(
    process.cwd(),
    mode === "release" ? process.env.RELEASE_TAG : undefined,
  );
  const plan = publicationPlan({
    image: process.env.PUBLICATION_IMAGE,
    mode,
    sha: run("git", ["rev-parse", "HEAD"]).trim(),
    runId: process.env.GITHUB_RUN_ID,
    attempt: process.env.GITHUB_RUN_ATTEMPT,
    version: info.version,
    owner: process.env.GITHUB_REPOSITORY_OWNER?.toLowerCase(),
  });
  if (mode === "release")
    run("git", ["merge-base", "--is-ancestor", plan.sha, "origin/main"]);
  if (process.argv[2] === "plan") {
    for (const key of ["context", "dockerfile", "sha", "version", "repository"])
      output(key, plan[key]);
    const built =
      mode === "release"
        ? candidate(plan.image, plan.version, plan.sha)
        : undefined;
    output("exists", Boolean(built?.digest));
    return;
  }
  if (process.argv[2] !== "merge")
    throw new Error("INVALID_PUBLICATION_COMMAND");
  // Defense in depth: PR jobs can build, but must never publish even if misconfigured.
  if (
    ["pull_request", "pull_request_target"].includes(
      process.env.GITHUB_EVENT_NAME,
    )
  )
    throw new Error("PR_PUBLICATION_FORBIDDEN");
  const records = ["amd64", "arm64"].map((arch) =>
    JSON.parse(readFileSync(`native-digests/${arch}.json`, "utf8")),
  );
  const sources = digestSources(records, plan.repository);
  const staging = `${plan.repository}:native-${plan.unique}`;
  run("docker", [
    "buildx",
    "imagetools",
    "create",
    "--tag",
    staging,
    ...sources,
  ]);
  const digest = registryDigest(staging);
  if (!digest) throw new Error("NATIVE_MANIFEST_MISSING");
  const source = `${plan.repository}@${digest}`;
  verifyImage(source, plan.version, plan.sha);
  for (const [kind, property] of [
    ["sbom", "SBOM"],
    ["provenance", "Provenance"],
  ]) {
    const data = JSON.parse(
      run("docker", [
        "buildx",
        "imagetools",
        "inspect",
        source,
        "--format",
        `{{json .${property}}}`,
      ]),
    );
    validateAttestation(data, kind);
  }
  const repos = repositories(plan.image);
  const targets =
    mode === "release"
      ? [`${plan.repository}:candidate-${plan.version}`]
      : repos.map((repo) => `${repo}:sha-${plan.unique}`);
  const aliases =
    mode === "edge" && process.env.GITHUB_REF === "refs/heads/main"
      ? repos.flatMap((repo) => ["edge", "main"].map((tag) => `${repo}:${tag}`))
      : [];
  await promoteNative({
    source,
    digest,
    targets,
    aliases,
    inspect: registryDigest,
    copy: (from, to) =>
      run("skopeo", [
        "copy",
        "--authfile",
        path.join(homedir(), ".docker/config.json"),
        "--all",
        "--preserve-digests",
        `docker://${from}`,
        `docker://${to}`,
      ]),
  });
  console.log(
    `Verified native AMD64/ARM64 publication: ${plan.image} (${digest})`,
  );
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
