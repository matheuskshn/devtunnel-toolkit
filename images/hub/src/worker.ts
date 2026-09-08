import { TunnelManagementHttpClient, ManagementApiVersions } from '@microsoft/dev-tunnels-management';
import type { Tunnel } from '@microsoft/dev-tunnels-contracts';
import { MappedTunnelHost } from './mapped-host.js';

// Credentials travel only over Node's private IPC channel, never argv or logs.
let host: MappedTunnelHost | undefined;
let stopping = false;
let sequence = 0;
const requests = new Map<number, { resolve: (t: Tunnel) => void; reject: (e: Error) => void; timer: NodeJS.Timeout }>();
function credentials(): Promise<Tunnel> {
  const id = ++sequence;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { requests.delete(id); reject(new Error('CREDENTIAL_TIMEOUT')); }, 150000);
    requests.set(id, { resolve, reject, timer });
    process.send?.({ type: 'credentials', id });
  });
}
async function stop(code: number): Promise<void> {
  if (stopping) return; stopping = true;
  const deadline = setTimeout(() => process.exit(code), 4000);
  for (const entry of requests.values()) { clearTimeout(entry.timer); entry.reject(new Error('STOPPING')); }
  requests.clear();
  try { await host?.dispose(); } catch { /* no raw SDK errors */ }
  clearTimeout(deadline); process.exit(code);
}
process.on('SIGTERM', () => void stop(0));
process.on('disconnect', () => void stop(0));
process.on('uncaughtException', () => void stop(1));
process.on('unhandledRejection', () => void stop(1));
process.on('message', async (message: any) => {
  if (message.type === 'credentials') {
    const entry = requests.get(message.id); if (!entry) return;
    requests.delete(message.id); clearTimeout(entry.timer);
    if (message.error) { entry.reject(new Error('CREDENTIAL_FAILED')); await stop(message.error === 'AUTH_REQUIRED' ? 75 : 1); }
    else entry.resolve(message.tunnel);
    return;
  }
  if (message.type !== 'start' || host) return;
  try {
    const management = new TunnelManagementHttpClient({ name: 'devtunnel-toolkit-hub', version: '0.1.0' }, ManagementApiVersions.Version20230927preview);
    management.enableEventsReporting = false; management.trace = () => {};
    host = new MappedTunnelHost(management, message.listener);
    host.refreshingTunnelAccessToken(e => { e.tunnelAccessToken = credentials().then(t => t.accessTokens!.host); });
    host.refreshingTunnel(e => { e.tunnelPromise = credentials(); });
    host.connectionStatusChanged(e => process.send?.({ type: 'status', status: e.status }));
    await host.connect(await credentials());
    process.send?.({ type: 'ready' });
    let refreshing = false;
    setInterval(() => {
      if (refreshing || stopping) return; refreshing = true;
      host!.refreshPorts().catch(() => stop(1)).finally(() => { refreshing = false; });
    }, message.maintenanceSeconds * 1000);
  } catch { await stop(1); }
});
