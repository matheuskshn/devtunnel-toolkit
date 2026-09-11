import { randomBytes, createHash } from "node:crypto";
import type { IncomingMessage, ServerResponse } from "node:http";
import { check, HubError } from "../model.js";
import type { ControlStore, User } from "./control.js";

export const randomToken = () => randomBytes(32).toString("base64url");
const hash = (value: string) =>
  createHash("sha256").update(value).digest("hex");
export interface WebOptions {
  port: number;
  origin: string;
  key: string;
  secure: boolean;
  requirePasswordChange: boolean;
}
export function webOptions(env: NodeJS.ProcessEnv): WebOptions | undefined {
  check(
    env.HUB_WEB_ENABLED === undefined ||
      ["true", "false"].includes(env.HUB_WEB_ENABLED),
    "INVALID_WEB_ENABLED",
  );
  if (env.HUB_WEB_ENABLED !== "true") return;
  const requirePasswordChange = env.HUB_WEB_REQUIRE_PASSWORD_CHANGE ?? "true";
  check(
    ["true", "false"].includes(requirePasswordChange),
    "INVALID_WEB_REQUIRE_PASSWORD_CHANGE",
  );
  let url: URL;
  try {
    url = new URL(env.HUB_WEB_ORIGIN!);
  } catch {
    throw new HubError("WEB_ORIGIN_REQUIRED");
  }
  check(
    !url.username &&
      !url.password &&
      !url.search &&
      !url.hash &&
      url.pathname === "/" &&
      (url.protocol === "https:" ||
        (url.protocol === "http:" &&
          ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname))),
    "INVALID_WEB_ORIGIN",
  );
  const port = Number(env.HUB_WEB_PORT ?? 8082);
  check(
    Number.isInteger(port) && port >= 1024 && port <= 65535,
    "INVALID_WEB_PORT",
  );
  check(
    typeof env.HUB_WEB_KEY === "string" &&
      /^[A-Za-z0-9+/]{43}=$/.test(env.HUB_WEB_KEY),
    "WEB_KEY_REQUIRED",
  );
  return {
    port,
    origin: url.origin,
    key: env.HUB_WEB_KEY,
    secure: url.protocol === "https:",
    requirePasswordChange: requirePasswordChange === "true",
  };
}
export function cookie(req: IncomingMessage, name: string): string | undefined {
  const values = (req.headers.cookie ?? "")
    .split(";")
    .map((s) => s.trim())
    .filter((s) => s.startsWith(`${name}=`));
  return values.length === 1 ? values[0].slice(name.length + 1) : undefined;
}
export function setCookie(
  res: ServerResponse,
  name: string,
  value: string,
  secure: boolean,
  maxAge = 28800,
): void {
  const current = res.getHeader("Set-Cookie");
  const previous = Array.isArray(current)
    ? current
    : current
      ? [String(current)]
      : [];
  res.setHeader("Set-Cookie", [
    ...previous,
    `${name}=${value}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${maxAge}${secure ? "; Secure" : ""}`,
  ]);
}
export interface BrowserSession {
  user: string;
  epoch: number;
  csrf: string;
  created: number;
  used: number;
  provider?: string;
  providerEpoch?: number;
}
export class Sessions {
  private sessions = new Map<string, BrowserSession>();
  constructor(
    readonly control: ControlStore,
    readonly secure: boolean,
  ) {}
  get name() {
    return this.secure ? "__Host-hub" : "hub-local";
  }
  prune() {
    for (const [key, s] of this.sessions)
      if (Date.now() - s.used > 1800000 || Date.now() - s.created > 28800000)
        this.sessions.delete(key);
  }
  create(res: ServerResponse, user: User, provider?: string): void {
    this.prune();
    check(this.sessions.size < 1000, "SESSION_CAPACITY");
    const token = randomToken();
    this.sessions.set(hash(token), {
      user: user.id,
      epoch: user.epoch,
      csrf: randomToken(),
      created: Date.now(),
      used: Date.now(),
      provider,
      providerEpoch: provider
        ? this.control.data.providers.find((p) => p.id === provider)?.epoch
        : undefined,
    });
    setCookie(res, this.name, token, this.secure);
  }
  get(
    req: IncomingMessage,
    touch = true,
  ): { session: BrowserSession; user: User } {
    this.prune();
    const token = cookie(req, this.name);
    check(token && /^[A-Za-z0-9_-]{43}$/.test(token), "LOGIN_REQUIRED");
    const session = this.sessions.get(hash(token));
    check(session, "LOGIN_REQUIRED");
    const user = this.control.user(session.user);
    check(user.epoch === session.epoch, "LOGIN_REQUIRED");
    if (session.provider) {
      const p = this.control.data.providers.find(
        (p) => p.id === session.provider,
      );
      check(p?.enabled && p.epoch === session.providerEpoch, "LOGIN_REQUIRED");
    }
    if (touch) session.used = Date.now();
    return { session, user };
  }
  remove(req: IncomingMessage, res: ServerResponse) {
    const token = cookie(req, this.name);
    if (token) this.sessions.delete(hash(token));
    setCookie(res, this.name, "", this.secure, 0);
  }
  clear() {
    this.sessions.clear();
  }
}
export class RateLimit {
  private buckets = new Map<string, { count: number; until: number }>();
  take(key: string, limit = 8, window = 60000): void {
    for (const [k, v] of this.buckets)
      if (v.until < Date.now()) this.buckets.delete(k);
    const bucket = this.buckets.get(key) ?? {
      count: 0,
      until: Date.now() + window,
    };
    check(this.buckets.size < 2048 || this.buckets.has(key), "RATE_LIMITED");
    this.buckets.set(key, bucket);
    check(++bucket.count <= limit, "RATE_LIMITED");
  }
}
export function securityHeaders(res: ServerResponse, secure: boolean) {
  res.setHeader(
    "Content-Security-Policy",
    "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  );
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("Referrer-Policy", "no-referrer");
  res.setHeader("X-Frame-Options", "DENY");
  res.setHeader(
    "Permissions-Policy",
    "camera=(), microphone=(), geolocation=()",
  );
  res.setHeader("Cache-Control", "no-store");
  if (secure) res.setHeader("Strict-Transport-Security", "max-age=31536000");
}
export async function body(
  req: IncomingMessage,
): Promise<Record<string, unknown>> {
  check(
    req.headers["content-type"]?.split(";")[0] === "application/json",
    "JSON_REQUIRED",
  );
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    check(size <= 65536, "BODY_TOO_LARGE");
    chunks.push(chunk);
  }
  let value;
  try {
    value = JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    check(false, "INVALID_JSON");
  }
  check(
    value && typeof value === "object" && !Array.isArray(value),
    "INVALID_JSON",
  );
  return value;
}
export function fields(input: Record<string, unknown>, allowed: string[]) {
  check(
    Object.keys(input).every((k) => allowed.includes(k)),
    "UNKNOWN_FIELD",
  );
}
