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
  slots, 50 accounts, 200 client profiles, 100 host profiles and 100 configured
  mappings per profile. A remote deletion batch contains at most 25 explicit targets.
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
- Explicit remote-token issuance returns a scoped secret only to the same-user
  requester. Reveal/copy requires a UI action; the CLI hides it unless
  `--show-token` is present. Closing the dialog drops its reference. Clipboard
  contents and copied tokens are then the user's responsibility.
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

## Remote management and published proxies

Remote operations are typed requests, not an arbitrary CLI/shell endpoint. The
selected account must have the service's required permissions. Deletion, ACL
reset/removal and remote-port preparation require explicit target confirmation.
In-use resources in this agent must be stopped before changing ports, access or
HTTP options. Other devices cannot be stopped by this guard. Deleting a remote
resource is irreversible here; cancelling a request is not an undo operation.

Published HTTP/CONNECT and SOCKS5 listeners have independent policies, default
deny and explicit remote ports. Tunnel and port anonymous access is rejected
before hosting and rechecked for new connections. This is not an atomic lock on
server ACLs or token revocation: permissions changed outside this agent may take
effect between checks; existing streams require stopping the host to terminate.
Private egress needs explicit permission; loopback additionally requires an
IP/CIDR rule. Metadata/link-local destinations and the proxy's own listeners are
blocked. DNS results are checked and connections pinned to an allowed IP.
There is no automatic direct fallback, TLS interception or UDP/BIND support.

The allowed remote client gains the hosting computer's reachability for permitted
destinations, not the Hub's network. Keep policies narrow. Tunnel authentication
does not identify an individual HTTP request to the proxy; logs attribute events
to the managed service, not an asserted remote username. A shared loopback listener
does not prove which tunnel originated an HTTP request; no tunnel/user identity
is inferred from its peer address.

Service lifetimes are independent of publications. Bindings pin the running
service generation; stopping that service revokes its bindings and active
connections. Restart the publication after restarting the service. Other ports
remain available. An old binding must never connect to a different process that
reuses the service's former local port. Services must be stopped before changing
their listener/policy, so existing traffic cannot silently retain the old policy.
Removing a referenced service is rejected until its bindings are removed.

Access rules are evaluated in order; the first enabled matching rule wins.
General destination-port restrictions and CONNECT-specific restrictions are
additional gates. Explicit allow-all is limited to public addresses, without
weakening private-address, loopback, link-local or metadata safeguards.

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
