import * as oidc from "openid-client";
import { Client, Filter } from "ldapts";
import { randomBytes } from "node:crypto";
import { check } from "../model.js";
import type { AuthProvider } from "./control.js";

export interface LoginFlow {
  provider: string;
  epoch: number;
  state: string;
  verifier: string;
  nonce: string;
  expires: number;
  config?: oidc.Configuration;
}
const random = () => randomBytes(32).toString("base64url");
export async function beginLogin(
  p: AuthProvider,
  callback: string,
): Promise<{ url: string; flow: LoginFlow }> {
  const flow: LoginFlow = {
    provider: p.id,
    epoch: p.epoch,
    state: random(),
    verifier: oidc.randomPKCECodeVerifier(),
    nonce: random(),
    expires: Date.now() + 600000,
  };
  const params = {
    client_id: p.clientId!,
    redirect_uri: callback,
    response_type: "code",
    state: flow.state,
    code_challenge: await oidc.calculatePKCECodeChallenge(flow.verifier),
    code_challenge_method: "S256",
  };
  if (p.kind === "github") {
    const url = new URL("https://github.com/login/oauth/authorize");
    url.search = new URLSearchParams({
      ...params,
      scope: "read:user",
      allow_signup: "false",
    }).toString();
    return { url: url.href, flow };
  }
  check(p.kind === "oidc" || p.kind === "microsoft", "INVALID_AUTH_PROVIDER");
  flow.config = await oidc.discovery(
    new URL(p.issuer!),
    p.clientId!,
    p.clientSecret,
    undefined,
    { timeout: 10 },
  );
  oidc.enableNonRepudiationChecks(flow.config);
  const url = oidc.buildAuthorizationUrl(flow.config, {
    ...params,
    scope: "openid profile email",
    nonce: flow.nonce,
  });
  return { url: url.href, flow };
}
export async function finishLogin(
  p: AuthProvider,
  flow: LoginFlow,
  current: URL,
): Promise<{ subject: string; name: string }> {
  check(
    flow.expires > Date.now() &&
      p.enabled &&
      flow.provider === p.id &&
      flow.epoch === p.epoch,
    "LOGIN_EXPIRED",
  );
  check(
    current.searchParams.get("state") === flow.state &&
      !current.searchParams.has("error"),
    "LOGIN_FAILED",
  );
  if (p.kind === "github") {
    const code = current.searchParams.get("code");
    check(code && code.length <= 2048, "LOGIN_FAILED");
    const callback = new URL(current.pathname, current.origin).href;
    const response = await fetch(
      "https://github.com/login/oauth/access_token",
      {
        method: "POST",
        redirect: "error",
        signal: AbortSignal.timeout(10000),
        headers: {
          Accept: "application/json",
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: new URLSearchParams({
          client_id: p.clientId!,
          client_secret: p.clientSecret!,
          code,
          redirect_uri: callback,
          code_verifier: flow.verifier,
        }),
      },
    );
    check(response.ok, "LOGIN_FAILED");
    const token = (await response.json()) as { access_token?: string };
    check(token.access_token, "LOGIN_FAILED");
    const identity = await fetch("https://api.github.com/user", {
      redirect: "error",
      signal: AbortSignal.timeout(10000),
      headers: {
        Authorization: `Bearer ${token.access_token}`,
        Accept: "application/vnd.github+json",
        "User-Agent": "devtunnel-toolkit-hub",
        "X-GitHub-Api-Version": "2022-11-28",
      },
    });
    check(identity.ok, "LOGIN_FAILED");
    const user = (await identity.json()) as { id?: number; login?: string };
    check(Number.isSafeInteger(user.id) && user.login, "LOGIN_FAILED");
    return { subject: String(user.id), name: user.login };
  }
  check(flow.config, "LOGIN_FAILED");
  const tokens = await oidc.authorizationCodeGrant(flow.config, current, {
    pkceCodeVerifier: flow.verifier,
    expectedState: flow.state,
    expectedNonce: flow.nonce,
    idTokenExpected: true,
  });
  const claims = tokens.claims();
  check(claims?.sub, "LOGIN_FAILED");
  // No linking by email/login: the verified subject under this immutable issuer owns the binding.
  return {
    subject: claims.sub,
    name: String(claims.name ?? claims.preferred_username ?? claims.sub).slice(
      0,
      120,
    ),
  };
}
export async function ldapLogin(
  p: AuthProvider,
  username: string,
  password: string,
  factory: (options: ConstructorParameters<typeof Client>[0]) => Client = (
    options,
  ) => new Client(options),
): Promise<{ subject: string; name: string }> {
  check(
    p.kind === "ldap" &&
      p.enabled &&
      password.length > 0 &&
      password.length <= 1024 &&
      username.length > 0 &&
      username.length <= 256,
    "LOGIN_FAILED",
  );
  check(new URL(p.url!).protocol === "ldaps:", "LDAP_TLS_REQUIRED");
  const options = {
    url: p.url!,
    timeout: 8000,
    connectTimeout: 5000,
    tlsOptions: {
      rejectUnauthorized: true,
      minVersion: "TLSv1.2" as const,
      ...(p.ca ? { ca: p.ca } : {}),
    },
  };
  const lookup = factory(options);
  let user: Client | undefined;
  try {
    await lookup.bind(p.bindDN!, p.bindPassword!);
    const result = await lookup.search(p.baseDN!, {
      scope: "sub",
      sizeLimit: 2,
      timeLimit: 5,
      filter: `(${p.loginAttribute}=${Filter.escape(username)})`,
      attributes: [p.loginAttribute!, p.idAttribute!],
      ...(p.idAttribute?.toLowerCase() === "objectguid"
        ? { explicitBufferAttributes: [p.idAttribute] }
        : {}),
    });
    check(
      result.searchEntries.length === 1 && result.searchReferences.length === 0,
      "LOGIN_FAILED",
    );
    const entry = result.searchEntries[0],
      value = entry[p.idAttribute!];
    check(
      (typeof value === "string" && value.length > 0) || Buffer.isBuffer(value),
      "LDAP_STABLE_ID_REQUIRED",
    );
    user = factory(options);
    await user.bind(entry.dn, password);
    return {
      subject: Buffer.isBuffer(value)
        ? value.toString("base64")
        : (value as string),
      name: username,
    };
  } finally {
    await lookup.unbind().catch(() => {});
    await user?.unbind().catch(() => {});
  }
}
