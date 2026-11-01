import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { packageCli, runtimeVersion } from "./managed-cli.mjs";

const pins = JSON.parse(readFileSync(new URL("./devtunnel-runtime.json", import.meta.url)));
const [architecture, version = pins.cliVersion, override = "", environment = "prod"] = process.argv.slice(2);
const pin = pins.architectures[architecture];
if (!pin || version !== pins.cliVersion || pins.runtimeVersion !== runtimeVersion ||
  !/^[a-z0-9]+$/.test(environment) || (environment !== "prod" && !override) ||
  (override && !/^[a-f0-9]{64}$/.test(override))) throw new Error("INVALID_CLI_BUILD_INPUT");

function download(url, algorithm, expected) {
  // curl honors the build environment's standard network configuration. Nothing
  // from that environment is copied into the final runtime image.
  const bytes = execFileSync("curl", ["--silent", "--show-error", "--fail", "--retry", "3", "--connect-timeout", "15",
    "--max-time", "180", "--max-filesize", "134217728", "--proto", "=https", "--tlsv1.2", url],
  { maxBuffer: 128 * 1024 * 1024, timeout: 240000 });
  if (createHash(algorithm).update(bytes).digest("hex") !== expected) throw new Error("CLI_ARTIFACT_HASH_MISMATCH");
  return bytes;
}

const work = mkdtempSync(path.join(tmpdir(), "managed-cli-build-"));
try {
  const sourceSha256 = override || pin.cliSha256;
  const cliUrl = `https://tunnelsassets${environment}.blob.core.windows.net/cli/${pin.rid}-devtunnel`;
  const runtimeUrl = `https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtimeVersion}/dotnet-runtime-${runtimeVersion}-${pin.rid}.tar.gz`;
  const bundle = download(cliUrl, "sha256", sourceSha256);
  console.log(`Verified CLI source (${pin.rid}, SHA-256).`);
  const runtime = download(runtimeUrl, "sha512", pin.runtimeSha512);
  console.log(`Verified .NET ${runtimeVersion} runtime (${pin.rid}, SHA-512).`);
  const archive = path.join(work, "runtime.tar.gz"); writeFileSync(archive, runtime);
  mkdirSync("/out"); mkdirSync("/out/dotnet"); mkdirSync("/out/evidence");
  execFileSync("tar", ["-xzf", archive, "--no-same-owner", "-C", "/out/dotnet"]);
  const framework = `/out/dotnet/shared/Microsoft.NETCore.App/${runtimeVersion}`;
  const evidence = packageCli(bundle, "/out/cli", framework, { rid: pin.rid, cliVersion: version, sha256: sourceSha256 });
  evidence.sourceUrl = cliUrl; evidence.runtimeUrl = runtimeUrl; evidence.runtimeSha512 = pin.runtimeSha512;
  writeFileSync("/out/evidence/packaging.json", JSON.stringify(evidence, null, 2) + "\n");
  const home = path.join(work, "home"); mkdirSync(home);
  for (const directory of [".config", ".local/share", ".cache"]) mkdirSync(path.join(home, directory), { recursive: true });
  const help = execFileSync("/out/dotnet/dotnet", ["exec", "/out/cli/devtunnel.dll", "--help"], {
    encoding: "utf8", timeout: 30000, cwd: home, env: { ...process.env, HOME: home,
      XDG_CONFIG_HOME: path.join(home, ".config"), XDG_DATA_HOME: path.join(home, ".local/share"), XDG_CACHE_HOME: path.join(home, ".cache"),
      DOTNET_HOST_TRACE: "1", DOTNET_HOST_TRACEFILE: path.join(work, "host-trace.txt") },
  });
  const trace = readFileSync(path.join(work, "host-trace.txt"), "utf8");
  if (!help.includes(`CLI version: ${version}`) || !help.includes("Dev Tunnels CLI") ||
    !trace.includes(`CoreCLR path = '${framework}/libcoreclr.so'`)) throw new Error("CLI_PATCHED_RUNTIME_SMOKE_FAILED");
  console.log(`Verified unchanged CLI ${version} application on .NET ${runtimeVersion} (${pin.rid}); ${evidence.excludedRuntimeFiles.length} old framework files excluded.`);
} finally { rmSync(work, { recursive: true, force: true }); }
