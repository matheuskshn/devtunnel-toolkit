import { accessSync, constants, realpathSync, statSync } from "node:fs";
import path from "node:path";

// Release automation runs on Unix hosts with system-managed tools. Never search
// the inherited PATH, a checkout directory, or an operator-supplied executable.
const commands = new Set(["git", "docker", "skopeo"]);
export function trustedExecutable(command) {
  if (!commands.has(command) || process.platform === "win32") {
    throw new Error("UNSUPPORTED_AUTOMATION_EXECUTABLE");
  }
  for (const directory of ["/usr/bin", "/usr/local/bin", "/bin"]) {
    try {
      const executable = realpathSync(path.join(directory, command));
      accessSync(executable, constants.X_OK);
      if (!statSync(executable).isFile()) continue;
      if (isSystemManaged(executable)) return executable;
    } catch {
      // An absent or unsafe candidate is not a reason to consult PATH.
    }
  }
  throw new Error("TRUSTED_AUTOMATION_EXECUTABLE_REQUIRED");
}

function isSystemManaged(file) {
  let current = file;
  while (true) {
    const metadata = statSync(current);
    if (metadata.uid !== 0 || (metadata.mode & 0o022) !== 0) return false;
    const parent = path.dirname(current);
    if (parent === current) return true;
    current = parent;
  }
}
