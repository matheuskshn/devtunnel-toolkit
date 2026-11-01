// .NET bundle v6 layout: dotnet/runtime Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs.
// Repackage unchanged application files with a separately serviced framework.
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { inflateRawSync } from "node:zlib";

const signature = Buffer.from("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae", "hex");
const maximumPayload = 512 * 1024 * 1024;
export const runtimeVersion = "10.0.14";
const check = (value, code) => { if (!value) throw new Error(code); };
const hash = (bytes) => createHash("sha256").update(bytes).digest("hex");

export function readBundle(bytes) {
  check(bytes.length <= 256 * 1024 * 1024, "CLI_BUNDLE_SIZE");
  const marker = bytes.indexOf(signature);
  check(marker >= 8 && bytes.indexOf(signature, marker + 1) < 0, "CLI_BUNDLE_MARKER");
  const number = (value) => {
    check(value >= 0n && value <= BigInt(Number.MAX_SAFE_INTEGER), "CLI_BUNDLE_INTEGER");
    return Number(value);
  };
  const header = number(bytes.readBigInt64LE(marker - 8));
  let cursor = header;
  const available = (length) => check(cursor + length <= bytes.length, "CLI_BUNDLE_TRUNCATED");
  const u32 = () => { available(4); const value = bytes.readUInt32LE(cursor); cursor += 4; return value; };
  const u64 = () => { available(8); const value = number(bytes.readBigInt64LE(cursor)); cursor += 8; return value; };
  const string = () => {
    let length = 0;
    for (let shift = 0; ; shift += 7) {
      check(shift <= 28, "CLI_BUNDLE_STRING"); available(1);
      const byte = bytes[cursor++]; length += (byte & 127) * 2 ** shift;
      if (!(byte & 128)) break;
    }
    check(length > 0 && length <= 4096, "CLI_BUNDLE_STRING"); available(length);
    const value = new TextDecoder("utf-8", { fatal: true }).decode(bytes.subarray(cursor, cursor + length));
    cursor += length; return value;
  };
  check(header > marker + signature.length && header < bytes.length, "CLI_BUNDLE_HEADER");
  check(u32() === 6 && u32() === 0, "CLI_BUNDLE_VERSION");
  const count = u32(); check(count > 0 && count <= 4096, "CLI_BUNDLE_COUNT");
  const bundleId = string();
  const depsOffset = u64(), depsSize = u64(), configOffset = u64(), configSize = u64();
  check(u64() === 1, "CLI_BUNDLE_EXTRACTION_MODE");
  const entries = new Map();
  const ranges = [];
  let expanded = 0;
  for (let index = 0; index < count; index++) {
    const offset = u64(), size = u64(), compressed = u64(); available(1);
    const type = bytes[cursor++], name = string();
    check(!name.includes("\\") && !name.includes("\0") && !path.posix.isAbsolute(name) &&
      name.split("/").every(part => part && part !== "." && part !== "..") && !entries.has(name), "CLI_BUNDLE_PATH");
    expanded += size;
    check(type <= 5 && size > 0 && expanded <= maximumPayload && offset > marker + signature.length &&
      offset + (compressed || size) <= header, "CLI_BUNDLE_BOUNDS");
    ranges.push([offset, offset + (compressed || size)]);
    entries.set(name, { type, offset, size, compressed });
  }
  ranges.sort((a, b) => a[0] - b[0]);
  check(ranges.every((range, index) => !index || range[0] >= ranges[index - 1][1]), "CLI_BUNDLE_OVERLAP");
  for (const entry of entries.values()) {
    const stored = bytes.subarray(entry.offset, entry.offset + (entry.compressed || entry.size));
    entry.data = entry.compressed ? inflateRawSync(stored, { maxOutputLength: entry.size }) : stored;
    check(entry.data.length === entry.size, "CLI_BUNDLE_EXPANDED_SIZE");
  }
  for (const [name, type, offset, size] of [["devtunnel.deps.json", 3, depsOffset, depsSize], ["devtunnel.runtimeconfig.json", 4, configOffset, configSize]]) {
    const entry = entries.get(name);
    check(entry?.type === type && entry.offset === offset && entry.size === size && size < 2 * 1024 * 1024, "CLI_BUNDLE_CONFIG");
  }
  check(entries.get("devtunnel.dll")?.type === 1, "CLI_BUNDLE_APPLICATION");
  return { bundleId, entries };
}

export function packageCli(bytes, output, framework, { rid, cliVersion, sha256 }) {
  check(/^[a-f0-9]{64}$/.test(sha256) && hash(bytes) === sha256, "CLI_SOURCE_HASH");
  check(["linux-x64", "linux-arm64"].includes(rid), "CLI_RUNTIME_IDENTIFIER");
  const { bundleId, entries } = readBundle(bytes);
  const deps = JSON.parse(entries.get("devtunnel.deps.json").data);
  const config = JSON.parse(entries.get("devtunnel.runtimeconfig.json").data);
  const runtimeKey = `runtimepack.Microsoft.NETCore.App.Runtime.${rid}/10.0.12`;
  check(deps.runtimeTarget?.name === `.NETCoreApp,Version=v10.0/${rid}` && deps.libraries?.[`devtunnel/${cliVersion}`] &&
    deps.libraries[runtimeKey]?.type === "runtimepack" && Object.values(deps.libraries).filter(v => v.type === "runtimepack").length === 1,
  "CLI_RUNTIME_INVENTORY");
  check(config.runtimeOptions?.tfm === "net10.0" && config.runtimeOptions.includedFrameworks?.length === 1 &&
    config.runtimeOptions.includedFrameworks[0].name === "Microsoft.NETCore.App" &&
    config.runtimeOptions.includedFrameworks[0].version === "10.0.12", "CLI_SOURCE_RUNTIME");
  const frameworkDeps = JSON.parse(readFileSync(path.join(framework, "Microsoft.NETCore.App.deps.json")));
  check(frameworkDeps.libraries?.[`Microsoft.NETCore.App.Runtime.${rid}/${runtimeVersion}`], "CLI_PATCHED_FRAMEWORK");
  const frameworkFiles = new Set(readdirSync(framework));
  const excluded = new Set();
  for (const target of Object.values(deps.targets)) {
    if (Object.keys(target).length === 0) continue; // SDK emits an empty portable target beside the RID target.
    const runtime = target[runtimeKey];
    check(runtime && Object.keys(runtime.native ?? {}).length === 0 && Object.keys(runtime.runtimeTargets ?? {}).length === 0, "CLI_RUNTIME_ASSETS");
    for (const name of Object.keys(runtime.runtime ?? {})) {
      const filename = path.posix.basename(name);
      check(entries.has(filename) && frameworkFiles.has(filename), "CLI_RUNTIME_REPLACEMENT_MISSING");
      excluded.add(filename);
    }
    delete target[runtimeKey];
    for (const library of Object.values(target)) {
      if (library.dependencies) delete library.dependencies[`runtimepack.Microsoft.NETCore.App.Runtime.${rid}`];
    }
  }
  check(excluded.has("System.Private.CoreLib.dll") && excluded.has("System.Runtime.dll"), "CLI_RUNTIME_INCOMPLETE");
  delete deps.libraries[runtimeKey];
  const applicationFiles = [], excludedRuntimeFiles = [];
  for (const [name, entry] of entries) {
    if (excluded.has(name)) { excludedRuntimeFiles.push({ name, sha256: hash(entry.data) }); continue; }
    if (["devtunnel.deps.json", "devtunnel.runtimeconfig.json"].includes(name)) continue;
    check(!frameworkFiles.has(path.posix.basename(name)), "CLI_FRAMEWORK_SHADOW");
    applicationFiles.push({ name, sha256: hash(entry.data), data: entry.data });
  }
  // Never overlay an existing directory or write through an existing symlink.
  mkdirSync(output);
  for (const { name, data } of applicationFiles) {
    const target = path.join(output, name); mkdirSync(path.dirname(target), { recursive: true });
    writeFileSync(target, data, { flag: "wx", mode: 0o644 });
  }
  delete config.runtimeOptions.includedFrameworks;
  config.runtimeOptions.framework = { name: "Microsoft.NETCore.App", version: runtimeVersion };
  config.runtimeOptions.rollForward = "LatestPatch";
  writeFileSync(path.join(output, "devtunnel.runtimeconfig.json"), JSON.stringify(config, null, 2) + "\n", { flag: "wx" });
  writeFileSync(path.join(output, "devtunnel.deps.json"), JSON.stringify(deps, null, 2) + "\n", { flag: "wx" });
  return { schemaVersion: 1, cliVersion, rid, sourceBundleSha256: sha256, bundleId,
    originalRuntime: "10.0.12", installedRuntime: runtimeVersion, deployment: "framework-dependent",
    applicationFiles: applicationFiles.map(({ name, sha256 }) => ({ name, sha256 })), excludedRuntimeFiles };
}
