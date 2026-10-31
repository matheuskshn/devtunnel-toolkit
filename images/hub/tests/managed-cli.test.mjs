import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { deflateRawSync } from "node:zlib";
import { readBundle, packageCli } from "../bin/managed-cli.mjs";

const signature = Buffer.from("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae", "hex");
const digest = b => createHash("sha256").update(b).digest("hex");
const json = value => Buffer.from(JSON.stringify(value));
const uint = value => { const b = Buffer.alloc(4); b.writeUInt32LE(value); return b; };
const long = value => { const b = Buffer.alloc(8); b.writeBigInt64LE(BigInt(value)); return b; };
const text = value => { const b = Buffer.from(value); assert.ok(b.length < 128); return Buffer.concat([Buffer.from([b.length]), b]); };

function fixtureEntries(rid = "linux-x64") {
  const runtime = `runtimepack.Microsoft.NETCore.App.Runtime.${rid}`;
  const deps = { runtimeTarget: { name: `.NETCoreApp,Version=v10.0/${rid}` },
    targets: { ".NETCoreApp,Version=v10.0": {}, [`.NETCoreApp,Version=v10.0/${rid}`]: {
      "devtunnel/1.0.2115": { runtime: { "devtunnel.dll": {} }, dependencies: { [runtime]: "10.0.12", Vendor: "1.2.3" } },
      [`${runtime}/10.0.12`]: { runtime: { "System.Private.CoreLib.dll": {}, "System.Runtime.dll": {} }, native: {} },
      "Vendor/1.2.3": { runtime: { "Vendor.dll": {} } },
    } }, libraries: { "devtunnel/1.0.2115": { type: "project" }, [`${runtime}/10.0.12`]: { type: "runtimepack" }, "Vendor/1.2.3": { type: "package" } } };
  const config = { runtimeOptions: { tfm: "net10.0", includedFrameworks: [{ name: "Microsoft.NETCore.App", version: "10.0.12" }],
    configProperties: { "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false } } };
  return [
    { name: "devtunnel.dll", type: 1, data: Buffer.from("unchanged vendor application") },
    { name: "Vendor.dll", type: 1, data: Buffer.from("unchanged dependency"), compress: true },
    { name: "fr/Vendor.resources.dll", type: 1, data: Buffer.from("unchanged satellite") },
    { name: "libvendor.so", type: 2, data: Buffer.from("unchanged native library") },
    { name: "System.Private.CoreLib.dll", type: 1, data: Buffer.from("old runtime corelib") },
    { name: "System.Runtime.dll", type: 1, data: Buffer.from("old runtime facade") },
    { name: "devtunnel.deps.json", type: 3, data: json(deps) },
    { name: "devtunnel.runtimeconfig.json", type: 4, data: json(config) },
  ];
}

function bundle(entries, { version = 6, mutate } = {}) {
  const stub = Buffer.alloc(128); signature.copy(stub, 16);
  let offset = stub.length;
  const files = entries.map(e => {
    const bytes = e.compress ? deflateRawSync(e.data) : e.data;
    const result = { ...e, offset, bytes }; offset += bytes.length; return result;
  });
  stub.writeBigInt64LE(BigInt(offset), 8);
  const deps = files.find(e => e.name === "devtunnel.deps.json"), config = files.find(e => e.name === "devtunnel.runtimeconfig.json");
  mutate?.(files);
  const header = Buffer.concat([uint(version), uint(0), uint(files.length), text("fixture-id"), long(deps.offset), long(deps.data.length),
    long(config.offset), long(config.data.length), long(1)]);
  const manifest = files.map(e => Buffer.concat([long(e.offset), long(e.data.length), long(e.compress ? e.bytes.length : 0), Buffer.from([e.type]), text(e.name)]));
  return Buffer.concat([stub, ...files.map(e => e.bytes), header, ...manifest]);
}

function workspace(t, rid = "linux-x64") {
  const root = mkdtempSync(path.join(tmpdir(), "managed-cli-test-")); t.after(() => rmSync(root, { recursive: true, force: true }));
  const framework = path.join(root, "framework"); mkdirSync(framework);
  writeFileSync(path.join(framework, "Microsoft.NETCore.App.deps.json"), json({ libraries: { [`Microsoft.NETCore.App.Runtime.${rid}/10.0.14`]: {} } }));
  for (const file of ["System.Private.CoreLib.dll", "System.Runtime.dll", "System.Net.Http.dll"]) writeFileSync(path.join(framework, file), "patched framework");
  return { framework, output: path.join(root, "cli") };
}

for (const rid of ["linux-x64", "linux-arm64"]) test(`repackaging ${rid} retains vendor bytes and replaces the complete old framework`, t => {
  const { framework, output } = workspace(t, rid), bytes = bundle(fixtureEntries(rid));
  const result = packageCli(bytes, output, framework, { rid, cliVersion: "1.0.2115", sha256: digest(bytes) });
  assert.equal(result.installedRuntime, "10.0.14");
  assert.equal(result.excludedRuntimeFiles.length, 2);
  for (const file of result.applicationFiles) assert.equal(digest(readFileSync(path.join(output, file.name))), file.sha256);
  for (const file of result.excludedRuntimeFiles) assert.equal(existsSync(path.join(output, file.name)), false);
  assert.equal(readFileSync(path.join(output, "Vendor.dll"), "utf8"), "unchanged dependency");
  const config = JSON.parse(readFileSync(path.join(output, "devtunnel.runtimeconfig.json")));
  assert.equal(config.runtimeOptions.framework.version, "10.0.14"); assert.equal(config.runtimeOptions.includedFrameworks, undefined);
  assert.equal(config.runtimeOptions.configProperties["System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"], false);
  const deps = JSON.parse(readFileSync(path.join(output, "devtunnel.deps.json")));
  assert.deepEqual(Object.keys(deps.libraries).sort(), ["Vendor/1.2.3", "devtunnel/1.0.2115"]);
  assert.deepEqual(deps.targets[deps.runtimeTarget.name]["devtunnel/1.0.2115"].dependencies, { Vendor: "1.2.3" });
});

for (const name of ["../escape", "/absolute", "a/../escape", "a\\escape", "a//b", "Vendor.dll", "bad\0path"]) test(`bundle rejects unsafe or duplicate path ${JSON.stringify(name)}`, () => {
  const entries = fixtureEntries(); entries.push({ name, type: 1, data: Buffer.from("bad") });
  assert.throws(() => readBundle(bundle(entries)), /CLI_BUNDLE_PATH/);
});

test("bundle rejects unsupported versions, overlapping payloads, missing markers and out-of-bounds sizes", () => {
  assert.throws(() => readBundle(bundle(fixtureEntries(), { version: 7 })), /CLI_BUNDLE_VERSION/);
  assert.throws(() => readBundle(bundle(fixtureEntries(), { mutate: files => { files[1].offset = files[0].offset; } })), /CLI_BUNDLE_OVERLAP/);
  const missing = bundle(fixtureEntries()); missing.fill(0, 16, 48); assert.throws(() => readBundle(missing), /CLI_BUNDLE_MARKER/);
  assert.throws(() => readBundle(bundle(fixtureEntries(), { mutate: files => { files[0].offset = Number.MAX_SAFE_INTEGER; } })), /CLI_BUNDLE_BOUNDS/);
  const truncated = bundle(fixtureEntries()).subarray(0, -1); assert.throws(() => readBundle(truncated), /CLI_BUNDLE_TRUNCATED/);
});

test("unverified sources and mismatched CLI/framework inventories fail before creating output", t => {
  const { framework, output } = workspace(t), bytes = bundle(fixtureEntries());
  const options = { rid: "linux-x64", cliVersion: "1.0.2115", sha256: digest(bytes) };
  assert.throws(() => packageCli(bytes, output, framework, { ...options, sha256: "0".repeat(64) }), /CLI_SOURCE_HASH/);
  assert.throws(() => packageCli(bytes, output, framework, { ...options, cliVersion: "1.0.0" }), /CLI_RUNTIME_INVENTORY/);
  assert.throws(() => packageCli(bytes, output, framework, { ...options, rid: "linux-arm64" }), /CLI_RUNTIME_INVENTORY/);
  writeFileSync(path.join(framework, "Microsoft.NETCore.App.deps.json"), json({ libraries: {} }));
  assert.throws(() => packageCli(bytes, output, framework, options), /CLI_PATCHED_FRAMEWORK/);
  assert.equal(existsSync(output), false);
});

test("application-local framework shadowing is rejected and existing output is never overwritten", t => {
  const { framework, output } = workspace(t);
  const entries = fixtureEntries(); entries.push({ name: "System.Net.Http.dll", type: 1, data: Buffer.from("old runtime masquerading as app") });
  let bytes = bundle(entries);
  assert.throws(() => packageCli(bytes, output, framework, { rid: "linux-x64", cliVersion: "1.0.2115", sha256: digest(bytes) }), /CLI_FRAMEWORK_SHADOW/);
  mkdirSync(output); writeFileSync(path.join(output, "marker"), "preserve me"); bytes = bundle(fixtureEntries());
  assert.throws(() => packageCli(bytes, output, framework, { rid: "linux-x64", cliVersion: "1.0.2115", sha256: digest(bytes) }), /EEXIST/);
  assert.equal(readFileSync(path.join(output, "marker"), "utf8"), "preserve me");
});
