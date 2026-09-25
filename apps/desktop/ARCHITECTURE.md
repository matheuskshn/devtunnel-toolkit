# Desktop architecture

```text
Avalonia App ---------+
                     +--- same-user IPC ---> Agent / Application
CLI -----------------+                         |
                           +-------------------+--------------------+
                           |                   |                    |
                        SQLite           Dev Tunnels SDK       Platform vault
                           |                   |                    |
                      profiles/logs     remote relay streams   protected tokens
                                               |
                                        loopback TCP listeners
```

`Core` contains models, validation and ports/interfaces. `Application` coordinates
operations and connection lifecycle, without owning a GUI. `DevTunnels` adapts
official management/connection SDKs and native account-scoped authorization.
For CLI-backed accounts the official CLI supplies device login, identity and
tunnel-scoped connect tokens; the SDK still owns tunnel metadata and relay
streams. Only allowlisted CLI operations are executed, without a shell.
`Networking` owns listeners and stream lifetimes, but not the SDK relay.
`Platform` implements private paths, credentials and optional per-user startup.
`Persistence` stores schema-versioned non-secret state. `Ipc` provides bounded
versioned frames and watch subscriptions. `App` and `Cli` are clients of that API.

The Application layer also owns the opt-in local HTTP/CONNECT proxy and PAC
server. Core selects the first matching ordered rule without resolving DNS.
Titanium.Web.Proxy handles HTTP framing and TCP establishment; the adapter
disables TLS interception, system proxy integration and integrated authentication.
Upstreams use current effective mappings, not hard-coded local tunnel ports.
PAC Rules, LocalProxy and Manual are distinct modes; manual JavaScript is never
evaluated inside the agent.

A single Avalonia instance per data directory owns the notification icon. Closing
a window can hide it without ending the agent. A separate same-user activation
channel focuses that instance; CLI/IPC shutdown acknowledges then cancels the
agent lifetime. IPC schema v5 includes desktop-owned interactive login and prevents
incompatible clients from silently losing proxy and PAC settings. Connection
snapshots also carry optional operation timestamps and phase metadata, so progress
survives command acknowledgement and reopening the UI. Schema-1/2 settings load
with safe defaults. Saving atomically
upgrades the database marker to schema 3, preventing old agents from silently
rewriting a state that contains new routing settings. Downgrade requires restoring
a pre-upgrade backup; it is not an in-place operation.

Windows/Linux adapters stay in one Platform assembly initially. Separate helper
executables would be warranted only when privileged features actually exist.
There is no privileged broker or arbitrary command endpoint in this version.

Account IDs and profile IDs are generated UUIDs, independent of remote Tunnel IDs.
This allows two native identities to refer to one remote tunnel without sharing
secrets. CLI-backed profiles share the official CLI's OS-keychain session and
must pass identity checks before and after scoped operations.
Discovery lists owned tunnels; shared access does not imply account enumeration.
Accessible shared tunnels can be adopted by canonical ID.

The mutation gate serializes profile/account changes. Long-lived login and relay
operations run outside it with independent cancellation. Stop cancels before
awaiting a worker; worker acquisitions of the gate use that cancellation token.
Effective-port persistence retains automatic selections across reconnects without
turning vanished discovered ports into manually required ports.

Relay reconnect is supervised in one layer with bounded exponential backoff and
jitter. Authentication-required and permanent mapping errors stop retries.
Stream forwarding limits concurrent clients and has bounded open/drain deadlines.
Half-close is preserved so a response can arrive after the client finishes sending.

The first schema stores an atomic JSON state in SQLite, plus bounded structured
events. Explicit schema versions prevent silently reading incompatible state.
Database access is serialized. The vault is a separate credential boundary; a
profile export is not a backup of credentials. Changing a data-directory scope
changes credential addressing and requires planned migration or new login.

The watch protocol publishes at most one snapshot per second per connection.
Clients have a fixed connection limit and bounded request/write deadlines.
Snapshots carry provider metadata and device challenges but never tokens or
traffic. GUI and CLI reconnect to the same agent rather than spawning competing
connection managers.
