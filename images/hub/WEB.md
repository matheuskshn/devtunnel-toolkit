# Hub web console

The optional console is served by the existing Node process in the Hub container.
Its static HTML, CSS, JavaScript and SVG icons need no CDN, frontend runtime,
external fonts or additional server. It works with either filesystem storage
(including a supported mounted file share) or PostgreSQL.

The login page and console header include a light/dark theme toggle. The initial
theme follows the operating system. An explicit choice is stored only in this
browser's local storage and synchronized across tabs of the same origin; it is
not a Hub policy or a user credential. If browser storage is blocked, switching
still works for the current page. Both themes include forms, dialogs, tables and
status indicators. Icons are bundled inline SVG with no external requests.

## Enable locally

Build the Hub image and use the [web Compose example](examples/compose/compose.web.yml).
Copy [.env.example](.env.example) **outside the repository** and set:

```dotenv
HUB_WEB_ENABLED=true
HUB_WEB_PORT=8082
HUB_WEB_ORIGIN=http://localhost:8082
HUB_WEB_KEY=<a separate random 32-byte base64 key>
HUB_WEB_REQUIRE_PASSWORD_CHANGE=true
```

Generate the key with `openssl rand -base64 32` and store it in your executor's
secret mechanism. Do not reuse `HUB_CREDENTIAL_KEY` or commit either key. Keep the
same web key for restarts, container replacement and backup restoration. Loss of
this key makes console users, password hashes, provider secrets and saved policy
unrecoverable. Changing it directly is **not** a supported rotation procedure.

```sh
HUB_ENV_FILE=/absolute/path/hub.env docker compose \
  -f images/hub/examples/compose/compose.web.yml up -d --build
```

The example publishes **only** `127.0.0.1:8082`. Open `http://localhost:8082`.
HTTP origins are accepted only for loopback development. Do not publish the
loopback-development configuration to a shared network.

### Recovery administrator

There is no default password. The local account `admin` is reserved for recovery
and cannot be disabled, deleted, renamed or demoted. Bootstrap or recover it from
a trusted terminal while the manager is running:

```bash
read -r -s -p 'Temporary admin password: ' hub_admin_password
printf '\n'
printf '%s\n' "$hub_admin_password" | docker compose \
  -f images/hub/examples/compose/compose.web.yml exec -T hub \
  hub admin reset-password admin --password-stdin
unset hub_admin_password
```

Use the same Compose environment/file selection as at startup. Inside a
container, the command is `hub admin reset-password admin --password-stdin`.
Enter the password on stdin and terminate input with EOF, or pipe it from a
trusted secret source. Never place passwords directly in command arguments.
Passwords require at least 14 characters. Reset revokes existing browser sessions
for that account and marks its password as pending a user change. By default,
`HUB_WEB_REQUIRE_PASSWORD_CHANGE=true` blocks access until the change is made.
Set it to `false` to allow normal access while showing a persistent warning with
an **Alter password** action. The warning cannot be dismissed; it disappears only
after the user actually changes the password. This applies to local accounts
after creation or reset, including the recovery administrator, not IdP passwords.
Only the exact strings `true` and `false` are accepted; restart to apply changes.
Disabling enforcement does not clear the persisted pending flag: re-enabling it
requires the change for any account still pending. The CLI reset result reports
`passwordChangePending` separately from `passwordChangeRequired`.
The CLI can
also reset another **local** account by username. It does not reset IdP passwords.

## Production networking

Administrators can configure the published tunnel/client proxy port in
**Configurações > Editar política > Porta do proxy**. The environment equivalent
is `HUB_PROXY_PORT` (default 3140). Stop all sessions before applying; restart the
sessions and reconnect clients to complete the change. This is not `HUB_WEB_PORT`
and does not change container ingress or audit listeners. See the
[proxy port lifecycle](README.md#published-proxy-port) for migration and recovery.

The same policy form enables **SOCKS5 TCP CONNECT** and configures **Porta SOCKS5**.
Environment equivalents are `HUB_SOCKS_ENABLED=false` and `HUB_SOCKS_PORT=3180`.
SOCKS5 is optional and disabled by default. It uses a separate per-session local
adapter that sends HTTP CONNECT through that session's Squid listener, so domain,
destination-port, metadata protection and audit rules remain centralized. BIND
and UDP ASSOCIATE are not supported. Stop sessions to change either published
port or enablement, then start them and reconnect clients. Existing published
ports and pending changes are distinguished in the session mapping/details.

Set `HUB_WEB_ORIGIN` to the exact external HTTPS origin, including a non-default
port when applicable. Configure the platform ingress/reverse proxy to terminate
TLS, forward to `HUB_WEB_PORT`, and preserve the original `Host` header. HTTP
requests with a different Host are rejected. Forwarded headers are deliberately
not trusted to derive origins, identities or rate-limit keys.

Publish only the console port through an authenticated, restricted management
network path. Do not expose Squid listeners, the manager socket, health port or
container exec to console users. Health probes still use `/live` and `/ready` on
`HUB_HEALTH_PORT`, not the web port. Keep the existing single-active-manager rule
and graceful shutdown configuration. This console does not change cloud resources.

The browser uses HttpOnly, SameSite=Lax cookies, with Secure and the `__Host-`
prefix over HTTPS. Sessions expire after 30 minutes idle or 8 hours absolute;
container restart logs browsers out, but retains users, providers, policies and
tunnel credentials. CSRF tokens and exact Origin checks protect mutations.
Security headers prohibit third-party scripts, embedding and inline execution.
At most two authentication operations run at once. Rate limits are local to the
manager and use the direct peer address; users behind a reverse proxy share its
IP budget. Add perimeter rate limiting for larger deployments.

### Live updates (SSE)

One same-origin, cookie-authenticated `GET /api/events` connection per visible tab
replaces periodic polling as the normal update mechanism. The stream contains
only small invalidation notices, never user identities, logs, device codes or
credentials. Notices are computed from each user's authorized view; the browser
fetches updated snapshots through the existing permission-checked HTTP APIs.
HTTP POST remains the only path for mutations and continues to require CSRF and
exact Origin validation. Query parameters and cross-origin event streams are
rejected; no credentials or session tokens belong in an SSE URL.

The manager checks browser session expiry, user/provider epochs and password
policy at least once per second while connected. Passive SSE and background
refreshes do not extend the 30-minute idle timeout. Logout, account/provider
revocation and storage fencing close the affected streams. At most four streams
per user and 128 per manager are accepted, with a 30-connections-per-minute user
limit. Writes are bounded to 16 KiB of queued data; slow clients disconnect and
resnapshot rather than grow an unbounded event replay queue.

Heartbeat events run every 15 seconds. Every connection/reconnection requests a
fresh authorized snapshot and resets the browser's log cursor, including after
container replacement. `Last-Event-ID` does not replay old payloads. The existing
bounded log retention still applies, so SSE is not a durable delivery guarantee.
The UI coalesces notifications, updates logs without redrawing the session panel,
and preserves filter focus, dialogs, form values and scroll positions. Hidden
tabs pause their connection; visible tabs reconnect. If SSE is unavailable or
temporarily fails, a labeled 10-second polling fallback keeps the panel usable.
A silent connection exceeding 45 seconds without a heartbeat is reconnected.

Reverse proxies must forward streaming responses without buffering and permit
long-lived HTTP connections. The server sends `X-Accel-Buffering: no` and
`Cache-Control: no-store, no-transform`; configure equivalent ingress behavior
where required. The stream uses the same console port and origin, with no extra
container port or WebSocket upgrade. Validate ingress reconnect behavior in the
target executor; local tests do not prove a particular cloud ingress setup.

## Login providers

An administrator configures providers under **Authentication**. Client secrets
and LDAP bind passwords are write-only; empty secret fields on edit retain their
current values. Disable a provider to immediately invalidate its browser sessions.
Changing a provider also invalidates those sessions. Its identity namespace
(issuer/client ID or LDAP directory/stable-ID attributes) is immutable: register
a new provider ID when changing these fields to prevent account takeover.

| Type                | Required setup                                                                                                                                       |
| ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| Microsoft corporate | Confidential Entra application, tenant-specific issuer `https://login.microsoftonline.com/TENANT-UUID/v2.0`, client ID/secret, callback registration |
| GitHub              | OAuth application, client ID/secret and callback registration                                                                                        |
| OIDC                | HTTPS issuer with discovery, confidential client ID/secret and callback registration                                                                 |
| LDAP                | `ldaps://` endpoint, base DN, read-only lookup bind DN/password, login attribute and immutable unique ID attribute                                   |
| Local               | Administrator creates a local user and temporary password in **Users**                                                                               |

The callback is always `<HUB_WEB_ORIGIN>/auth/callback`, shown in the panel. OAuth
uses authorization code + PKCE and browser-bound, one-use state; OIDC also checks
nonce, issuer, audience, expiry and ID-token signature. Provider access tokens
remain on the server for the login exchange and are not stored or returned.

Microsoft uses an explicit tenant, not `common` or `organizations`. Apply MFA and
conditional access in the external IdP. This version does not implement local
MFA, SAML, SCIM, directory group synchronization, passwordless login or logout of
the user's external IdP session. Generic OIDC can connect to another identity
broker when those capabilities are needed.

LDAP requires verified TLS, optionally with a CA certificate configured in the
panel. Plain LDAP and StartTLS are not accepted. The lookup must return exactly
one entry with a stable ID; a separate TLS connection binds as that user to
validate their password. Use `entryUUID` where supported, or `objectGUID` for
Active Directory. Referral following and anonymous/empty-password login are not
allowed. The directory must be reachable from the executor and trust must be
configured correctly.

External registration is optional. When enabled, a successful first external
login creates a **disabled, pending viewer**. An administrator approves it under
**Users**, selects its role and grants session IDs. Registration alone grants no
access. Accounts are matched by provider ID + stable subject, never email or
display name, and accounts are not automatically linked across providers.

## Permissions and operations

| Capability                                        | Administrator | Operator          | Viewer            |
| ------------------------------------------------- | ------------- | ----------------- | ----------------- |
| Session status and filtered proxy/manager logs    | All sessions  | Assigned sessions | Assigned sessions |
| Login, start, stop and logout of tunnels          | All sessions  | Assigned sessions | No                |
| Create/remove Hub sessions                        | Yes           | No                | No                |
| Users, grants, local password reset and providers | Yes           | No                | No                |
| Runtime policy                                    | Yes           | No                | No                |
| Change own local password                         | Yes           | Yes               | Yes               |

All current `hub session` operations have structured UI equivalents. List/status
are visible on the sessions screen; mutations run as bounded background jobs.
The health indicator reports manager readiness. Password recovery remains
available through the protected Unix socket. Starting/stopping the container
itself remains the executor's responsibility. There is no arbitrary shell,
free-form CLI argument execution, file browser or token export endpoint.

Console identity is separate from tunnel identity. Tunnel login uses the
official device-code flow in a modal; only the initiating console user can see
that code. The console never sees the tunnel account's password. A tunnel's
verified identity and immutable listener continue to own its proxy audit trail.
Administrator actions add a separate console actor to the management audit.
Removing a session tombstones it; it does not delete the remote resource.

Logs in the UI are a bounded in-memory buffer (1,000 server events, at most 300
per response). Jobs are bounded to 100 retained entries and 8 running operations;
completed jobs are pruned on admission after 10 minutes. Device codes are removed
at completion. For durable audit retention, collect the JSON stdout stream with
your platform's logging system. Raw CLI streams, tokens, HTTP paths and headers
are not exposed in the logs. Browser sessions and unfinished OAuth flows are not
restored after a restart. Directory-side account disablement is observed on the
next login; use the Hub user's disable action to revoke a current browser session
immediately.

## Policy and persistence

The panel edits domain/port policy, tunnel providers/tenant allowlists, the naming
template, session limit and maintenance interval. **All tunnel sessions must be
stopped** before applying a policy. The manager validates the candidate with
Squid and saves it; failure rolls back the running configuration. Names already
captured for existing sessions do not change.

Saved web policy overrides matching environment/JSON values while the console
is enabled. Fields that were never saved still follow environment > JSON >
defaults. Hub ID, listener pool, health port, web origin/key, storage backend and
database credentials remain executor settings, shown as deployment-level
configuration rather than unsafe in-process changes. Disabling the web console
returns operating policy to environment/JSON; review it before restarting.

The complete control-plane document is AES-256-GCM encrypted under `HUB_WEB_KEY`
inside the existing state snapshot. Both storage backends persist it atomically
with their normal locking/CAS. Local passwords are scrypt hashes inside that
encrypted document. Filesystem deployments require a durable data mount;
PostgreSQL deployments need no persistent local volume. Existing credential
packages still use their own encryption key and per-session temporary homes.
Back up state and both keys separately. Restoring an older backup may restore old
permissions or passwords; review them before enabling ingress. Do not run an
older, console-unaware image against live web state without a reviewed rollback.

## Local validation

```sh
cd images/hub
npm ci --ignore-scripts
npm test
npm run check:public
docker build -t devtunnel-toolkit-hub:local .
npm run test:container
npm run test:postgres
```

Tests use synthetic identities and disposable local resources. Protocol tests
mock GitHub/OIDC endpoints and LDAP binds; actual external logins require a
registered application or reachable directory with test accounts. Passing local
protocol tests does not certify an organization's IdP policy or directory setup.
