# DevTunnel Toolkit Desktop

For the portable ZIP or tarball, extract the entire archive into a user-owned
directory. Keep the `agent/` and `cli/` directories beside the desktop executable.
Each executable carries its own .NET runtime; no global .NET installation is required.
On Windows, the matching `-setup.exe` installer is an alternative to extraction:
it installs the same layout for the current user, creates a Start Menu shortcut,
and can add a desktop shortcut. It does not require administrator rights.

For Microsoft/GitHub CLI authentication, install the official `devtunnel` CLI
separately and make it available on the agent's `PATH`, or set
`DEVTUNNEL_TOOLKIT_CLI_PATH` to an absolute executable path. Native SDK authentication
does not require that executable.
Entra/Microsoft CLI offers browser, device-code and Windows-integrated login in
Accounts > Edit > CLI login flow. Integrated login requires a supported Windows
session and organizational policies; no password or custom Client ID is collected. The packaged application does not bundle or
silently download the CLI.

- Windows: launch `DevTunnelToolkit.App.exe`.
- Linux: launch `./DevTunnelToolkit.App` from a graphical session.
- Headless: run `agent/DevTunnelToolkit.Agent`, then use
  `cli/DevTunnelToolkit.Cli --help` in another terminal.

The desktop and CLI communicate with the same per-user agent. Account data and
credentials are stored in the user's application-data directory and configured
credential vault, never in this installation directory.

Linux desktop prerequisites include a supported graphical session and the
Avalonia platform libraries (X11, fontconfig and the desktop's graphics stack).
The Linux agent and CLI can run without a graphical session. Linux packages use
glibc and are not Alpine/musl packages.

## Background operation

Closing the window keeps it in the notification area when supported and enabled.
Use the logo icon to reopen it or choose **Encerrar tudo** to stop the agent,
tunnels and optional local proxy. Launching the app again focuses the existing
instance. On Linux without a supported tray host, use **Configurações > Encerrar
tudo** or `cli/DevTunnelToolkit.Cli agent stop` (`.exe` on Windows).
Agent-only startup remains headless.

The optional **Proxy e PAC** page configures ordered host/IP/CIDR and port rules,
explicit direct fallback and generated/manual PAC. A fixed local HTTP proxy
(default `127.0.0.1:8888`) supports clients without PAC. System proxy settings
are never changed automatically; TLS is not intercepted.

## Optional startup

Startup is opt-in. After extracting into the final installation directory:

```powershell
# Windows, standard user PowerShell
./startup/configure-startup.ps1 -Enable
./startup/configure-startup.ps1 -Disable
```

```sh
# Linux, standard user session with systemd --user
bash ./startup/configure-startup.sh --enable
bash ./startup/configure-startup.sh --disable
```

These scripts manage only the application's own per-user startup entry. They do
not install a system service, enable user lingering, or request administrator
access. They register the packaged agent with its data directory through the
same manager used by the desktop settings. Enabling startup takes effect at the
next user sign-in and does not start a second agent. Disable startup before
moving or deleting the installation directory.
Changing automatic startup from the application may be unavailable on desktops
without a supported user-session service manager.

For a custom data directory, pass `-DataDirectory` on Windows or `--data-dir` on
Linux. Headless encryption keys must be supplied through the user session's
external secret configuration; startup registration never copies a key from
the current process environment into a service file or registry value.

## Verification and updates

Verify the adjacent `.sha256` file before extraction. Preserve the installation
layout when updating. Stop the agent and close the desktop before replacing
files; account/configuration data remains outside the installation directory.

Back up the private state directory with the agent stopped before upgrading.
This version reads schema 1/2 and marks state as schema 3 on save. Older agents
reject schema 3; downgrading requires a pre-upgrade backup.

Always update the desktop, agent and CLI together. If the interface reports an
incompatible agent, stop the previous `DevTunnelToolkit.Agent` process (this
interrupts its active connections), then use **Start local agent** in the updated
desktop. A portable agent can remain running after installing a newer desktop;
closing the desktop alone does not stop it. Do not stop the separate official
`devtunnel` CLI or delete the application-data directory.

`build-manifest.json` identifies the version, source commit, target architecture
and packaged dependencies. CI-generated packages also include an SPDX SBOM.
ARM64 cross-builds are not evidence of successful native ARM64 execution.

Unsigned archives and installers do not provide a code-signing trust guarantee.
Use release checksums and the repository's release provenance when available.
The installer uninstalls application files and startup registration owned by that
installation, but intentionally preserves user profiles and credentials outside
the installation directory. Windows may show a publisher warning until signing
infrastructure is added.
