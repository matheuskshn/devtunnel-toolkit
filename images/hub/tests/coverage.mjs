import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const hub = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repository = path.resolve(hub, "../..");
const run = (args, cwd) =>
  execFileSync(process.execPath, args, { cwd, stdio: "inherit" });

function emittedJavaScript(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      return emittedJavaScript(file);
    }
    return entry.name.endsWith(".js") ? [file] : [];
  });
}

export function verifyCoverageEmissions(directory = hub) {
  const emitted = path.join(directory, "dist");
  const orphaned = emittedJavaScript(emitted).filter((file) => {
    const source = path.join(
      directory,
      "src",
      path.relative(emitted, file).replace(/\.js$/, ".ts"),
    );
    return !existsSync(source) || !statSync(source).isFile();
  });
  if (orphaned.length) {
    // Never delete another developer's build artifacts automatically.
    throw new Error(
      `COVERAGE_STALE_EMISSIONS: ${orphaned
        .map((file) => path.relative(directory, file))
        .sort()
        .join(", ")}. ` +
        "Use a clean build directory or remove only verified obsolete build artifacts before collecting coverage.",
    );
  }
}

function collectCoverage() {
  // Coverage builds keep source maps beside the compiled code. The production
  // Docker build remains unchanged and does not serve these maps to browsers.
  run(
    [
      path.join(hub, "node_modules/typescript/bin/tsc"),
      "--sourceMap",
      "--inlineSources",
    ],
    hub,
  );
  // tsc does not remove output for deleted/renamed TS files. all=true must not
  // count these stale files or their embedded old source maps as current code.
  verifyCoverageEmissions();
  const tests = ["images/hub/tests", ".github/scripts"].flatMap((directory) =>
    readdirSync(path.join(repository, directory))
      .filter((file) => file.endsWith(".test.mjs"))
      .map((file) => path.join(directory, file)),
  );
  run(
    [
      path.join(hub, "node_modules/c8/bin/c8.js"),
      "--config",
      "images/hub/.c8rc.json",
      process.execPath,
      "--test",
      "--test-timeout=30000",
      ...tests,
    ],
    repository,
  );
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
) {
  collectCoverage();
}
