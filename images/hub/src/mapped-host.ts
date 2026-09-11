import { SessionPortKey, TunnelRelayTunnelHost } from '@microsoft/dev-tunnels-connections';
import type { TunnelManagementClient } from '@microsoft/dev-tunnels-management';
import type { TunnelPort } from '@microsoft/dev-tunnels-contracts';
import type { PortForwardingService } from '@microsoft/dev-tunnels-ssh-tcp';
import { DEFAULT_PROXY_PORT } from './model.js';

export const PUBLISHED_PROXY_PORT = DEFAULT_PROXY_PORT;
export class MappedTunnelHost extends TunnelRelayTunnelHost {
  private disposed?: Promise<void>;
  private readonly mappings: ReadonlyMap<number, number>;
  constructor(management: TunnelManagementClient, readonly listenerPort: number, readonly proxyPort = DEFAULT_PROXY_PORT,
    socks?: { proxyPort: number; listenerPort: number }) {
    if (!Number.isInteger(proxyPort) || proxyPort < 1024 || proxyPort > 65535) throw new TypeError('Invalid proxy port');
    if (!Number.isInteger(listenerPort) || listenerPort < 1024 || listenerPort > 65535 || listenerPort === proxyPort) throw new TypeError('Invalid listener');
    if (socks && (!Number.isInteger(socks.proxyPort) || !Number.isInteger(socks.listenerPort) ||
      socks.proxyPort < 1024 || socks.proxyPort > 65535 || socks.listenerPort < 1024 || socks.listenerPort > 65535 ||
      new Set([proxyPort, listenerPort, socks.proxyPort, socks.listenerPort]).size !== 4)) throw new TypeError('Invalid SOCKS mapping');
    super(management, () => {}); // SDK traces may contain credentials or remote details.
    this.mappings = new Map([[proxyPort, listenerPort], ...(socks ? [[socks.proxyPort, socks.listenerPort] as [number, number]] : [])]);
  }
  override dispose(): Promise<void> { return this.disposed ??= super.dispose(); }
  override async refreshPorts(): Promise<void> {
    // A worker's advertised port is immutable; policy changes require a new worker.
    if (!await this.refreshTunnel(true)) return;
    if (this.tunnel?.ports?.length !== this.mappings.size ||
      new Set(this.tunnel.ports.map(p => p.portNumber)).size !== this.mappings.size ||
      this.tunnel.ports.some(p => !this.mappings.has(p.portNumber))) {
      await this.dispose(); throw new Error('TUNNEL_PORT_POLICY_CHANGED');
    }
    // Do not call SDK 1.3.56 refreshPorts(): its cleanup compares the local port to
    // the advertised port and iterates a Map with Object.entries. Existing mappings
    // stay intact; the standard authentication/reconnect hook forwards new sessions.
  }
  override async forwardPort(pfs: PortForwardingService, port: TunnelPort): Promise<void> {
    if (this.disposed) throw new Error('Host disposed');
    const listener = this.mappings.get(port.portNumber);
    if (listener === undefined) throw new Error('Host only publishes configured proxy ports');
    pfs.acceptRemoteConnectionsForNonForwardedPorts = false;
    const existing = pfs.localForwardedPorts.find(p => p.remotePort === port.portNumber);
    if (existing) {
      if (existing.localPort !== listener) throw new Error('A different listener is registered');
      return;
    }
    const forwarder = await pfs.forwardFromRemotePort('127.0.0.1', port.portNumber, '127.0.0.1', listener);
    if (!forwarder) throw new Error('Forwarding rejected');
    this.remoteForwarders.set(new SessionPortKey(pfs.session.sessionId, port.portNumber).toString(), forwarder);
  }
}
