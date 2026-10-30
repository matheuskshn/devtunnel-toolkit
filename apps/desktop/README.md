# DevTunnel Toolkit Desktop

Native Windows/Linux client for Microsoft Dev Tunnels. The Avalonia interface and
CLI control a per-user background agent through a restricted local pipe/socket.
Closing the window keeps the interface in the notification area when available.
Tunnels and the local proxy belong to the background agent. No container is required.

This is a development preview. Windows and ARM64 builds have CI gates; a
cross-build is not proof of native runtime compatibility. Windows also has a
per-user installer; neither the installer nor the ZIP implies automatic updates
or executable code signing.

## Features

Long-running actions show a description, an indeterminate progress indicator and
elapsed time in the relevant card or dialog. Tunnel feedback follows the agent's
actual lifecycle after the command is accepted: discovery, relay connection, local
port setup and retry backoff. A retry countdown uses the agent's scheduled time,
not an estimated completion percentage. Pending connections can be cancelled;
closing an editor does not cancel a command already accepted by the agent.
Authentication distinguishes waiting for the provider from waiting for user input.
Timeout messages do not imply that an already accepted background operation stopped.

- Microsoft via official CLI without a custom Client ID, plus native Microsoft,
  GitHub SDK, GitHub CLI and scoped tunnel-token accounts.
- Owned-tunnel discovery and manual adoption of an accessible canonical Tunnel ID.
- Multiple simultaneous tunnels, independent reconnect policies and device login.
- Per-port automatic, same-port, manual, reserved-range and disabled mappings.
- Persisted effective ports and conflict-safe binding.
- SQLite profiles, protected credential vault, bounded logs and live status.
- Light, dark, system and high-contrast themes.
- Notification-area controls, single-window activation and graceful agent shutdown.
- Optional selective HTTP/CONNECT proxy with domain/IP/CIDR and port rules.
- Editable PAC rules, local-proxy mode and imported/manual PAC scripts.
- Per-user startup where the platform supports it.
- Remote tunnel/port management, access controls, scoped tokens and explicit batch deletion.
- Independent HTTP/CONNECT and SOCKS5 services with configurable loopback ports,
  ordered access rules, limits, logs and optional startup without a tunnel account.
- Tunnel publications bind an existing managed service or an explicit local TCP port.
- Separate service/publication progress, reconnect controls and notification-area actions.

Connecting and removing a local profile never create or delete a remote resource.
**Conexões** consumes remote tunnels; **Serviços** runs local proxies independently.
**DevTunnel** manages server resources and publications of local services/ports.
Preparing missing remote ports is a separate confirmed action. Stopping a publication
does not stop its services. See [remote management and services](REMOTE_HOSTING.md).

The personal HTTP/CONNECT proxy routes via direct TCP or a connected HTTP/SOCKS5
upstream. A published proxy instead uses the hosting computer's network and its
own default-deny destination policy. Neither intercepts TLS. Hosting adds a
dedicated SOCKS5 TCP CONNECT listener; UDP/BIND, caching and full Squid feature
parity are not provided. Existing client mappings remain independent.

## Run from source

Install the .NET SDK specified in `global.json`. From this directory:

```sh
dotnet restore DevTunnelToolkit.slnx --locked-mode
dotnet build DevTunnelToolkit.slnx
dotnet test DevTunnelToolkit.slnx --no-build
dotnet run --project src/DevTunnelToolkit.Agent -- --data-dir /absolute/path/to/private-state
```

In another terminal:

```sh
dotnet run --project src/DevTunnelToolkit.App -- --data-dir /absolute/path/to/private-state
```

On Windows use an absolute Windows path. On Linux the GUI needs a desktop session
and Avalonia's native X11/graphics dependencies. The agent and CLI work headlessly.
Use the same data directory for all three processes. By default it is
`LocalApplicationData/devtunnel-toolkit` for the current OS user.

Published portable packages contain the GUI at the root, the agent under
`agent/`, and the CLI under `cli/`. Windows installers install that same layout
in the current user's application directory, add a Start Menu shortcut and
optionally a desktop shortcut. No administrator privileges are needed. The GUI
automatically starts its bundled agent without a shell, or reuses a compatible
agent already running for that workspace. Startup waits for IPC readiness. A
version mismatch is reported without killing the existing agent or its tunnels.
A source checkout should start the agent explicitly as shown above.

## Authentication

The account nickname is local metadata. Authenticated identity and timestamps
come from the provider, not editable fields. Reauthentication must preserve the
stable identity of an existing account; add another account for a different user.

GitHub uses the public device-flow client identifier documented by the official
Dev Tunnels SDK. Follow the displayed HTTPS verification link and device code.
Do not put a password, token or client secret in a profile or command argument.

The recommended Microsoft option uses the official `devtunnel` CLI installed on
the device and available on the agent's `PATH` (or set
`DEVTUNNEL_TOOLKIT_CLI_PATH` to its absolute executable path). Its login flow is selectable without an Application/Client ID or tenant:

- **Browser**: Entra/Microsoft using `--entra --use-browser-auth`, like the official
  CLI's interactive default. New Microsoft CLI profiles in the UI use this mode.
  On Windows this can open the native Microsoft WAM dialog rather than a browser
  tab. When initiated from the Desktop, its main window provides the visible
  parent required by the CLI. Progress and cancellation stay in the account card,
  with no additional Toolkit dialog or CMD/Terminal window. Closing the Desktop
  cancels its pending interactive login, not the agent or unrelated connections.
  The agent serializes access to the shared CLI login and independently validates
  the identity before persisting it. The connection-scoped IPC exchange carries
  no tokens, arbitrary commands or raw CLI output; disconnect cancels the attempt.
  A login initiated through the management CLI without the Desktop still uses a
  temporary Toolkit support window. Background commands remain hidden. The CLI
  child inherits only its redirected standard handles; no command shell is used.
- **Device code**: `--use-device-code-auth`, preserved for existing profiles.
- **Windows integrated**: `--entra --use-integrated-windows-auth`, Microsoft CLI
  only, using the Windows identity running the agent. Login is rejected before
  launching the CLI on Linux. Domain/device configuration and organizational
  policies may require interaction/MFA instead; no other flow is tried silently.
  A work account merely registered in Windows does not guarantee integrated
  authentication support. When the CLI requests interactive login, select Browser.

GitHub CLI supports browser or device code, not Windows integrated. A valid shared
CLI login may be reused by the official CLI. Changing the flow changes the next
login method, not the stored account identity. Browser/device flows require the
appropriate interactive environment. The UI shows progress and cancellation even
when no device code is emitted. A Microsoft CLI fallback from Browser/Windows integrated to
device code is stopped and reported, not silently presented as the chosen flow.
Login attempts record their requested mode and start time. Changing authentication
settings or starting a second login is rejected while an attempt is active; cancel
or complete it first. Diagnostics expose only allowlisted categories and numeric
Entra error codes, never raw CLI output or credentials. The application
checks the CLI identity before and after issuing a tunnel-scoped `connect` token;
the official SDK still owns relay streams and local port mappings. It never
reads the CLI's undocumented credential cache, stores its login token or passes
internal connect/host/manage tokens through process arguments or IPC. Explicitly
issued share tokens are a separate opt-in operation, returned only to the requesting
same-user UI/CLI, not snapshots or persisted state. The CLI does not disclose a
reliable login expiry, so the UI reports it as unknown.

The CLI's OS-keychain login is shared with other CLI consumers, not isolated per
desktop profile. Switching the CLI account makes new operations for a previously
bound profile stop with `CLI_ACCOUNT_CHANGED`. Concurrent CLI-backed identities
therefore are not guaranteed. Native GitHub and native Microsoft profiles keep
their own credentials and can coexist with a CLI-backed account. Removing a
CLI-backed desktop profile does not log out the shared CLI session.

The advanced native Microsoft option uses MSAL public-client device flow. Supply
the Client ID of an Entra public-client registration and an appropriate tenant
(`organizations`, `common`, `consumers` or tenant UUID). The default resource
scope is the Dev Tunnels service application's `.default` scope; its use requires
permissions/consent and policies accepted by that tenant. App registration alone
does not establish those permissions. Validate consent and renewal with the
actual organization before deployment.

A scoped connect token can instead be stored using a `TunnelToken` account and
the CLI's `account import-token ACCOUNT_ID --token-stdin` command. Such accounts
adopt a specific tunnel manually; they do not enumerate account-owned tunnels.
Supply the token through redirected stdin, never argv. Stored account credentials
cannot be revealed or exported by this application's IPC API. The separate remote
token command issues scoped share tokens, with explicit reveal/copy controls.

`AuthenticatedAt` is a successful login time. `ExpiresAt`, when supplied by the
provider, is the access credential's expiry, not a guarantee of session lifetime
or refresh eligibility. Conditional access, revocation and tenant policy can
require a new login earlier. Unknown expiry remains unknown. The agent obtains
fresh authorization through its account-scoped provider and asks for login when
silent acquisition is no longer possible.

## Credential storage

| Environment             | Secret storage                                                      |
| ----------------------- | ------------------------------------------------------------------- |
| Windows                 | DPAPI CurrentUser, with private file ACLs                           |
| Linux desktop           | Secret Service through `secret-tool`, with an unlocked keyring      |
| Explicit headless setup | AES-256-GCM files with externally supplied `TOOLKIT_CREDENTIAL_KEY` |

The headless key must be 32 random bytes encoded as Base64. Supply it from your
executor's secret manager and preserve it across restarts. There is no plaintext
fallback and no automatic generation of an adjacent key file. Replacing the key
does not migrate existing credentials. Profile JSON and SQLite contain no access
or refresh tokens. Their account names, domains and identities are nevertheless
private operational data: do not commit data directories or exported logs.

## CLI

Use `dotnet run --project src/DevTunnelToolkit.Cli --` from source, or the packaged
`cli/DevTunnelToolkit.Cli` (`.exe` on Windows). Examples below omit that prefix:

```text
--data-dir ABSOLUTE_PATH status --json
--data-dir ABSOLUTE_PATH account add GitHub "Personal account"
--data-dir ABSOLUTE_PATH account add entra "Work browser" --login-mode browser
--data-dir ABSOLUTE_PATH account add windows "Work integrated"
--data-dir ABSOLUTE_PATH account login ACCOUNT_ID
--data-dir ABSOLUTE_PATH account discover ACCOUNT_ID
--data-dir ABSOLUTE_PATH tunnel add ACCOUNT_ID tunnel-demo.use2 --alias demo
--data-dir ABSOLUTE_PATH tunnel connect tunnel-demo.use2
--data-dir ABSOLUTE_PATH tunnel reconnect demo
--data-dir ABSOLUTE_PATH tunnel stop demo
--data-dir ABSOLUTE_PATH proxy start
--data-dir ABSOLUTE_PATH proxy test example.org 443
--data-dir ABSOLUTE_PATH proxy stop
--data-dir ABSOLUTE_PATH agent stop
--data-dir ABSOLUTE_PATH logs
--data-dir ABSOLUTE_PATH watch
```

IDs are generated automatically. Tunnel operations accept an internal profile ID,
canonical Tunnel ID or unique alias. Ambiguous aliases/IDs are rejected. Removing
a profile is local only, and removing an account is blocked while profiles depend
on it. Port edits stop the affected connection and require reconnecting.

The UI supports the same essential account, discovery, profile, mapping and
connection actions. The CLI also imports settings/profile JSON. Exported status
and logs contain metadata, not credentials, but are not automatically safe to
publish publicly.

## Mapping and PAC

Listeners bind only to explicit loopback addresses (`127.0.0.1` or `::1`). Automatic
mapping tries the previous effective port, then the remote port, then the
configured range. Binding itself reserves the port; no external process is killed.
An IPv6 mapping is separate from IPv4; this version does not promise dual-stack
exposure through one mapping. TCP only, not UDP or transparent IP routing.

The client preserves independent TCP read/write completion, but end-to-end
half-close also depends on the remote host. The SDK's built-in TCP forwarder
currently closes both directions when one ends. Protocols that send EOF before
reading a response therefore need host-specific validation; ordinary bidirectional
TCP forwarding does not imply support for that pattern. See the
[upstream forwarder implementation](https://github.com/microsoft/dev-tunnels-ssh/blob/e65278de0bf73b7730d82a9469714677f5fc496a/src/cs/Ssh.Tcp/Services/StreamForwarder.cs).

In Settings, opt into the PAC server, choose its port and add domain rules tied to
a profile/remote-port pair. Configure the PAC URL manually in a compatible client:

```text
http://127.0.0.1:7777/proxy.pac
```

PAC follows the effective local mapping and emits `PROXY` or `SOCKS5`. Exact and
subdomain matching are configurable; more-specific domains win. Unmatched hosts
use `DIRECT`. A matched unavailable tunnel defaults to a non-working loopback
proxy. The per-rule **Direct fallback** option instead emits `DIRECT` while the
tunnel is unavailable, or `PROXY/SOCKS5 ...; DIRECT` while mapped. Actual failover
and PAC caching depend on the client, not the agent.

The **Proxy e PAC** page offers three PAC modes:

- **Rules**: generate PAC from profile/remote-port rules and effective mappings.
- **Local proxy**: direct PAC-aware clients to the Toolkit's local proxy, which
  applies the same policy as clients using a fixed proxy. Requires that proxy.
- **Manual**: edit or import a UTF-8 `.pac` (up to 65,536 characters), preview and
  export it. The agent serves it verbatim; it does not evaluate or verify the
  JavaScript. Only use trusted scripts. Switching mode preserves dormant rules.

PAC is a routing preference, not a security boundary: browser/OS implementations
may ignore it and non-PAC applications are unaffected. No OS proxy setting, DNS
server, route or firewall rule is changed.

## Selective local proxy

Enable **Proxy e PAC > Editar proxy** and choose a loopback address/port (default
`127.0.0.1:8888`). Configure that address as a fixed HTTP proxy in applications
that do not understand PAC. Applications that ignore HTTP proxies are not
captured; this is not a VPN, TUN device or transparent system-wide proxy.

Each rule accepts multiple destinations separated by commas or newlines, all of
the selected type (domain, IP or CIDR). Spaces and empty separator lines are
ignored; duplicates are matched once. Limits are 100 distinct destinations per
rule and 1,000 across all rules. Any invalid member rejects the entire rule.
Every destination shares the action, port, tunnel, upstream protocol and fallback.

Ordered rules match domain (optionally subdomains), literal IP or CIDR, plus a
destination port (`0` means any). First enabled matching rule wins. Actions are
**Direct**, **Tunnel** and **Block**, with a Direct/Block default. A Tunnel rule
selects a profile, remote proxy port and HTTP or SOCKS5 upstream protocol. The
mapped remote service must actually be a proxy. Effective mapping changes are
picked up automatically for new requests/connections.

The local proxy always routes by host and port, never URL path, including HTTPS
CONNECT. TLS stays end-to-end; no certificates are installed. Domain routing
does not resolve DNS just to classify a request. IP/CIDR rules therefore match
literal IP destinations only, not the resolved addresses of a domain. Tunnel
hostnames are resolved through the upstream; direct routes use local DNS.

Direct fallback is disabled by default and explicitly enabled per Tunnel rule.
It applies when no connected mapping exists or upstream TCP establishment fails
before any request bytes are sent. It does not bypass 403/407, retry a sent POST,
recover every failure occurring deeper in an established relay, or migrate open
connections. Fallback can expose destination DNS/traffic to the normal network.
Existing CONNECT streams must reconnect after a routing change.

The route simulator makes no connections or DNS queries. **Verificar acesso**
sends a bounded `HEAD /` through the running local proxy; **Verificar proxy remoto**
uses the matched tunnel's upstream proxy directly to help isolate a local versus
remote failure. The latter never falls back to the direct network. Both tests
validate TLS normally, do not follow redirects, send no credentials/cookies and
do not download response bodies. The UI reports elapsed time, HTTP status and
sanitized error categories, not raw upstream errors. The CLI equivalent is
`proxy probe HOST PORT http|https [--upstream]`.

An accepted local client or CONNECT response is not proof of destination access.
The displayed client count measures accepted local connections; use the access
test to verify an HTTP response. A failed remote DNS lookup or unreachable
destination must be resolved at that network, not by disabling TLS validation.
Discovery and other operations display progress and elapsed time. Unchanged
agent snapshots preserve the visible controls, hover and focus state.

Proxy/PAC listener
ports are reserved as destination ports to prevent recursive local loops.
Stop the proxy before changing timeouts/client limits; routing edits apply live.
Stopping it preserves profiles and rules, and disables PAC in Local proxy mode.

## Notification area and shutdown

On Windows, closing the window defaults to keeping the app in the notification
area. Reopen it by clicking the branded icon, using **Open**, or launching the
same data-directory instance again. The tray controls individual tunnels, the
proxy, logs and **Encerrar tudo**. Exit-all confirms and gracefully stops the
agent, listeners and UI while preserving accounts and configuration.

On Linux, tray mode requires a StatusNotifierWatcher and `gdbus`. If that
capability is unavailable, closing the window does not hide an unreachable UI.
The agent continues independently; reopen the app and use **Configurações >
Encerrar tudo**, or run `agent stop` via the CLI. The close-to-tray preference
can be disabled. `DevTunnelToolkit.App --tray` starts hidden only when a tray
host is detected. Agent-only/headless startup does not start a graphical tray.

## Startup and platform boundaries

The packaged agent can opt into startup through Settings or the supplied startup
wrapper. Windows uses HKCU Run; Linux uses a `systemd --user` unit. Registrations
are scoped to the data directory and ownership is checked before replacement or
removal. The command does not start a second process or stop an existing agent.
Source execution under `dotnet` and unsupported session managers disable startup.
An explicitly supplied headless credential key must also be available to the
startup executor. The application never copies that key into a startup entry.

LAN/WSL exposure, firewall changes, automatic system proxy application, tray
notifications, privileged helpers and automatic updates are not implemented.
They are not silently approximated by shell commands.

## Development and packaging

```sh
dotnet format DevTunnelToolkit.slnx --verify-no-changes --no-restore
dotnet test DevTunnelToolkit.slnx -c Release
node scripts/audit.mjs
node --test scripts/*.test.mjs
node scripts/package.mjs --rid linux-x64
```

Windows packaging runs on Windows with the verified Inno Setup 7 compiler set in
`INNO_COMPILER`. It generates both the portable ZIP and a per-user `-setup.exe`
installer for `win-x64` or `win-arm64` from the same staged files. CI installs
pinned Inno Setup with `scripts/install-inno.ps1`, checks both SHA-256 files and
installs, runs and uninstalls the x64 build on a Windows runner. A ZIP remains
available for users who prefer a portable copy. The installer does not bundle
the official `devtunnel` CLI. Uninstall removes the installed app and its owned
startup registration, but keeps profiles and credentials in user storage.
Close the desktop and stop the agent before a manual ZIP replacement; the
installer prompts to close locked applications during upgrades. Neither format
is currently Authenticode-signed, so Windows may show a publisher warning.

Windows executable and installer branding is derived from the approved logos in
`assets/branding/devtunnel-toolkit-logo`. After changing the source logos, run
`powershell -NoProfile -File scripts/build-branding.ps1` on Windows and review the
generated `packaging/branding/` assets. Main and dialog window icons also follow
the desktop's light, dark and high-contrast themes.

The desktop, CLI and agent must come from a compatible package. IPC version 7
adds independent services and publication bindings. Incompatible agents are
rejected with an actionable update message before requests are dispatched. No agent is
silently terminated because it may own active tunnels. The native Windows smoke
test uses a temporary isolated workspace, saves both CLI providers and verifies
persistence after restarting its own agent, without logging in or changing
remote resources. The installer smoke refuses to run over an existing installation.

The packaging script requires .NET on PATH (or `DOTNET_EXECUTABLE`). CI supplies
the pinned Microsoft SBOM tool through `SBOM_TOOL` and produces SPDX, build metadata
and checksums. Local packages without that tool are not release candidates.
Suite version comes from the repository's `version.txt`. Existing package files
are not overwritten. Review [the security model](SECURITY.md) and the
[architecture](ARCHITECTURE.md) before extending local capabilities.

Reference contracts: [Dev Tunnels SDK](https://github.com/microsoft/dev-tunnels),
[service properties](https://github.com/microsoft/dev-tunnels/blob/main/cs/src/Contracts/TunnelServiceProperties.cs),
[MSAL device flow](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/device-code-flow).
