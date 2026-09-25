import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const repository = resolve(root, "../..");
const args = process.argv.slice(2);
if (
  args.length !== 2 ||
  args[0] !== "--rid" ||
  !["linux-x64", "linux-arm64", "win-x64", "win-arm64"].includes(args[1])
) {
  console.error(
    "Usage: node scripts/package.mjs --rid linux-x64|linux-arm64|win-x64|win-arm64",
  );
  process.exit(2);
}
const rid = args[1];
const windowsTarget = rid.startsWith("win-");
if (windowsTarget && process.platform !== "win32")
  throw new Error("Windows ZIP packaging runs on a Windows runner.");
const version = readFileSync(join(repository, "version.txt"), "utf8").trim();
if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version))
  throw new Error("Invalid suite version.");
const output = join(root, "artifacts");
mkdirSync(output, { recursive: true });
const archiveName = `devtunnel-toolkit-desktop-${version}-${rid}.${windowsTarget ? "zip" : "tar.gz"}`;
const archive = join(output, archiveName);
const installerName = windowsTarget
  ? `devtunnel-toolkit-desktop-${version}-${rid}-setup.exe`
  : null;
const installer = installerName ? join(output, installerName) : null;
const innoCompiler = windowsTarget ? process.env.INNO_COMPILER : null;
if (windowsTarget && (!innoCompiler || !existsSync(innoCompiler)))
  throw new Error(
    "Windows packaging requires INNO_COMPILER pointing to the verified ISCC.exe.",
  );
if (installer && (existsSync(installer) || existsSync(`${installer}.sha256`)))
  throw new Error(
    `Installer already exists: ${installerName}. Move it before packaging again.`,
  );
if (existsSync(archive))
  throw new Error(
    `Package already exists: ${archiveName}. Move it before packaging again.`,
  );
const temporary = mkdtempSync(join(tmpdir(), "devtunnel-toolkit-package-"));
const stage = join(temporary, "package");
const dotnet = process.env.DOTNET_EXECUTABLE || "dotnet";

function run(command, commandArgs, options = {}) {
  const result = spawnSync(command, commandArgs, {
    cwd: root,
    shell: false,
    stdio: "inherit",
    ...options,
  });
  if (result.error || result.status !== 0)
    throw new Error(`Packaging command failed: ${basename(command)}`);
  return result;
}

function files(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isSymbolicLink())
      throw new Error("Package staging contains a symlink.");
    return entry.isDirectory() ? files(path) : [path];
  });
}

try {
  for (const [project, subdirectory] of [
    ["App", ""],
    ["Agent", "agent"],
    ["Cli", "cli"],
  ]) {
    run(dotnet, [
      "publish",
      `src/DevTunnelToolkit.${project}/DevTunnelToolkit.${project}.csproj`,
      "-c",
      "Release",
      "-r",
      rid,
      "--self-contained",
      "true",
      "-o",
      join(stage, subdirectory),
      "-p:PublishSingleFile=false",
      "-p:PublishTrimmed=false",
      "-p:RestoreLockedMode=true",
      "-p:DebugType=none",
    ]);
  }
  cpSync(join(repository, "LICENSE"), join(stage, "LICENSE"));
  cpSync(join(root, "packaging/README.md"), join(stage, "README.md"));
  cpSync(join(root, "packaging/PROXY-THIRD-PARTY-NOTICES.txt"), join(stage, "PROXY-THIRD-PARTY-NOTICES.txt"));
  mkdirSync(join(stage, "startup"), { recursive: true });
  const startupScript = windowsTarget
    ? "configure-startup.ps1"
    : "configure-startup.sh";
  cpSync(
    join(root, "packaging", windowsTarget ? "windows" : "linux", startupScript),
    join(stage, "startup", startupScript),
  );

  const revision = run("git", ["rev-parse", "HEAD"], {
    stdio: "pipe",
    encoding: "utf8",
  }).stdout.trim();
  if (!/^[0-9a-f]{40}$/.test(revision))
    throw new Error("Invalid source revision.");
  const dependencies = new Map();
  for (const path of files(stage).filter((path) =>
    path.endsWith(".deps.json"),
  )) {
    const document = JSON.parse(readFileSync(path, "utf8"));
    for (const [name, metadata] of Object.entries(document.libraries ?? {})) {
      if (metadata.type === "package")
        dependencies.set(name, { name, sha512: metadata.sha512 ?? null });
    }
  }
  const nativeTarget =
    ((windowsTarget && process.platform === "win32") ||
      (rid.startsWith("linux-") && process.platform === "linux")) &&
    rid.endsWith(`-${process.arch}`);
  if (nativeTarget) {
    for (const component of [
      "agent/DevTunnelToolkit.Agent",
      "cli/DevTunnelToolkit.Cli",
    ]) {
      run(join(stage, `${component}${windowsTarget ? ".exe" : ""}`), [
        "--help",
      ]);
    }
  }
  const manifest = {
    schemaVersion: 1,
    product: "DevTunnel Toolkit Desktop",
    version,
    runtimeIdentifier: rid,
    sourceRevision: revision,
    builtAt: new Date().toISOString(),
    sourceWorktreeDirty:
      run("git", ["status", "--porcelain"], {
        stdio: "pipe",
        encoding: "utf8",
      }).stdout.trim().length > 0,
    nativeCliAgentSmoke: nativeTarget,
    architectureValidation: rid.endsWith("arm64")
      ? "Cross-built; native ARM64 runtime verification required."
      : "See CI test results for native runtime verification.",
    dependencies: [...dependencies.values()].sort((a, b) =>
      a.name.localeCompare(b.name),
    ),
  };
  writeFileSync(
    join(stage, "build-manifest.json"),
    `${JSON.stringify(manifest, null, 2)}\n`,
  );

  if (process.env.SBOM_TOOL) {
    run(process.env.SBOM_TOOL, [
      "generate",
      "-b",
      stage,
      "-bc",
      join(root, "src"),
      "-pn",
      "DevTunnel Toolkit Desktop",
      "-pv",
      version,
      "-ps",
      "DevTunnel Toolkit contributors",
      "-nsb",
      "https://github.com/matheuskshn/devtunnel-toolkit",
      "-mi",
      "SPDX:2.2",
    ]);
    const sbom = join(stage, "_manifest/spdx_2.2/manifest.spdx.json");
    if (!existsSync(sbom))
      throw new Error("SBOM generation did not produce an SPDX document.");
    cpSync(
      sbom,
      join(output, `devtunnel-toolkit-desktop-${version}-${rid}.spdx.json`),
    );
  } else {
    console.warn(
      "SBOM_TOOL is unset. This local package has a dependency manifest but no SPDX SBOM. CI requires the pinned SBOM tool.",
    );
  }

  const forbidden = files(stage).find((path) =>
    relative(stage, path)
      .split(/[\\/]/)
      .some(
        (part) =>
          [
            ".local",
            ".git",
            "appsettings.Development.json",
            "credentials.json",
          ].includes(part) ||
          /^\.env(?:\.|$)/i.test(part) ||
          /\.(?:db|sqlite|sqlite3|pfx|key)$/i.test(part),
      ),
  );
  if (forbidden)
    throw new Error("Package includes a forbidden runtime or local artifact.");
  for (const component of [
    "DevTunnelToolkit.App",
    "agent/DevTunnelToolkit.Agent",
    "cli/DevTunnelToolkit.Cli",
  ]) {
    const executable = join(
      stage,
      `${component}${windowsTarget ? ".exe" : ""}`,
    );
    if (!existsSync(executable) || statSync(executable).size === 0)
      throw new Error("A required executable is missing.");
  }

  if (windowsTarget) {
    run("tar.exe", ["-a", "-cf", archive, "-C", stage, "."]);
  } else {
    run("tar", ["-czf", archive, "-C", stage, "."]);
  }
  if (installer) {
    run(innoCompiler, [
      `--define=AppVersion=${version}`,
      `--define=RuntimeIdentifier=${rid}`,
      `--define=TargetArchitecture=${rid === "win-arm64" ? "arm64" : "x64compatible and not arm64"}`,
      `--define=StageDir=${stage}`,
      `--output-dir=${output}`,
      join(root, "packaging/windows/installer.iss"),
    ]);
    if (!existsSync(installer) || statSync(installer).size === 0)
      throw new Error("Inno Setup did not produce the expected installer.");
    const installerHash = createHash("sha256")
      .update(readFileSync(installer))
      .digest("hex");
    writeFileSync(`${installer}.sha256`, `${installerHash}  ${installerName}\n`);
    console.log(`Installer: ${installer}\nSHA256: ${installerHash}`);
  }
  const hash = createHash("sha256").update(readFileSync(archive)).digest("hex");
  writeFileSync(`${archive}.sha256`, `${hash}  ${archiveName}\n`);
  cpSync(
    join(stage, "build-manifest.json"),
    join(
      output,
      `${basename(archive, windowsTarget ? ".zip" : ".tar.gz")}.build.json`,
    ),
  );
  console.log(`Package: ${archive}\nSHA256: ${hash}`);
} finally {
  // This directory is created exclusively for this invocation and never supplied by a caller.
  rmSync(temporary, { recursive: true, force: true });
}
