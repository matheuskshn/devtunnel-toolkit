import test from "node:test";
import assert from "node:assert/strict";
import { generateKeyPair, exportJWK, SignJWT } from "jose";
import { beginLogin, finishLogin } from "../dist/web/providers.js";
import { selectImages } from "../../../.github/scripts/image-changes.mjs";

test("static console assets select only the Hub image", () => {
  const result = selectImages(["images/hub/web/app.css"]);
  assert.equal(result.hub, true);
  assert.equal(result.legacy, false);
  assert.equal(selectImages(["images/hub/WEB.md"]).hub, false);
});
test("GitHub exchanges PKCE server-side and returns only stable identity", async (t) => {
  const original = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = original;
  });
  const p = {
    id: "github-test",
    kind: "github",
    clientId: "test-client",
    clientSecret: "synthetic-secret",
    enabled: true,
    epoch: 1,
  };
  const { flow } = await beginLogin(p, "https://hub.example.com/auth/callback");
  let calls = 0;
  globalThis.fetch = async (url, options) => {
    calls++;
    if (String(url) === "https://github.com/login/oauth/access_token") {
      assert.equal(options.body.get("code_verifier"), flow.verifier);
      assert.equal(options.body.get("client_secret"), p.clientSecret);
      return Response.json({ access_token: "synthetic-access-token" });
    }
    assert.equal(String(url), "https://api.github.com/user");
    assert.equal(
      options.headers.Authorization,
      "Bearer synthetic-access-token",
    );
    return Response.json({
      id: 1001,
      login: "user-test",
      email: "untrusted@example.com",
    });
  };
  const identity = await finishLogin(
    p,
    flow,
    new URL(
      `https://hub.example.com/auth/callback?code=synthetic-code&state=${flow.state}`,
    ),
  );
  assert.deepEqual(identity, { subject: "1001", name: "user-test" });
  assert.equal(calls, 2);
});
test("OIDC discovery and signed tokens validate issuer, audience, nonce, expiry, state and PKCE", async (t) => {
  const original = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = original;
  });
  const { privateKey, publicKey } = await generateKeyPair("RS256"),
    jwk = await exportJWK(publicKey);
  jwk.kid = "synthetic-key";
  jwk.alg = "RS256";
  jwk.use = "sig";
  const issuer = "https://identity.example.com",
    p = {
      id: "oidc-test",
      kind: "oidc",
      issuer,
      clientId: "test-client",
      clientSecret: "synthetic-secret",
      enabled: true,
      epoch: 1,
    };
  let currentFlow,
    invalid = "",
    jwksRequested = false;
  globalThis.fetch = async (input, options) => {
    const url = String(input);
    if (url.endsWith("/.well-known/openid-configuration"))
      return Response.json({
        issuer,
        authorization_endpoint: `${issuer}/authorize`,
        token_endpoint: `${issuer}/token`,
        jwks_uri: `${issuer}/jwks`,
        response_types_supported: ["code"],
        id_token_signing_alg_values_supported: ["RS256"],
        token_endpoint_auth_methods_supported: ["client_secret_post"],
      });
    if (url === `${issuer}/jwks`) {
      jwksRequested = true;
      return Response.json({ keys: [jwk] });
    }
    assert.equal(url, `${issuer}/token`);
    assert.equal(options.body.get("code_verifier"), currentFlow.verifier);
    const token = await new SignJWT({
      nonce: invalid === "nonce" ? "wrong" : currentFlow.nonce,
      name: "OIDC User",
    })
      .setProtectedHeader({ alg: "RS256", kid: "synthetic-key" })
      .setIssuer(invalid === "issuer" ? "https://wrong.example.com" : issuer)
      .setAudience(invalid === "audience" ? "wrong-client" : p.clientId)
      .setSubject("stable-test-subject")
      .setIssuedAt()
      .setExpirationTime(invalid === "expiry" ? 0 : "5m")
      .sign(privateKey);
    return Response.json({
      access_token: "synthetic-access",
      id_token: token,
      token_type: "Bearer",
      expires_in: 300,
    });
  };
  for (const mode of ["", "nonce", "issuer", "audience", "expiry"]) {
    invalid = mode;
    const result = await beginLogin(p, "https://hub.example.com/auth/callback");
    currentFlow = result.flow;
    const target = new URL(
      `https://hub.example.com/auth/callback?code=synthetic-code&state=${currentFlow.state}`,
    );
    if (mode) await assert.rejects(finishLogin(p, currentFlow, target));
    else
      assert.deepEqual(await finishLogin(p, currentFlow, target), {
        subject: "stable-test-subject",
        name: "OIDC User",
      });
  }
  assert.equal(jwksRequested, true);
});
