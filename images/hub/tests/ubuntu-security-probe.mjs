// Synthetic, read-only evidence from the exact running Ubuntu candidate.
// Does not interpret absent tools or hardening as patches for installed packages.
import assert from "node:assert/strict";
import fs from "node:fs";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import os from "node:os";
import path from "node:path";

const run = (file, args) =>
  execFileSync(file, args, { encoding: "utf8" }).trim();
const root = "/usr/local/share/devtunnel/native/evidence";
const expectedUid = Number(process.env.HUB_TEST_EXPECTED_UID ?? 1000);
assert.ok([1000, 10001].includes(expectedUid));
const cliEvidence = JSON.parse(fs.readFileSync("/opt/devtunnel/evidence/packaging.json"));
assert.equal(cliEvidence.deployment, "framework-dependent");
assert.equal(cliEvidence.installedRuntime, "10.0.14");
for (const file of cliEvidence.applicationFiles) {
  assert.equal(createHash("sha256").update(fs.readFileSync(`/opt/devtunnel/cli/${file.name}`)).digest("hex"), file.sha256);
}
for (const file of cliEvidence.excludedRuntimeFiles) {
  assert.equal(fs.existsSync(`/opt/devtunnel/cli/${file.name}`), false);
}
const cliHome = fs.mkdtempSync(path.join(os.tmpdir(), "cli-runtime-proof-"));
try {
  for (const directory of [".config", ".local/share", ".cache"]) fs.mkdirSync(path.join(cliHome, directory), { recursive: true });
  const traceFile = path.join(cliHome, "host-trace.txt");
  const help = execFileSync("devtunnel", ["--help"], { encoding: "utf8", timeout: 15000, cwd: cliHome,
    env: { ...process.env, HOME: cliHome, XDG_CONFIG_HOME: path.join(cliHome, ".config"),
      XDG_DATA_HOME: path.join(cliHome, ".local/share"), XDG_CACHE_HOME: path.join(cliHome, ".cache"),
      DOTNET_HOST_TRACE: "1", DOTNET_HOST_TRACEFILE: traceFile } });
  assert.match(help, /Dev Tunnels CLI/);
  const trace = fs.readFileSync(traceFile, "utf8");
  assert.ok(trace.includes("CoreCLR path = '/opt/devtunnel/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so'"));
} finally { fs.rmSync(cliHome, { recursive: true, force: true }); }
const packages = JSON.parse(fs.readFileSync(`${root}/packages.json`));
assert.equal(packages.length, 5);
for (const p of packages) {
  assert.equal(
    run("dpkg-query", ["-W", "-f", "${Version}", p.package]),
    p.version,
  );
  for (const file of p.replaced) {
    const actual = createHash("sha256")
      .update(fs.readFileSync(`/${file.installed}`))
      .digest("hex");
    assert.equal(
      actual,
      file.sha256,
      `Native provenance mismatch: ${file.installed}`,
    );
  }
}
assert.equal(run("dpkg", ["--audit"]), "");
const directories = [
  "/usr/bin",
  "/usr/sbin",
  "/usr/lib/systemd",
  "/lib/systemd",
];
const absent = [
  "avahi-daemon",
  "systemd-journald",
  "genrb",
  "eu-readelf",
  "eu-strip",
  "newuidmap",
  "newgidmap",
];
for (const name of absent)
  for (const dir of directories)
    assert.equal(fs.existsSync(`${dir}/${name}`), false, `${dir}/${name}`);
assert.equal(
  fs
    .readdirSync("/usr/lib")
    .flatMap((d) => {
      const dir = `/usr/lib/${d}`;
      return d.endsWith("-linux-gnu") ? fs.readdirSync(dir) : [];
    })
    .some((x) => /^libdw\.so/.test(x)),
  false,
);

const processes = [];
for (const pid of fs.readdirSync("/proc").filter((x) => /^\d+$/.test(x))) {
  let status;
  try {
    status = fs.readFileSync(`/proc/${pid}/status`, "utf8");
  } catch (e) {
    if (e.code === "ENOENT") continue;
    throw e;
  }
  const name = status.match(/^Name:\s*(.*)$/m)?.[1];
  const uids = status
    .match(/^Uid:\s*(.*)$/m)?.[1]
    .trim()
    .split(/\s+/)
    .map(Number);
  assert.deepEqual(
    uids,
    Array(4).fill(expectedUid),
    `UID/saved UID for ${name}`,
  );
  assert.match(status, /^CapEff:\s*0+$/m, `Capabilities for ${name}`);
  assert.match(status, /^NoNewPrivs:\s*1$/m, `NoNewPrivs for ${name}`);
  processes.push(name);
}
assert.ok(
  processes.some((x) => x.includes("squid")),
  "Squid must be running for UID evidence",
);
console.log(
  JSON.stringify({
    nativePackages: packages.length,
    cliRuntime: cliEvidence.installedRuntime,
    hashes: packages.reduce((n, p) => n + p.replaced.length, 0),
    absentComponents: absent,
    libdwAbsent: true,
    allProcessUids: expectedUid,
    capabilities: 0,
    noNewPrivileges: true,
    caveat:
      "Component absence and runtime mitigations are not package vulnerability fixes",
  }),
);
