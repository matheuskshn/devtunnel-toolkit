import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const dotnet = process.env.DOTNET_EXECUTABLE || "dotnet";
const result = spawnSync(
  dotnet,
  [
    "list",
    "DevTunnelToolkit.slnx",
    "package",
    "--vulnerable",
    "--include-transitive",
    "--format",
    "json",
    "--no-restore",
  ],
  {
    cwd: root,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
    shell: false,
  },
);
if (result.error || result.status !== 0) {
  console.error("NuGet vulnerability audit failed to run.");
  process.exit(1);
}
let report;
try {
  report = JSON.parse(result.stdout);
} catch {
  console.error("NuGet returned an invalid vulnerability report.");
  process.exit(1);
}
if (!Array.isArray(report.projects) || report.projects.length === 0) {
  console.error("NuGet did not audit any project.");
  process.exit(1);
}
if (report.problems?.length) {
  console.error("NuGet reported incomplete or failed dependency analysis.");
  process.exit(1);
}
const findings = [];
for (const project of report.projects) {
  for (const framework of project.frameworks ?? []) {
    for (const pkg of [
      ...(framework.topLevelPackages ?? []),
      ...(framework.transitivePackages ?? []),
    ]) {
      for (const vulnerability of pkg.vulnerabilities ?? []) {
        findings.push(
          `${pkg.id}@${pkg.resolvedVersion}: ${vulnerability.severity} ${vulnerability.advisoryurl ?? ""}`,
        );
      }
    }
  }
}
if (findings.length) {
  console.error([...new Set(findings)].join("\n"));
  process.exit(1);
}
console.log(
  `NuGet audit passed for ${report.projects.length} projects, including transitive dependencies.`,
);
