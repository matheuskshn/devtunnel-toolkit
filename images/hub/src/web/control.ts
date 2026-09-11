import {
  randomBytes,
  randomUUID,
  scrypt as derive,
  timingSafeEqual,
} from "node:crypto";
import { check, HubError, type Config } from "../model.js";
import { credentialKeys, seal, unseal } from "../credentials.js";
import type { StateStore } from "../state.js";

export type Role = "admin" | "operator" | "viewer";
export interface User {
  id: string;
  username: string;
  name: string;
  role: Role;
  disabled: boolean;
  local: boolean;
  password?: string;
  // Persist the pending change independently of whether the deployment enforces it.
  mustChange: boolean;
  epoch: number;
  sessions: string[];
  identities: { provider: string; subject: string }[];
  createdAt: string;
}
export interface AuthProvider {
  id: string;
  kind: "microsoft" | "github" | "oidc" | "ldap";
  label: string;
  enabled: boolean;
  registration: boolean;
  epoch: number;
  issuer?: string;
  clientId?: string;
  clientSecret?: string;
  url?: string;
  baseDN?: string;
  bindDN?: string;
  bindPassword?: string;
  loginAttribute?: string;
  idAttribute?: string;
  ca?: string;
}
export interface ControlData {
  version: 1;
  revision: number;
  users: User[];
  providers: AuthProvider[];
  policy?: Partial<Config>;
}
export const roles: Role[] = ["admin", "operator", "viewer"];
export const usernameValid = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][a-z0-9._-]{2,63}$/.test(v);
export function passwordValid(value: unknown): asserts value is string {
  check(
    typeof value === "string" &&
      value.length >= 14 &&
      value.length <= 1024 &&
      value.trim().length >= 14,
    "PASSWORD_TOO_WEAK",
  );
}
async function scrypt(password: string, salt: Buffer): Promise<Buffer> {
  return new Promise((resolve, reject) =>
    derive(
      password,
      salt,
      64,
      { N: 32768, r: 8, p: 1, maxmem: 64 * 1024 * 1024 },
      (e, key) => (e ? reject(e) : resolve(key)),
    ),
  );
}
export async function hashPassword(password: string): Promise<string> {
  passwordValid(password);
  const salt = randomBytes(16);
  return `scrypt1:${salt.toString("base64")}:${(await scrypt(password, salt)).toString("base64")}`;
}
export async function verifyPassword(
  password: unknown,
  encoded?: string,
): Promise<boolean> {
  if (typeof password !== "string" || password.length > 1024) {
    return false;
  }
  const parts = (encoded ?? "").split(":");
  const valid =
    parts.length === 3 &&
    parts[0] === "scrypt1" &&
    Buffer.from(parts[1], "base64").length === 16 &&
    Buffer.from(parts[2], "base64").length === 64;
  // Unknown and disabled accounts still pay the same password hashing cost.
  const actual = await scrypt(
    password,
    valid ? Buffer.from(parts[1], "base64") : Buffer.alloc(16),
  );
  return (
    timingSafeEqual(
      actual,
      valid ? Buffer.from(parts[2], "base64") : Buffer.alloc(64),
    ) && valid
  );
}
export function publicUser(user: User) {
  const { password, ...safe } = user;
  return { ...safe, passwordConfigured: !!password };
}
export function publicProvider(provider: AuthProvider) {
  const { clientSecret, bindPassword, ...safe } = provider;
  return {
    ...safe,
    clientSecretConfigured: !!clientSecret,
    bindPasswordConfigured: !!bindPassword,
  };
}
export function visible(user: User, sessionId: string): boolean {
  return user.role === "admin" || user.sessions.includes(sessionId);
}

function providerUrl(value: unknown, protocol: string): URL {
  check(
    typeof value === "string" && value.length <= 2048,
    "INVALID_PROVIDER_URL",
  );
  let u: URL;
  try {
    u = new URL(value);
  } catch {
    throw new HubError("INVALID_PROVIDER_URL");
  }
  check(
    u.protocol === protocol &&
      !u.username &&
      !u.password &&
      !u.search &&
      !u.hash,
    "INVALID_PROVIDER_URL",
  );
  return u;
}
function validateLdapProvider(p: AuthProvider): void {
  const u = providerUrl(p.url, "ldaps:");
  check(!u.pathname || u.pathname === "/", "INVALID_PROVIDER_URL");
  check(
    p.baseDN && p.bindDN && p.bindPassword && p.bindPassword.length <= 4096,
    "LDAP_CONFIG_REQUIRED",
  );
  for (const attr of [p.loginAttribute, p.idAttribute]) {
    check(
      typeof attr === "string" && /^[a-zA-Z][a-zA-Z0-9-]{0,63}$/.test(attr),
      "INVALID_LDAP_ATTRIBUTE",
    );
  }
  check(
    !p.ca ||
      (typeof p.ca === "string" &&
        p.ca.length <= 32768 &&
        p.ca.includes("-----BEGIN CERTIFICATE-----") &&
        !p.ca.includes("PRIVATE KEY")),
    "INVALID_LDAP_CA",
  );
}
function validateOauthProvider(p: AuthProvider): void {
  check(
    p.clientId &&
      p.clientId.length <= 512 &&
      p.clientSecret &&
      p.clientSecret.length <= 4096,
    "OAUTH_CONFIG_REQUIRED",
  );
  if (p.kind !== "github") {
    const u = providerUrl(p.issuer, "https:");
    if (p.kind === "microsoft") {
      check(
        u.hostname === "login.microsoftonline.com" &&
          /^\/[0-9a-f-]{36}\/v2\.0\/?$/i.test(u.pathname),
        "MICROSOFT_TENANT_ISSUER_REQUIRED",
      );
    }
  }
}
export function validateProvider(p: AuthProvider): void {
  check(
    usernameValid(p.id) &&
      ["microsoft", "github", "oidc", "ldap"].includes(p.kind),
    "INVALID_AUTH_PROVIDER",
  );
  check(
    typeof p.label === "string" &&
      p.label.length > 0 &&
      p.label.length <= 80 &&
      typeof p.enabled === "boolean" &&
      typeof p.registration === "boolean",
    "INVALID_AUTH_PROVIDER",
  );
  for (const k of [
    "issuer",
    "clientId",
    "clientSecret",
    "url",
    "baseDN",
    "bindDN",
    "bindPassword",
    "loginAttribute",
    "idAttribute",
    "ca",
  ] as const) {
    check(
      p[k] === undefined || (typeof p[k] === "string" && p[k]!.length <= 32768),
      "INVALID_AUTH_PROVIDER",
    );
  }
  if (p.kind === "ldap") {
    validateLdapProvider(p);
  } else {
    validateOauthProvider(p);
  }
}
export class ControlStore {
  data!: ControlData;
  private queue: Promise<unknown> = Promise.resolve();
  private keys;
  private failed = false;
  constructor(
    readonly store: StateStore,
    key: string,
    readonly onFailure: () => void = () => {},
  ) {
    this.keys = credentialKeys({ HUB_CREDENTIAL_KEY: key });
  }
  async load(): Promise<void> {
    if (this.store.state.console) {
      const plain = unseal(
        Buffer.from(this.store.state.console, "base64"),
        `${this.store.config.hubId}:console:v1`,
        this.keys,
      );
      try {
        this.data = JSON.parse(plain.toString());
      } finally {
        plain.fill(0);
      }
      check(
        this.data.version === 1 &&
          Array.isArray(this.data.users) &&
          Array.isArray(this.data.providers),
        "INVALID_CONSOLE_STATE",
      );
      check(
        this.data.users.some(
          (u) =>
            u.id === "admin" && u.local && u.role === "admin" && !u.disabled,
        ),
        "RECOVERY_ADMIN_MISSING",
      );
    } else {
      this.data = {
        version: 1,
        revision: 0,
        providers: [],
        users: [
          {
            id: "admin",
            username: "admin",
            name: "Administrador local",
            role: "admin",
            disabled: false,
            local: true,
            mustChange: true,
            epoch: 0,
            sessions: [],
            identities: [],
            createdAt: new Date().toISOString(),
          },
        ],
      };
      await this.update(() => {});
    }
  }
  update(
    edit: (draft: ControlData) => void | Promise<void>,
    expected?: number,
  ): Promise<void> {
    const task = this.queue.then(async () => {
      check(!this.failed, "CONSOLE_STORAGE_FAILED");
      check(
        expected === undefined || expected === this.data.revision,
        "CONFIG_CONFLICT",
      );
      const draft = structuredClone(this.data);
      await edit(draft);
      draft.revision++;
      check(
        draft.users.length <= 1000 && draft.providers.length <= 20,
        "CONSOLE_CAPACITY",
      );
      check(
        draft.users.some(
          (u) =>
            u.id === "admin" && u.local && u.role === "admin" && !u.disabled,
        ),
        "RECOVERY_ADMIN_PROTECTED",
      );
      const bytes = Buffer.from(JSON.stringify(draft));
      try {
        this.store.state.console = seal(
          bytes,
          `${this.store.config.hubId}:console:v1`,
          this.keys,
        ).toString("base64");
      } finally {
        bytes.fill(0);
      }
      try {
        await this.store.save();
        this.data = draft;
      } catch (e) {
        this.failed = true;
        this.onFailure();
        throw e;
      }
    });
    this.queue = task.catch(() => {});
    return task;
  }
  user(id: string): User {
    check(!this.failed, "CONSOLE_STORAGE_FAILED");
    const u = this.data.users.find((u) => u.id === id);
    check(u && !u.disabled, "LOGIN_REQUIRED");
    return u;
  }
  async resetPassword(username: string, password: string): Promise<void> {
    const hash = await hashPassword(password);
    await this.update((d) => {
      const u = d.users.find((u) => u.username === username && u.local);
      check(u, "LOCAL_USER_NOT_FOUND");
      u.password = hash;
      u.mustChange = true;
      u.epoch++;
      if (u.id === "admin") {
        u.disabled = false;
        u.role = "admin";
      }
    });
  }
  async external(
    provider: AuthProvider,
    subject: string,
    name: string,
  ): Promise<User> {
    check(
      typeof subject === "string" &&
        subject.length > 0 &&
        subject.length <= 1024,
      "INVALID_EXTERNAL_SUBJECT",
    );
    const existing = this.data.users.find((u) =>
      u.identities.some(
        (i) => i.provider === provider.id && i.subject === subject,
      ),
    );
    if (existing) {
      return this.user(existing.id);
    }
    check(provider.registration, "ACCOUNT_NOT_ENROLLED");
    await this.update((d) => {
      check(
        d.providers.some(
          (p) =>
            p.id === provider.id && p.enabled && p.epoch === provider.epoch,
        ),
        "LOGIN_FAILED",
      );
      if (
        d.users.some((u) =>
          u.identities.some(
            (i) => i.provider === provider.id && i.subject === subject,
          ),
        )
      ) {
        return;
      }
      const id = randomUUID();
      d.users.push({
        id,
        username: `user-${id.slice(0, 12)}`,
        name: name.slice(0, 120),
        role: "viewer",
        disabled: true,
        local: false,
        mustChange: false,
        epoch: 0,
        sessions: [],
        identities: [{ provider: provider.id, subject }],
        createdAt: new Date().toISOString(),
      });
    });
    check(false, "ACCOUNT_PENDING_APPROVAL");
  }
}
