export type Provider = 'microsoft' | 'github';
export type Status = 'login_required' | 'ready' | 'starting' | 'running' | 'stopped' | 'reauth_required' | 'error' | 'removed';
export interface Identity { provider: Provider; user_id: string; user_login: string; tenant_id?: string }
export interface Session {
  id: string; provider: Provider; tunnel_name: string; tunnel_id?: string;
  listener: number; identity?: Identity; desired: boolean; status: Status;
  error?: string; created_at: string;
}
export interface State { version: 1; hub_id: string; next_listener: number; sessions: Session[] }
export interface Config {
  hubId: string; listenerStart: number; listenerEnd: number; maxSessions: number;
  allowedDomains: string[]; allowAllDomains: boolean; allowedPorts: number[]; connectPorts: number[];
  healthPort: number; maintenanceSeconds: number;
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
export function errorCode(e: unknown): string { return e instanceof HubError ? e.code : 'INTERNAL_ERROR'; }
export function parseConfig(value: unknown): Config {
  check(value && typeof value === 'object' && !Array.isArray(value), 'INVALID_CONFIG');
  const input = value as Record<string, unknown>;
  const defaults: Config = { hubId: 'devhub', listenerStart: 18001, listenerEnd: 18999,
    maxSessions: 50, allowedDomains: [], allowAllDomains: false, allowedPorts: [80, 443], connectPorts: [443],
    healthPort: 8080, maintenanceSeconds: 300,
    allowedProviders: ['microsoft','github'], allowedMicrosoftTenants: [] };
  check(Object.keys(input).every(k => k in defaults), 'UNKNOWN_CONFIG_KEY');
  const c = { ...defaults, ...input } as Config;
  check(validId(c.hubId), 'INVALID_HUB_ID');
  const port = (n: unknown): n is number => Number.isInteger(n) && Number(n) > 0 && Number(n) <= 65535;
  check(port(c.listenerStart) && c.listenerStart >= 1024 && port(c.listenerEnd) && c.listenerEnd >= c.listenerStart, 'INVALID_LISTENER_RANGE');
  check(!(c.listenerStart <= 3140 && c.listenerEnd >= 3140), 'RESERVED_PORT');
  check(port(c.healthPort) && c.healthPort >= 1024 && c.healthPort !== 3140 && !(c.healthPort >= c.listenerStart && c.healthPort <= c.listenerEnd), 'INVALID_HEALTH_PORT');
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
