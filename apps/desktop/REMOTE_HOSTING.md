# Services and DevTunnel management

The Desktop separates local services, remote resources and client connections.
**Serviços** runs HTTP/CONNECT and SOCKS5 proxies without a tunnel or account.
**DevTunnel** manages resources held by Microsoft's service and publications of
local services/ports through them. **Conexões** consumes a remote tunnel as a
client. Removing a local profile does not delete a remote tunnel; stopping a
publication does not stop its services or delete remote ports/permissions.

## Management workflow

1. Authenticate an account in **Contas**.
2. Select that account in **DevTunnel** and refresh the list.
3. Create a private tunnel or open an existing canonical ID.
4. Use its tabs to edit metadata, ports, access rules or issue a scoped token.
5. Adopt it as a client profile or create a hosting profile separately.

The server remains the authority for permissions, quota and expiration. A valid
client connection does not imply management permission. Tunnel-token accounts are
not management accounts. CLI-backed profiles reuse the official CLI session and
check its identity; use native accounts for independent concurrent identities.

| Capability | Desktop surface | Important boundary |
| --- | --- | --- |
| List, show, create, update, delete | Remote list and details | Explicit account and canonical target; no implicit last-used tunnel |
| Description, labels, expiry and HTTP options | Remote editor | Stop this agent's dependent connections/hosts before updating |
| Port create, edit, delete | Ports tab | Existing port settings are not overwritten by hosting preparation |
| Access list, add, remove, reset | Access tab, tunnel or port scope | Inherited entries remain read-only; destructive changes require confirmation |
| Scoped token issuance | Masked token dialog | Transient secret; no state/log storage; unknown expiry stays unknown |
| Multiple deletions | Explicit selection, up to 25 | Confirm every target; partial results reported; no unconditional delete-all |
| Start HTTP/CONNECT and SOCKS5 services | Serviços | Independent loopback listeners, policy, limits and optional autostart |
| Publish services and existing TCP ports | DevTunnel publications | Explicit service-ID bindings or local endpoints; no implicit listener creation |
| Stop/restart hosted relay | Hosting card and tray | Controls this agent only, not another computer's process |
| Service clusters and API timing | Remote diagnostics | API timing is not relay traffic latency or a regional benchmark |
| Echo and mapped-port probe | Hosting diagnostics | Bounded 32-byte challenge; only an existing connected mapping |

The functional interface is not an unrestricted mirror of all official CLI flags.
There is no arbitrary command execution, implicit temporary hosting, global
delete-all, remote-process termination, cluster latency sweep or CLI cache
`set`/`unset`. The echo probe is the Toolkit's bounded TCP protocol, not every
mode of the official diagnostic echo command. Dev Tunnels itself is a preview
service; consult the [official command reference](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands).

## Publish a proxy

1. Create an HTTP/CONNECT or SOCKS5 service in **Serviços**. Set its name, local
   loopback address/port, policy and limits. The defaults are `3140` and `3180`;
   both are configurable and an automatic local port can avoid conflicts.
2. Configure ordered allow/deny rules with domains/IPs/CIDRs and destination
   ports/ranges. Multiple destinations separated by commas or newlines share the
   rule. Empty rules deny all unless public-destination fallback was explicitly
   enabled. Private IPs and loopback always need an explicit matching allow rule.
3. Start the service and inspect its effective listening address. No tunnel
   account, remote resource or remote write is involved in this step.
4. In **DevTunnel**, select an existing private tunnel or explicitly create one.
   Add a publication binding from a remote port to the selected local service.
5. Use **Preparar portas** if the selected remote ports do not exist. Review and
   confirm the target; this creates only missing selected ports.
6. Start publishing. The card shows remote validation, relay connection,
   readiness, failures and retry time. The agent owns the relay; the service
   registry owns its independent listener. Stopped services must be started first.
7. From another authorized client, map the remote proxy port and configure the
   resulting local endpoint as HTTP or SOCKS5 in the consuming application.

This uses the Desktop host's network as the exit point. It does not provide a
corporate network path that the computer does not already have. The personal
**Proxy e PAC** router is separate and is never published automatically.

For an existing service, select **Serviço local**, its explicit loopback address
and port, and a remote port. That service must already be running. The agent does
not start arbitrary applications, edit OS proxy settings or change firewall rules.
Proxy-specific allowlists cannot restrict the behavior of an independently hosted
service. Only publish services you intend to expose to the tunnel's authorized clients.

### Ports and lifecycle

| Port concept | Example | Meaning |
| --- | --- | --- |
| Service listener | `127.0.0.1:3140` | Where this computer accepts proxy clients |
| Allowed destination | `80,443,8000-8100` | Where the proxy may connect |
| Published tunnel port | `9000` | The remote port bound to this service |

General destination ports apply to all requests. CONNECT ports are an additional
restriction for HTTP CONNECT and SOCKS5 TCP CONNECT, not another listening port.
Empty port filters impose no extra port restriction on an allowed destination.
HTTPS rules match destination host/IP and port, not encrypted URL paths.

One service may have bindings in multiple different tunnel resources, without
duplicating its listener. Stop/edit the service independently of these resources.
Stopping it closes its connections and revokes the existing bindings, preserving
unrelated ports in the publication. After starting it again, explicitly restart
the affected publication to acquire its new listener generation. This prevents
an old binding from forwarding to an unrelated process that reuses the old port.
Unlink a service from all publications before removing it.

### Which computer hosts a tunnel?

Creating a remote resource does not permanently assign it to that computer. The
UI distinguishes a live publication in this Desktop, another reported active
host, no reported host, and unknown status. A saved local profile alone is not
evidence that this Desktop is hosting. The remote metadata does not reliably
identify another computer's hostname or label it as a Hub.

The supported relay flow currently permits one active host per tunnel. Starting
a second host may disconnect the first, even if their intended remote ports
differ. Use separate tunnel IDs for simultaneous Desktop and Hub publications.
Consuming a Hub port as a Desktop client is independent and does not take over
hosting. Moving a resource to another host requires an explicit handoff; adding
a remote port record alone does not start a service or split hosting between
machines. See the [official relay implementation](https://github.com/microsoft/dev-tunnels/blob/main/cs/src/Connections/TunnelRelayTunnelHost.cs).

## Safety and lifecycle

- Published proxies require private tunnel/port access and default-deny egress.
  Metadata/link-local targets, recursive proxy connections, UDP and SOCKS BIND are
  rejected. No certificates are installed and HTTPS contents are not inspected.
- Proxy events contain service references and outcomes, not request bodies,
  tokens, URLs or an inferred remote-user/tunnel identity from a loopback address.
- Stop publication closes its relay, not the shared service. Removing a profile is local only.
  Exit from the tray can stop the agent, client connections and hosts together.
- Startup refuses a tunnel that the service already reports as hosted elsewhere;
  stop the original host explicitly first. This check cannot prevent another
  executor from racing a host connection after the check.
- Published proxies reject their own listener and associated local-service ports
  on loopback and local-interface addresses to avoid recursive connections,
  including aliases. The same port on an unrelated allowed destination is valid.
- Requests display elapsed time, not a fabricated completion estimate. Cancelling
  the wait for a remote write cannot guarantee the server did not apply it.
  Refresh before repeating a cancelled or partially completed operation.
- A successful remote creation followed by failed refresh is reported separately;
  refresh the remote list instead of blindly creating another resource.
- Access-rule changes read and replace the resource's ACL. Writes are serialized
  within this agent, not across other administrators or devices. Without a
  service-side compare-and-swap guarantee, simultaneous external edits may be
  overwritten. Coordinate ACL administration and refresh before and after changes.
- Database schema 5 stores services and explicit bindings, never issued tokens. IPC version 7
  rejects incompatible agents/frontends. Preserve a private backup before upgrades;
  downgrade requires restoring the older backup, not rewriting schema markers.

## Management CLI examples

Examples omit the executable and optional `--data-dir ABSOLUTE_PATH` prefix:

```text
remote list ACCOUNT_ID
remote show ACCOUNT_ID example-tunnel.use2
remote create ACCOUNT_ID tunnel.json
remote update ACCOUNT_ID example-tunnel.use2 tunnel.json
remote port-create ACCOUNT_ID example-tunnel.use2 port.json
remote access-add ACCOUNT_ID example-tunnel.use2 access.json
remote token ACCOUNT_ID example-tunnel.use2 connect
remote delete ACCOUNT_ID example-tunnel.use2 --confirm example-tunnel.use2
host import host.json
service import service.json
service list
service start SERVICE_ID
host prepare example-tunnel.use2 --confirm example-tunnel.use2
host start example-tunnel.use2
host stop example-tunnel.use2
service stop SERVICE_ID
diagnostic echo-start
diagnostic echo-stop
diagnostic ping CLIENT_PROFILE_ID 9000
```

`remote token` hides the returned token by default. `--show-token` explicitly emits
it to stdout; avoid logs, shared terminals and support bundles. JSON inputs follow
the Core models and are size-bounded. The GUI supplies forms for these operations,
so manual JSON is optional. See CLI `--help` for port-scoped access and batch syntax.
Importing or saving an existing hosting profile stops its current publication;
restart it explicitly after reviewing the saved configuration.

### Existing local configurations

Legacy embedded proxy listeners migrate to independent services with stable,
deterministic IDs. Each listener preserves its address, port, proxy policy and
autostart intent; its publication receives an explicit service-ID binding.
Distinct legacy listeners are not silently merged. Rules retain their original
order; the first matching
enabled rule now owns the complete decision, including private-address flags.
Review overlapping legacy allow rules rather than relying on combined permissions.
Back up the private state directory before upgrading. App, Agent and CLI must be
updated together; downgrade
requires the pre-upgrade backup rather than changing the schema marker manually.
