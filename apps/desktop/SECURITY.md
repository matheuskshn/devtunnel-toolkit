# Desktop security model

The trust boundary is the current OS user. The application is not a multi-user
server and cannot defend credentials against a malicious process already running
as that user, a debugger, an administrator, or a compromised desktop keyring.

## Controls

- Unix state directories are mode 0700, sockets and secret files mode 0600.
  Windows private-directory ACLs restrict inherited access and DPAPI uses CurrentUser.
- IPC uses private Unix sockets or `NamedPipe.CurrentUserOnly`, not a TCP API.
  Agent singleton locking prevents a normal second agent from replacing its IPC.
- Reparse points/symlinks are rejected for private paths and SQLite sidecars.
  Secrets are written using private temporary files and atomic replacement.
- Request frames are limited to 1 MiB and responses to 16 MiB. There are 32 IPC
  slots, 50 accounts, 200 profiles and 100 configured mappings per profile.
- Unknown operations and invalid model values fail closed. Extra JSON properties
  do not become commands, and user-supplied identity metadata is discarded.
  There is no shell endpoint.
- Microsoft cache blobs and GitHub credentials are isolated by account. A new
  identity cannot silently replace the owner of an existing authenticated account.
- CLI-backed profiles do not read or copy the official CLI's credential cache.
  The shared CLI identity is checked around each scoped command; switching it
  stops new operations for a mismatched profile. A separate process could still
  race a keychain change between checks, so CLI mode is not multi-account
  isolation. Use native accounts for that boundary.
- Secret files are DPAPI- or AES-GCM-protected; AES associated data binds each
  secret to its directory scope and reference. Key loss is not recoverable.
- Secrets are never serialized into snapshots, logs or profile exports. Raw SDK
  exceptions and HTTP bodies are not emitted to user-visible logs.
- Device codes are transient; native providers report deadlines, while the CLI
  currently does not report one to this interface. Browser launch allows only the
  explicitly recognized HTTPS identity-provider hosts, without arbitrary schemes.
- Local listeners bind only loopback; conflicting ports are never stolen.
- Startup is opt-in, per-user, ownership-scoped and independent of active sessions.
- PAC serves a bounded read-only endpoint and validates domain literals. It does
  not expose arbitrary files or accept configuration updates over HTTP.

## Proxy, PAC and tray boundaries

The HTTP/CONNECT listener is opt-in, loopback-only and implemented through the
MIT-licensed Titanium.Web.Proxy 7.0.11 library. TLS decryption, certificate trust,
automatic OS proxy changes, integrated Windows authentication and ambient
upstream discovery are disabled. Request framing and bounded transport handling
remain the library's responsibility. Library payload/URL logging is disabled;
Toolkit events contain routing codes and profile references only.

Routing is based on destination host/port. CIDR never causes local DNS queries.
Direct fallback is a per-rule opt-in and can expose DNS and traffic outside the
tunnel. The local proxy does not fall back on upstream authorization denials or
after transmitting a request. PAC-level failover is performed by clients and
has their semantics; it is not the same guarantee as proxy-level failover.
Existing CONNECT streams retain their route until disconnected.

Manual PAC is treated as untrusted text, size-bounded and stored as configuration.
It is not evaluated in the agent. PAC-capable clients execute that JavaScript;
only import scripts you trust. Do not put credentials in a PAC script, profile,
rule or exported configuration. The script is readable through the loopback
PAC endpoint and through same-user IPC/settings exports.

Tray activation uses a separate same-user pipe/private Unix socket and accepts
only a focus signal, not commands or filenames. Agent shutdown uses versioned
same-user IPC and does not kill unrelated processes or the official CLI.

## Limitations and operational requirements

Loopback forwarding is not per-process authentication: other users/processes with
local TCP access may reach these listeners. Do not treat a shared workstation as
isolated merely because the management socket is protected. Remote proxy ACLs,
authentication and OS policies remain necessary where that matters.

PAC is not a firewall. Its domain rules and local port metadata are readable by
local clients. Opt in only when that fits the workstation's trust model. Secret
Service detection means the client executable exists, not that its keyring is
unlocked; failures remain visible and do not trigger plaintext fallback.

Conditional-access policy can invalidate a token before its published expiry.
Automatic acquisition may refresh an access token but cannot override revocation,
MFA, sign-in frequency or tenant restrictions. Native Microsoft login needs a
permitted public-client registration and live validation in the target tenant.

Keep the data directory out of Git, Docker build contexts, support bundles and
screenshots. Exports omit tokens but may contain account identities, domains and
resource metadata. Public bug reports should use synthetic profiles instead.

Build outputs include third-party native libraries. NuGet audit and SPDX generation
are necessary but not proof that every bundled component has no vulnerability.
Release gates scan packages and require compatible Windows/Linux tests; native
ARM64 execution, desktop keyring/ACL behavior and signing need platform evidence.
Unsigned preview archives must not be represented as signed installers.

## Validation gates

Tests cover loopback-only mappings, conflicting ports, independent clients,
client-side half-close, cancellation/disposal races, SDK stream contracts, vault corruption,
secret-reference substitution, oversized frames, identity changes, SQLite
round-trips, PAC validation and UI command generation.

Client-side half-close tests do not guarantee end-to-end behavior with every host.
The SDK's built-in remote TCP forwarder can close both directions on EOF; validate
protocols that require a response after write-half-close against their actual host.

Before a release, additionally run an authorized real relay test, provider login
and refresh tests, native Windows/Linux desktop smoke, a second-OS-user IPC denial
test, locked dependency restoration, package scans and secret scanning. Do not
infer real-provider renewal or native-platform success from mocked tests.
