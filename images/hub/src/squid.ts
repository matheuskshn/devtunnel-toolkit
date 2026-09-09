import type { Config, Session } from './model.js';

/** Only numeric, immutable listener IDs enter Squid's log. Enrichment happens in Node. */
export function squidConfig(config: Config, sessions: Session[], runDir: string): string {
  if (!/^\/[a-zA-Z0-9/_-]+$/.test(runDir)) throw new Error('Invalid runtime path');
  const active = sessions.filter(s => s.identity && s.status !== 'removed');
  const lines = [
    'visible_hostname devtunnel-toolkit-hub', 'workers 1',
    `pid_filename ${runDir}/squid.pid`, 'cache_effective_user hub', 'cache_effective_group hub',
    'cache deny all', 'cache_mem 16 MB', 'maximum_object_size 0 KB',
    'cache_store_log none', 'cache_log /dev/null', 'logfile_rotate 0', 'log_mime_hdrs off',
    'strip_query_terms on', 'forwarded_for delete', 'via off', 'shutdown_lifetime 2 seconds',
    'pinger_enable off', 'netdb_filename none', 'coredump_dir /tmp',
    // Domain and port only, NEVER full URI, path, headers or credentials.
    'logformat hub %ts.%03tu %>lp %#rm %#>rd %>rP %>Hs %<st %tr',
    `access_log stdio:${runDir}/audit.fifo hub`,
    'acl local_client src 127.0.0.1/32',
    `acl allowed_ports port ${config.allowedPorts.join(' ')}`,
    `acl connect_ports port ${config.connectPorts.join(' ')}`,
    'acl CONNECT method CONNECT', 'acl manager proto cache_object',
    'http_access deny manager', 'http_access deny !local_client',
    'http_access deny !allowed_ports', 'http_access deny CONNECT !connect_ports',
    // Block instance metadata/link-local addresses even if a permitted name resolves there.
    'acl metadata dst 169.254.0.0/16 fe80::/10', 'http_access deny metadata',
    'acl loopback_destination dst 0.0.0.0/8 127.0.0.0/8 ::/128 ::1/128', 'http_access deny loopback_destination',
  ];
  if (active.length) {
    for (const s of active) lines.push(`http_port 127.0.0.1:${s.listener} name=s${s.listener}`);
    lines.push(`acl session_listener myportname ${active.map(s => `s${s.listener}`).join(' ')}`);
    if (config.allowAllDomains) {
      // Explicit operator opt-in; previous port/source/metadata denies still apply.
      lines.push('http_access allow session_listener');
    } else if (config.allowedDomains.length) {
      lines.push(`acl allowed_destinations dstdomain ${config.allowedDomains.join(' ')}`);
      lines.push('http_access allow session_listener allowed_destinations');
    }
  } else {
    // Keep Squid healthy before enrollment; this deny-only listener is never published.
    lines.push('http_port 127.0.0.1:3140 name=unassigned');
  }
  lines.push('http_access deny all');
  return lines.join('\n') + '\n';
}
export function auditRecord(line: string, sessions: Session[]): Record<string, unknown> | undefined {
  const fields = line.trim().split(' ');
  if (fields.length !== 8 || !/^\d+\.\d+$/.test(fields[0])) return;
  const [time, listener, method, destination, port, status, bytes, duration] = fields;
  const session = sessions.find(s => s.listener === Number(listener));
  if (!session?.identity) return;
  const decode = (s: string) => { try { return decodeURIComponent(s).slice(0, 253); } catch { return 'invalid'; } };
  return { event: 'proxy_access', time: new Date(Number(time) * 1000).toISOString(),
    session_id: session.id, ...session.identity, tunnel_id: session.tunnel_id ?? null,
    attribution: 'session_listener', listener: Number(listener), destination: decode(destination),
    destination_port: Number(port) || null, method: decode(method), status: Number(status),
    bytes: Number(bytes), duration_ms: Number(duration) };
}
