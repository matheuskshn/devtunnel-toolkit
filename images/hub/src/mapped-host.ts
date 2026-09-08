import { SessionPortKey, TunnelRelayTunnelHost } from '@microsoft/dev-tunnels-connections';
import type { TunnelManagementClient } from '@microsoft/dev-tunnels-management';
import type { TunnelPort } from '@microsoft/dev-tunnels-contracts';
import type { PortForwardingService } from '@microsoft/dev-tunnels-ssh-tcp';

export const PUBLISHED_PROXY_PORT = 3140;
export class MappedTunnelHost extends TunnelRelayTunnelHost {
  private disposed?: Promise<void>;
  constructor(management: TunnelManagementClient, readonly listenerPort: number) {
    if (!Number.isInteger(listenerPort) || listenerPort < 1024 || listenerPort > 65535 || listenerPort === 3140) throw new TypeError('Invalid listener');
    super(management, () => {}); // SDK traces may contain credentials or remote details.
  }
  override dispose(): Promise<void> { return this.disposed ??= super.dispose(); }
  override async refreshPorts(): Promise<void> {
    // v1 ports are immutable. Refresh metadata/ACL through the manager; never add ports.
    if (!await this.refreshTunnel(true)) return;
    if (this.tunnel?.ports?.length !== 1 || this.tunnel.ports[0].portNumber !== 3140) {
      await this.dispose(); throw new Error('TUNNEL_PORT_POLICY_CHANGED');
    }
    // Do not call SDK 1.3.56 refreshPorts(): its cleanup compares the local port to
    // the advertised port and iterates a Map with Object.entries. Existing mappings
    // stay intact; the standard authentication/reconnect hook forwards new sessions.
  }
  override async forwardPort(pfs: PortForwardingService, port: TunnelPort): Promise<void> {
    if (this.disposed) throw new Error('Host disposed');
    if (port.portNumber !== 3140) throw new Error('Host only publishes 3140');
    pfs.acceptRemoteConnectionsForNonForwardedPorts = false;
    const existing = pfs.localForwardedPorts.find(p => p.remotePort === 3140);
    if (existing) {
      if (existing.localPort !== this.listenerPort) throw new Error('A different listener is registered');
      return;
    }
    const forwarder = await pfs.forwardFromRemotePort('127.0.0.1', 3140, '127.0.0.1', this.listenerPort);
    if (!forwarder) throw new Error('Forwarding rejected');
    this.remoteForwarders.set(new SessionPortKey(pfs.session.sessionId, 3140).toString(), forwarder);
  }
}
