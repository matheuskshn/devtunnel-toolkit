import { execFileSync } from "node:child_process";
import { appendFileSync, readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { trustedExecutable } from "./trusted-executable.mjs";

const images = [
  {
    title: "DevTunnel Toolkit",
    image: "devtunnel-toolkit",
    dockerfile: "Dockerfile",
    context: ".",
    description: "Developer toolkit for Microsoft Dev Tunnels",
    files: [
      "Dockerfile",
      ".dockerignore",
      "docker/devtunnel-entrypoint",
      ...[
        "build-native-libraries",
        "build-mime-backport",
        "native-mime-regression.c",
        "package-native-libraries.mjs",
        "native-metadata.mjs",
        "p11-kit-module-soname.patch",
      ].map((f) => `images/hub/bin/${f}`),
    ],
  },
  {
    title: "Squid proxy",
    image: "devtunnel-toolkit-squid",
    dockerfile: "images/squid/Dockerfile",
    context: ".",
    description: "Squid proxy for DevTunnel Toolkit",
    files: [
      "images/squid/Dockerfile",
      "images/squid/.dockerignore",
      "images/squid/squid-entrypoint",
      "images/squid/squid.conf.template",
    ],
  },
  {
    title: "Selective route proxy",
    image: "devtunnel-toolkit-route-proxy",
    dockerfile: "images/tinyproxy/Dockerfile",
    context: ".",
    description: "Selective local route proxy for DevTunnel Toolkit clients",
    files: [
      "images/tinyproxy/Dockerfile",
      "images/tinyproxy/.dockerignore",
      "images/tinyproxy/tinyproxy-entrypoint",
      "images/tinyproxy/tinyproxy.conf.template",
    ],
  },
  {
    title: "OpenVPN server",
    image: "devtunnel-toolkit-openvpn",
    dockerfile: "images/openvpn/Dockerfile",
    context: ".",
    description: "OpenVPN server for DevTunnel Toolkit",
    files: [
      "images/openvpn/Dockerfile",
      "images/openvpn/.dockerignore",
      "images/openvpn/openvpn-entrypoint",
    ],
  },
];
const hubFiles = new Set(
  [
    "Dockerfile",
    ".dockerignore",
    "package.json",
    "package-lock.json",
    "tsconfig.json",
  ].map((f) => `images/hub/${f}`),
);
export function selectImages(files, force = false) {
  const include = images
    .filter(
      (image) =>
        force ||
        files.includes(".dockerignore") ||
        files.includes("images/hub/bin/use-gnu-coreutils") ||
        image.files.some((f) => files.includes(f)),
    )
    .map(({ files, ...image }) => image);
  return {
    matrix: { include },
    legacy: include.length > 0,
    hub:
      force ||
      files.some(
        (f) =>
          hubFiles.has(f) ||
          f.startsWith("images/hub/src/") ||
          f.startsWith("images/hub/bin/") ||
          f.startsWith("images/hub/web/"),
      ),
  };
}
export function changedFiles(event, eventName, ref, cwd = process.cwd()) {
  const executable = trustedExecutable("git");
  const git = (args) =>
    execFileSync(executable, args, {
      cwd,
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
    }).trim();
  const sha = (value) => {
    if (!/^[0-9a-f]{40,64}$/.test(value ?? ""))
      throw new Error("Invalid comparison commit");
    return value;
  };
  const head = git(["rev-parse", "HEAD"]);
  let base;
  let target = head;
  if (eventName === "pull_request") {
    target = sha(event.pull_request.head.sha);
    base = git(["merge-base", sha(event.pull_request.base.sha), target]);
  } else if (
    eventName === "push" &&
    event.before &&
    !/^0+$/.test(event.before)
  ) {
    base = sha(event.before);
  }
  // First commit or newly created branch: inspect the full tree.
  if (!base)
    return git(["ls-tree", "-r", "--name-only", "-z", target])
      .split("\0")
      .filter(Boolean);
  // Disable rename detection so both the removed and new build inputs are selected.
  return git(["diff", "--name-only", "--no-renames", "-z", base, target, "--"])
    .split("\0")
    .filter(Boolean);
}
export function selectForEvent(event, eventName, ref, cwd = process.cwd()) {
  if (
    eventName === "workflow_dispatch" ||
    (eventName === "push" && ref.startsWith("refs/tags/"))
  )
    return selectImages([], true);
  if (!["push", "pull_request"].includes(eventName))
    throw new Error("Unsupported selection event");
  return selectImages(changedFiles(event, eventName, ref, cwd));
}
if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, "utf8"));
  const result = selectForEvent(
    event,
    process.env.GITHUB_EVENT_NAME,
    process.env.GITHUB_REF,
  );
  for (const [key, value] of Object.entries(result))
    appendFileSync(
      process.env.GITHUB_OUTPUT,
      `${key}=${JSON.stringify(value)}\n`,
    );
  console.log(
    `Selected images: ${[...result.matrix.include.map((i) => i.image), ...(result.hub ? ["devtunnel-toolkit-hub"] : [])].join(", ") || "none"}`,
  );
}
