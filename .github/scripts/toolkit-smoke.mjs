// Synthetic, offline runtime validation. No account cache or host home is mounted.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { trustedExecutable } from "./trusted-executable.mjs";

const docker = trustedExecutable("docker");
const image =
  process.env.TOOLKIT_TEST_IMAGE ?? "devtunnel-toolkit:remediation-local";
const result = spawnSync(
  docker,
  [
    "run",
    "--rm",
    "--network",
    "none",
    "--read-only",
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges",
    "--tmpfs",
    "/tmp:mode=1777",
    "--tmpfs",
    "/home/devtunnel:uid=1000,gid=1000,mode=700",
    "--entrypoint",
    "bash",
    image,
    "-ec",
    `
    test "$(id -u)" = 1000
    test "$(id -g)" = 1000
    test "$HOME" = /home/devtunnel
    test -z "$(dpkg --audit)"
    test ! -e /usr/bin/pebble
    set -- help
    source /usr/local/bin/devtunnel-entrypoint >/dev/null
    test -n "$DBUS_SESSION_BUS_ADDRESS"
    dbus-send --session --print-reply --dest=org.freedesktop.secrets \\
      /org/freedesktop/secrets org.freedesktop.DBus.Introspectable.Introspect
    devtunnel --version
  `,
  ],
  { encoding: "utf8", timeout: 120000, maxBuffer: 2 * 1024 * 1024 },
);
assert.equal(result.status, 0, result.stderr);
assert.match(result.stdout, /org\.freedesktop\.Secret\.Service/);
assert.match(result.stdout, /1\.0\.2030/);
console.log(
  "PASS Toolkit: CLI, private D-Bus and Secret Service, UID/GID 1000, offline read-only runtime, intact package inventory",
);
