# devtunnel-toolkit-hub

An independent, multi-session Dev Tunnels host with one shared Squid proxy.
The existing toolkit, Squid, route-proxy and OpenVPN images are unchanged.
This directory is the **only build context** for the Hub image.

## Architecture

```text
User A: localhost:3140 -> private tunnel A:3140 -> SDK worker A -> 127.0.0.1:18001
                                                                               shared Squid -> allowed destination
User B: localhost:3140 -> private tunnel B:3140 -> SDK worker B -> 127.0.0.1:18002
```

The server performs port mapping. Clients use the standard `devtunnel connect`
command and always get local port `3140`. Each session has its own Microsoft or
GitHub login, persistent home, keyring, D-Bus session and SDK worker. The manager
allocates one immutable Squid listener per session and supervises workers.

This is logical isolation for trusted software processes, not a sandbox for
hostile tenants: they share a Linux UID, kernel, Squid and a trusted administrator.
Users never receive shell, container-exec, manager-socket or storage access.
The administration interface is a mode-0600 Unix socket, not a public web API.

## Quick start

See [Docker](examples/docker/README.md) and [Compose](examples/compose/compose.yml).
Supply configuration through environment variables or create a JSON file
**outside the repository**, using [the public example](examples/config.example.json).

```sh
docker build -t devtunnel-toolkit-hub:local images/hub
```

The image retains the `debian:bookworm-slim` base and bundles Node 22, Squid,
Dev Tunnels CLI, D-Bus, GNOME Keyring, certificates and tini at build time. It runs
as UID/GID 1000, without privileged mode, extra capabilities, Docker socket,
host networking or a VPN device. No packages are installed at startup.
The CLI version and architecture-specific SHA-256 are checked during build.
Upstream's moving download URL will fail the build on an unreviewed CLI update.

## Session commands

Run these through container exec as a trusted administrator:

```sh
hub session add user-a --provider microsoft
hub session login user-a
hub session start user-a
hub session status user-a
hub session list
hub session stop user-a
hub session logout user-a
hub session remove user-a
```

- `add` reserves a session ID, listener and naming template locally. Without
  `--tunnel-name`, the name stays unresolved until verified login.
- `login` returns a device-code prompt only to that administrative terminal.
  The user completes authentication in their own browser. Provider-confirmed
  login and stable ID are required; unknown CLI identity schemas fail closed.
- `start` validates the cached identity, creates the explicitly named remote
  tunnel on first use, ensures port 3140, rejects shared ACLs, and starts a worker.
  Subsequent starts reuse the persisted canonical ID, including its cluster.
- `stop` closes the worker and disables automatic restart for that session.
- `logout` also clears the CLI login; the identity binding is retained so another
  person cannot inherit the same session or its audit history.
- `remove` logs out and tombstones the session. It does **not** delete a remote
  tunnel or erase storage/backups. Names, IDs and listener allocations are never
  recycled. Remote deletion is a separate, explicit administrator operation.

Session IDs must be lowercase letters/digits/hyphens, 3-32 characters; names must
be 3-49 characters. A name collision fails, without inventing a random suffix or
adopting an unrelated tunnel. If the service confirms the remote resource no
longer exists, the manager attempts recreation with the exact same name and
cluster, after checking the cached identity. Authentication errors, denied
access, timeouts and unknown responses never authorize recreation. If the name
has been taken or the original cluster cannot be reused, intervention is required.
The CLI chooses the creation region. If it returns a different cluster, the
manager removes only that just-created resource and fails, retaining the old
canonical mapping instead of silently changing the client address.
The service can report a name conflict even immediately after confirming deletion.
That case becomes `TUNNEL_NAME_CONFLICT`, not a random-name fallback. Successful
reuse after service-side retention remains a separate acceptance test.

Two operations on one session cannot run together; one user's device login does
not block another user's administration. CLI operations within each home are
serialized, including credential refresh, to avoid token-cache races.

### Automatic fixed names

```dotenv
HUB_ID=devhub
HUB_TUNNEL_NAME_TEMPLATE={hub_id}-{username}
```

The default template uses the verified Microsoft login before `@`, or the verified
GitHub username, never the administrator's session alias. For example,
`alice@example.com` produces `devhub-alice`. The service supplies the cluster
suffix, such as `.use1`; it is not part of the template. Client port remains 3140.

Only `{hub_id}` and `{username}` are supported, and `{username}` is required.
Usernames are lowercased, runs of characters other than ASCII letters, digits and
hyphens become a hyphen, repeated hyphens collapse, and edge hyphens are removed.
Empty usernames or final names outside the 3-49 character naming rules fail;
names are never silently truncated. Collisions after normalization, including
different providers or tenants, fail with `SESSION_OR_NAME_RESERVED`. Choose an
explicit unique name for a new session using `--tunnel-name` to resolve a conflict.
Service-side conflicts still fail without adopting an existing tunnel.

The template is captured at `add`, resolved after verified login, and the name is
persisted before remote creation. Both filesystem and PostgreSQL preserve pending
templates and resolved names. Configuration changes affect only newly added
sessions. Existing names, including those created by older Hub versions or with
`--tunnel-name`, remain unchanged across restarts and login-name changes. No
automatic rename or remote migration is performed. Use a stable `HUB_ID` per
deployment, not a replica hostname, and do not include `.use1` in the template.

Usernames become part of the tunnel identifier. Use a manual non-identifying name
when this is undesirable; authenticated identity is still used for audit.
Before upgrading, back up state. Older Hub versions cannot read unresolved
sessions from this version; rollback requires a compatible state backup.

## Configuration and policy

All configuration fields also accept environment variables. Precedence is
**environment variables > JSON file > defaults**. Lists replace the entire file
value; they are not appended. Configuration is read at startup, not hot-reloaded.
`HUB_CONFIG` selects an optional JSON file; otherwise `/config/hub.json` is read
if present. An absent default file permits environment-only operation. An
explicitly selected missing file, unknown JSON keys or malformed configuration
stop startup. File validation is not bypassed by environment overrides.
Without an allowed domain list, every proxy destination is denied, unless the
administrator explicitly enables `HUB_ALLOW_ALL_DOMAINS=true`.

| Key                                 | Default                    | Purpose                                                             |
| ----------------------------------- | -------------------------- | ------------------------------------------------------------------- |
| `hubId`                           | `devhub`                 | Prefix for default fixed names; immutable for existing state        |
| `tunnelNameTemplate` | `{hub_id}-{username}` | Captured for new sessions; resolved once after verified login |
| `listenerStart` / `listenerEnd` | `18001` / `18999`      | Reserved internal listener pool                                     |
| `maxSessions`                     | `50`                     | Maximum non-removed sessions                                        |
| `allowedDomains`                  | `[]`                     | Exact DNS names, or`.example.com` for a domain and its subdomains |
| `allowedPorts`                    | `[80,443]`               | Destination ports                                                   |
| `connectPorts`                    | `[443]`                  | Allowed CONNECT destinations; subset of allowed ports               |
| `allowedProviders`                | `["microsoft","github"]` | Permitted session providers                                         |
| `allowedMicrosoftTenants`         | `[]`                     | Optional tenant-ID allowlist; empty permits any Microsoft tenant    |
| `healthPort`                      | `8080`                   | Liveness/readiness only; no identity or admin information           |
| `maintenanceSeconds`              | `300`                    | Remote policy/credential recheck interval                           |

| Environment variable | JSON key |
| --- | --- |
| `HUB_ID` | `hubId` |
| `HUB_TUNNEL_NAME_TEMPLATE` | `tunnelNameTemplate` |
| `HUB_LISTENER_START` / `HUB_LISTENER_END` | `listenerStart` / `listenerEnd` |
| `HUB_MAX_SESSIONS` | `maxSessions` |
| `HUB_ALLOWED_DOMAINS` | `allowedDomains` |
| `HUB_ALLOW_ALL_DOMAINS` | `allowAllDomains` (boolean, default `false`) |
| `HUB_ALLOWED_PORTS` / `HUB_CONNECT_PORTS` | `allowedPorts` / `connectPorts` |
| `HUB_ALLOWED_PROVIDERS` | `allowedProviders` |
| `HUB_ALLOWED_MICROSOFT_TENANTS` | `allowedMicrosoftTenants` |
| `HUB_HEALTH_PORT` | `healthPort` |
| `HUB_MAINTENANCE_SECONDS` | `maintenanceSeconds` |

Lists use comma-separated values, with whitespace around items ignored, not JSON
arrays. An unset variable preserves the file value or default; an empty variable
sets an empty list. Empty domains deny all destinations unless all-domain access
is explicitly enabled; empty tenants remove
tenant restrictions. Empty provider/port lists and empty scalar values are invalid.
Integers must use decimal digits. Values are validated with the same policy as
JSON and errors never echo their contents. If changing `HUB_HEALTH_PORT`, update
the deployment's HTTP probes to match. Existing state still enforces immutable
Hub identity and listener assignments.

`HUB_ALLOW_ALL_DOMAINS` accepts only `true` or `false`. When enabled, it bypasses
the domain allowlist, including for literal IP destinations. The remaining port,
loopback, link-local/metadata, local-source and enrolled-listener restrictions
still apply; tunnel authentication and tenant policy do not change. This grants
users access to all otherwise-permitted destinations reachable from the container,
including private networks. Enable it only as a deliberate deployment policy.
The default remains deny-by-default. `HUB_ALLOWED_DOMAINS=*` is still invalid.

Copy [`.env.example`](.env.example) to an external path such as
`/opt/devtunnel-hub/hub.env` and customize it for Docker or Compose:

```dotenv
HUB_ID=devhub
HUB_ALLOWED_PROVIDERS=microsoft
HUB_ALLOWED_MICROSOFT_TENANTS=00000000-0000-0000-0000-000000000000
HUB_ALLOWED_DOMAINS=service.example.com,.example.org
HUB_ALLOWED_PORTS=80,443
HUB_CONNECT_PORTS=443
HUB_MAX_SESSIONS=50
```

Replace the tenant placeholder with the permitted tenant UUID. In ACA, define
these same name/value pairs in the container's environment settings; no JSON
mount is required. Kubernetes can supply them via `env` or `envFrom`. User logins
remain isolated in persistent storage, not in environment variables.

From the repository root, the environment-only Compose example runs with:

```sh
HUB_ENV_FILE=/opt/devtunnel-hub/hub.env docker compose \
  -f images/hub/examples/compose/compose.environment.yml up -d --build
```

This example uses `env_file` to pass variables into the container. Compose's
`--env-file` alone only supplies interpolation values; it does not automatically
inject them into the container. The original `compose.yml` remains the JSON-file
alternative. The Hub itself does not load `.env` files.

For a corporate-only deployment, restrict providers to `microsoft` and configure
the tenant allowlist in external configuration. Device flow still depends on
the tenant's Conditional Access policy. A tenant allowlist does not bypass MFA.
Personal Microsoft accounts are not excluded by the default generic policy.

Only loopback clients and policy-permitted destinations/ports are allowed.
Loopback destinations, link-local/metadata addresses and Squid management are
denied first. All access rules precede a final `deny all`. Squid validates each
candidate configuration before reload. There is no anonymous tunnel access,
TLS inspection or forwarding of arbitrary ports. All-domain access requires the
explicit opt-in described above and does not create an anonymous public proxy.
Private tunnel and port ACLs must have no explicit grants. Owners must not
delegate access or share connection tokens; policy rechecks are periodic, not
an instantaneous revocation guarantee.

## Audit

Squid sends a minimal record through a private FIFO. The manager enriches it
using the immutable listener-to-session mapping and emits JSON on stdout:

```json
{"event":"proxy_access","session_id":"user-a","provider":"microsoft","user_id":"microsoft:tenant-example:subject-example","user_login":"user-a@example.com","tunnel_id":"devhub-user-a.use1","attribution":"session_listener","listener":18001,"destination":"service.example.com","destination_port":443,"method":"CONNECT","status":200,"bytes":1024,"duration_ms":42,"time":"2026-01-01T00:00:00.000Z"}
```

The login comes from the authenticated session, not the session alias or a
client-supplied proxy header. Attribution identifies the **tunnel session owner**,
not an independently authenticated person for every proxied request. Sharing
that tunnel's access also shares audit attribution.

Audit contains no full URL, path, query, request/response body, authentication
header, cookie, device code or access token. CONNECT logs authority and transfer
metadata only. Operational logs expose bounded error codes, not raw CLI/SDK
exceptions. Identity fields and destinations are still sensitive operational
data: restrict log access, retention and export. Stdout is not a durable audit
database; configure a reliable log collector and retention policy in deployment.

## Persistence and lifecycle

Choose `HUB_STORAGE_BACKEND=filesystem` (default) or `postgres`. There is no
automatic fallback or import between backends. Stop the old manager before
changing backend; moving existing logins requires a separately planned migration
or new user authentication. Keep `HUB_ID` stable across replacements.

### Filesystem / Azure Files

| Path         | Contents                                                               | Persistence                  |
| ------------ | ---------------------------------------------------------------------- | ---------------------------- |
| `/config`  | Deployment configuration, mounted read-only                            | Administrator managed        |
| `/data`    | State, immutable mappings, complete per-session homes/keyrings         | Persistent volume            |
| `/run/hub` | Private socket, D-Bus sockets, keyring control, FIFO, Squid config/PID | Ephemeral writable directory |
| `/tmp`     | Temporary runtime files                                                | Ephemeral writable directory |

`HUB_DATA_DIR` and `HUB_RUN_DIR` can relocate the two runtime paths. Use an
absolute path without whitespace or shell characters. Storage must be writable
by UID 1000. Do not mount `/data` into other workloads or expose it to users.

Unattended GNOME Keyring uses an empty unlock password. **This is not encryption
at rest.** Protect the volume with platform encryption, restrictive mounts and
storage RBAC, and treat every snapshot/backup as containing credentials.
Sockets and PIDs are recreated; persistent homes are retained across restarts.

The entrypoint holds a kernel `flock` for the manager's lifetime. A second manager
using the same data volume exits with code 75. It does not delete a stale PID
file to steal ownership. Only one active manager is supported. Cross-host storage
locking is a deployment acceptance gate, not a guarantee inferred from Docker.

### PostgreSQL

Provision a dedicated database first. The Hub creates versioned tables in the
`devtunnel_hub` schema transactionally; the initial role needs schema/table creation
permissions. Use a dedicated restricted role for production, and do not grant
database or container administration to tunnel users. PostgreSQL 16 is covered
by integration tests. Use a direct connection or session pooling, never transaction
pooling: the manager holds a session-level advisory lock for its lifetime.

| Variable | Purpose |
| --- | --- |
| `HUB_STORAGE_BACKEND=postgres` | Select the database backend; `/data` is unused |
| `HUB_PG_HOST`, `HUB_PG_DATABASE`, `HUB_PG_USER`, `HUB_PG_PASSWORD` | Required database connection settings |
| `HUB_PG_PORT` | Port, default `5432` |
| `HUB_PG_SSLMODE` | Default `verify-full`; `disable` is only for isolated local tests |
| `HUB_PG_CA_FILE` | Optional mounted PEM trust bundle; otherwise use system trust |
| `HUB_CREDENTIAL_KEY` | Required random 32-byte key, base64 encoded |
| `HUB_CREDENTIAL_KEY_ID` | Active key label, default `primary` |
| `HUB_CREDENTIAL_PREVIOUS_KEYS` | Optional JSON map of previous key labels to base64 keys |

TLS checks the server certificate and hostname. Inject the password and encryption
key using platform secrets, not image layers, source files or command arguments.
Generate the encryption key once, for example with `openssl rand -base64 32` in a
private administrative environment, and retain it across revisions. Back it up
separately from the database. Without the correct key, credentials cannot be restored.

State, fixed names, listener assignments, identity bindings and tombstones are
stored as a revision-checked JSON document. Credential packages are separate
AES-256-GCM authenticated ciphertext, bound to Hub ID, session ID and generation.
Session metadata is **not** application-encrypted; protect database access,
platform encryption and backups accordingly. Audit still goes to stdout.

The CLI requires real files and Secret Service. On startup the Hub restores each
home into a new private directory under `HUB_RUN_DIR`. D-Bus, sockets, PIDs and
keyring processes are recreated, not serialized. CLI operations for a session are
serialized; after each operation, including failed commands that may have changed
tokens, its keyring is stopped before the home is encrypted and committed. The
CLI's `.net` extracted binaries are excluded and regenerated locally. Archive
size/file-count limits, link rejection and path validation bound restoration.
Local homes are plaintext inside the trusted container, mode 0700/0600; use tmpfs
where available and never mount this runtime directory into another workload.
Graceful shutdown removes its temporary homes; container replacement discards
ephemeral storage. An abrupt process exit can leave private files until replacement.

A durable dirty marker is written **before** each credential operation. If the
process crashes before checkpoint completion, only that session requires login
again, instead of silently restoring a potentially superseded token cache. A clean
checkpoint restores without an interactive login, subject to provider validity.
Logout checkpoints credential deletion, and removal preserves audit tombstones.
Backups can still contain earlier logins; apply retention and provider revocation.

The database lock prevents a second active manager for the same database/Hub ID
(exit 75). The manager monitors its dedicated connection and terminates workers
if ownership becomes uncertain. There is no reconnect-with-stale-state or local
fallback. Database outages interrupt all sessions. Keep one replica and use
stop/drain/start deployments; this is not active-active hosting or a zero-overlap
guarantee under arbitrary network partitions/process suspension.

For key rotation, supply a new active ID/key plus the old key in
`HUB_CREDENTIAL_PREVIOUS_KEYS`, then restart. Subsequent checkpoints use the new
key. Retain old keys until **all** required packages and retained backups have
been migrated or retired. Changing only the key without the previous key fails
closed. Database backups and the matching keys are both required for recovery;
test restoration into an isolated deployment before using restored credentials.

### Session lifecycle

On restart, sessions whose desired state is running try to resume. A failed or
expired login marks just that session `reauth_required`; it does not restart
healthy users. Transient failures get bounded exponential retries. Policy and
identity mismatches need intervention. SIGTERM stops workers and session services
and saves state; configure at least 30 seconds of termination grace.

The SDK asks the manager for fresh host credentials over private process IPC.
Credential rechecks also inspect remote ACLs and ports. Every 12 hours of active
operation, the manager requests a two-day tunnel-resource expiration. This is
separate from login/token renewal, and the service may apply its own limit.
No particular 24-hour or 30-day login lifetime is promised. Revocation, MFA and
Conditional Access can require a fresh user login at any time.

## ACA and Kubernetes

The [ACA template](examples/aca/containerapp.yaml) uses environment variables for
configuration, Azure Files for data, and `/tmp/hub` for ephemeral runtime files. It has no ingress and
one replica. Register shares in the managed environment separately; keep all
real Azure identifiers, keys and final manifests outside this repository.

The [PostgreSQL ACA template](examples/aca/containerapp.postgres.yaml) instead
uses database persistence, platform secret references and ephemeral `/tmp/hub`.
No Azure Files share or storage mount is needed. Provision the database and ACA
secrets separately, and verify private DNS, port access and trusted database TLS
from the container's network. The same variables work with Docker, Compose or
Kubernetes; omit the data volume for this backend, but retain private writable
runtime directories and all container security controls.

`Single` revision mode and `maxReplicas: 1` do not prevent rollout overlap between
revisions. Use a stop/drain/start rollout for v1, accept downtime, and verify
locking from two actual replicas against the same mounted share before use.
If SMB locking or directory fsync does not behave correctly, do not bypass the
guard. A platform lease implementation or different storage strategy is required.
Scale-to-zero, active-active hosting and high availability are not supported.

The [Kubernetes template](examples/kubernetes/hub.yaml) uses a PVC, `Recreate`,
non-root security context, probes and no Service/Ingress. The actual CSI driver
must honor permissions and locking. Network/DNS reachability to destinations and
outbound access to authentication, management and relay services are deployment
responsibilities. Resource values are starting points, not validated capacity.

## Local verification and release gates

```sh
cd images/hub
npm ci --ignore-scripts
npm test
npm run check:public
npm audit --omit=dev --audit-level=high
docker build -t devtunnel-toolkit-hub:local .
npm run test:container
npm run test:postgres
```

Tests use synthetic identities and isolated Docker resources, clean them up, and
never require cloud credentials. They cover actual SDK SSH forwarding to real
Squid listeners, HTTP and CONNECT, destination denial, audit redaction, session
state and concurrency, separate D-Bus/keyrings, keyring persistence, non-root
read-only execution, storage lock, restart and SIGTERM. In-memory SSH transports
are test-only; these tests do not establish connectivity to the real relay.

The PostgreSQL suite starts an isolated PostgreSQL 16 container without published
ports. It checks encrypted real keyrings, fresh-directory restoration, per-user
isolation, interrupted checkpoints, key rotation, deletion, competing managers,
SIGKILL/replacement and fail-closed connection loss. It needs no cloud credentials.

Before release, also verify:

- Real Microsoft and GitHub CLI identity/tunnel/token JSON contracts and
  browser/device flow with externally stored credentials.
- A stock CLI client through the actual relay, two separate real accounts,
  restart with cached login, ACL rejection, and session-specific revocation.
- Token expiry/refresh and eventual interactive reauthentication over time.
- The target ACA Azure Files mount, permissions, lock contention and rollout.
- Or, for PostgreSQL: verified TLS, database permissions, credential restoration,
  lock contention, connection-loss shutdown and stop/drain/start rollout.
- ARM64 build/runtime and a container OS/package vulnerability scan.

The SDK 1.3.56 depends on `uuid` 3.4.0, which is affected by the
[GHSA-w5hq-g745-h8pq advisory](https://github.com/advisories/GHSA-w5hq-g745-h8pq)
with no compatible automatic fix. The SDK code calls `v4()` without a supplied
buffer; the advisory describes v3/v5/v6 buffer handling. This narrows observed exposure, but is not a
waiver or an upstream fix. Review before release; do not force a major override.

## Image publication

The dedicated [Hub workflow](../../.github/workflows/hub.yml) publishes development snapshots of
`ghcr.io/<repository-owner>/devtunnel-toolkit-hub` for `linux/amd64` and
`linux/arm64`. Publication requires successful unit/policy tests, dependency
audit at the high-severity threshold, local container smoke and both architecture
builds. Only the publishing job receives registry credentials and package-write
permissions. Images include build provenance and an SBOM.

| Event | Result |
| --- | --- |
| Pull request changing Hub files | Tests and builds only; no registry login or publication |
| Push to `main` changing Hub build inputs | Publish `edge`, `main` and a unique commit/run snapshot |
| Stable tag such as `v1.2.3` | Suite coordinator publishes all five images and completes a GitHub Release |
| Prerelease tag such as `v1.2.3-rc.1` | Suite coordinator publishes exact RC versions, not stable aliases |
| Manual Hub run | Rebuild and publish a unique snapshot; only `main` runs update `edge`/`main` |

On ordinary pushes and PRs, only changes to `src/`, `bin/`, the Dockerfile, `.dockerignore`, package manifests
or TypeScript configuration select a Hub image build. Documentation, examples,
tests and workflow-only edits can run checks but do not build or publish images.
The shared selector compares push ranges and PR merge bases. Manual executions
rebuild all images managed by the chosen development workflow. Version tags use
the separate suite release coordinator, which handles all five images and reuses
verified candidates on retries. See
[workflow selection](../../.github/README.md).

Docker Hub publication is optional. Configure repository secrets
`DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`; optionally set repository variable
`DOCKERHUB_NAMESPACE` (otherwise the username is used). The image name is
`docker.io/<namespace>/devtunnel-toolkit-hub`. GHCR uses the workflow's
`GITHUB_TOKEN`, not a personal token stored in source. Registry visibility and
pull permissions must be checked before consumers use the image.

The repository-wide Docker workflow is separate and uses the same selector to
build only affected existing images. A Hub-only change does not rebuild them.
Running the Hub workflow manually does not start that other workflow. Publishing
a build does not replace the deployment acceptance checks above.

The Hub package version follows the suite's `version.txt`; Release Please keeps
both package manifests synchronized. See [the release guide](../../RELEASING.md)
for version PRs, stable/RC policy, immutable version tags and recovery.

## Public-repository boundary

Never put real identities, internal destinations, IPs, Azure resource names,
tokens, keyrings, data volumes, deployment evidence or real `.env` files in this
checkout. Tests and examples use synthetic values. The Docker context uses a
source-only allowlist, excluding examples, tests and local artifacts. The
credential scanner is a supplemental check, not proof that arbitrary internal
data is absent. Human diff review is required before every commit or publication.

References: [Dev Tunnels CLI](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands),
[Dev Tunnels security](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security),
[Squid log formats](https://www.squid-cache.org/Doc/config/logformat/).
Storage references: [PostgreSQL advisory locks](https://www.postgresql.org/docs/16/explicit-locking.html#ADVISORY-LOCKS),
[node-postgres TLS](https://node-postgres.com/features/ssl),
[ACA secrets](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets).
