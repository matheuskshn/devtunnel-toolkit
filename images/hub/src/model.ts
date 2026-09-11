export type Provider = 'microsoft' | 'github';
export const DEFAULT_PROXY_PORT = 3140;
export const DEFAULT_SOCKS_PORT = 3180;
export type Status = 'login_required' | 'ready' | 'starting' | 'running' | 'stopped' | 'reauth_required' | 'error' | 'removed';
export interface Identity { provider: Provider; user_id: string; user_login: string; tenant_id?: string }
export interface Session {
  id: string; provider: Provider; tunnel_name?: string; tunnel_id?: string;
  tunnel_name_template?: string;
  proxy_port?: number;
  socks_port?: number; socks_listener?: number;
  listener: number; identity?: Identity; desired: boolean; status: Status;
  error?: string; created_at: string;
}
export interface State { version: 1; hub_id: string; next_listener: number; sessions: Session[]; console?: string }
export interface Config {
  hubId: string; tunnelNameTemplate: string; listenerStart: number; listenerEnd: number; maxSessions: number;
  allowedDomains: string[]; allowAllDomains: boolean; allowedPorts: number[]; connectPorts: number[];
  healthPort: number; proxyPort: number; socksPort: number; socksEnabled: boolean; maintenanceSeconds: number;
  allowedProviders: Provider[]; allowedMicrosoftTenants: string[];
}
export class HubError extends Error {
  constructor(readonly code: string) { super(code); }
}
export function check(condition: unknown, code: string): asserts condition {
  if (!condition) throw new HubError(code);
}
export const validId = (s: unknown): s is string => typeof s === 'string' && /^[a-z][a-z0-9-]{1,30}[a-z0-9]$/.test(s);
export const validTunnelName = (s: unknown): s is string => typeof s === 'string' && /^[a-z][a-z0-9-]{1,47}[a-z0-9]$/.test(s);
export const validCanonicalId = (s: unknown): s is string => typeof s === 'string' && /^[a-z0-9][a-z0-9-]{1,48}\.[a-z0-9]{3,12}$/.test(s);
export function validNameTemplate(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 200 && value.includes('{username}') &&
    validTunnelName(value.replaceAll('{hub_id}', 'hub').replaceAll('{username}', 'user'));
}
export function resolveTunnelName(template: string, hubId: string, identity: Identity): string {
  check(validNameTemplate(template), 'INVALID_TUNNEL_NAME_TEMPLATE');
  const login = identity.provider === 'microsoft' ? identity.user_login.split('@')[0] : identity.user_login;
  const username = login.toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
  check(username.length > 0, 'INVALID_TUNNEL_USERNAME');
  const name = template.replaceAll('{hub_id}', hubId).replaceAll('{username}', username);
  check(validTunnelName(name), 'INVALID_TUNNEL_NAME');
  return name;
}
export function errorCode(e: unknown): string { return e instanceof HubError ? e.code : 'INTERNAL_ERROR'; }
export function parseConfig(value: unknown): Config {
  check(value && typeof value === 'object' && !Array.isArray(value), 'INVALID_CONFIG');
  const input = value as Record<string, unknown>;
  const defaults: Config = { hubId: 'devhub', tunnelNameTemplate: '{hub_id}-{username}', listenerStart: 18001, listenerEnd: 18999,
    maxSessions: 50, allowedDomains: [], allowAllDomains: false, allowedPorts: [80, 443], connectPorts: [443],
    healthPort: 8080, proxyPort: DEFAULT_PROXY_PORT, socksPort: DEFAULT_SOCKS_PORT, socksEnabled: false, maintenanceSeconds: 300,
    allowedProviders: ['microsoft','github'], allowedMicrosoftTenants: [] };
  check(Object.keys(input).every(k => k in defaults), 'UNKNOWN_CONFIG_KEY');
  const c = { ...defaults, ...input } as Config;
  check(validId(c.hubId), 'INVALID_HUB_ID');
  check(validNameTemplate(c.tunnelNameTemplate), 'INVALID_TUNNEL_NAME_TEMPLATE');
  const port = (n: unknown): n is number => Number.isInteger(n) && Number(n) > 0 && Number(n) <= 65535;
  check(port(c.listenerStart) && c.listenerStart >= 1024 && port(c.listenerEnd) && c.listenerEnd >= c.listenerStart, 'INVALID_LISTENER_RANGE');
  check(port(c.proxyPort) && c.proxyPort >= 1024, 'INVALID_PROXY_PORT');
  check(typeof c.socksEnabled === 'boolean', 'INVALID_SOCKS_ENABLED');
  check(port(c.socksPort) && c.socksPort >= 1024 && c.socksPort !== c.proxyPort, 'INVALID_SOCKS_PORT');
  check(!(c.listenerStart <= c.socksPort && c.listenerEnd >= c.socksPort), 'RESERVED_PORT');
  check(!(c.listenerStart <= c.proxyPort && c.listenerEnd >= c.proxyPort), 'RESERVED_PORT');
  check(port(c.healthPort) && c.healthPort >= 1024 && c.healthPort !== c.proxyPort && c.healthPort !== c.socksPort && !(c.healthPort >= c.listenerStart && c.healthPort <= c.listenerEnd), 'INVALID_HEALTH_PORT');
  check(Number.isInteger(c.maxSessions) && c.maxSessions >= 1 && c.maxSessions <= 500, 'INVALID_SESSION_LIMIT');
  check(Number.isInteger(c.maintenanceSeconds) && c.maintenanceSeconds >= 60 && c.maintenanceSeconds <= 3600, 'INVALID_MAINTENANCE_INTERVAL');
  check(Array.isArray(c.allowedProviders) && c.allowedProviders.length > 0 && c.allowedProviders.every(p => ['microsoft','github'].includes(p)), 'INVALID_PROVIDERS');
  check(Array.isArray(c.allowedMicrosoftTenants) && c.allowedMicrosoftTenants.every(t => typeof t === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(t)), 'INVALID_TENANT_ALLOWLIST');
  check(Array.isArray(c.allowedDomains) && c.allowedDomains.length <= 1000 && c.allowedDomains.every(d =>
    typeof d === 'string' && d.length <= 253 && /^\.?[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/.test(d) && !d.includes('..') && !/^\.?[0-9.]+$/.test(d)), 'INVALID_DOMAIN_ALLOWLIST');
  check(typeof c.allowAllDomains === 'boolean', 'INVALID_ALLOW_ALL_DOMAINS');
  check(Array.isArray(c.allowedPorts) && c.allowedPorts.length > 0 && c.allowedPorts.every(port), 'INVALID_ALLOWED_PORTS');
  check(Array.isArray(c.connectPorts) && c.connectPorts.length > 0 && c.connectPorts.every(p => c.allowedPorts.includes(p)), 'INVALID_CONNECT_PORTS');
  return c;
}
export function bindIdentity(session: Session, identity: Identity): void {
  check(identity.provider === session.provider, 'IDENTITY_PROVIDER_MISMATCH');
  check(!session.identity || session.identity.user_id === identity.user_id, 'IDENTITY_CHANGED');
  session.identity = identity;
}
