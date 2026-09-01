import test from "node:test";
import assert from "node:assert/strict";
import worker from "../src/index.js";

globalThis.btoa ??= value => Buffer.from(value, "binary").toString("base64");
globalThis.atob ??= value => Buffer.from(value, "base64").toString("binary");

const stateKey = Buffer.alloc(32, 7).toString("base64url");
const env = {
  BAIDU_CLIENT_ID: "public-app-key",
  BAIDU_CLIENT_SECRET: "server-only-secret",
  BAIDU_REDIRECT_URI: "https://auth.example.test/oauth/baidu/callback",
  STATE_ENCRYPTION_KEY: stateKey
};

async function clientKeys() {
  const pair = await crypto.subtle.generateKey({ name: "RSA-OAEP", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" }, true, ["encrypt", "decrypt"]);
  return { pair, publicJwk: await crypto.subtle.exportKey("jwk", pair.publicKey) };
}

test("health does not expose configuration", async () => {
  const response = await worker.fetch(new Request("https://auth.example.test/health"), env);
  assert.equal(response.status, 200);
  const text = await response.text();
  assert.doesNotMatch(text, /public-app-key|server-only-secret/);
});

test("authorize creates a short-lived encrypted state", async () => {
  const { publicJwk } = await clientKeys();
  const response = await worker.fetch(new Request("https://auth.example.test/v1/baidu/authorize", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ port: 43821, nonce: "0123456789abcdef0123456789abcdef", public_key: publicJwk })
  }), env);
  assert.equal(response.status, 200);
  const result = await response.json();
  const url = new URL(result.authorize_url);
  assert.equal(url.origin + url.pathname, "https://openapi.baidu.com/oauth/2.0/authorize");
  assert.equal(url.searchParams.get("client_id"), env.BAIDU_CLIENT_ID);
  assert.equal(url.searchParams.get("redirect_uri"), env.BAIDU_REDIRECT_URI);
  assert.ok(url.searchParams.get("state"));
  assert.doesNotMatch(url.searchParams.get("state"), /43821|0123456789abcdef/);
});

test("callback returns an encrypted one-time localhost payload", async () => {
  const { pair, publicJwk } = await clientKeys();
  const start = await worker.fetch(new Request("https://auth.example.test/v1/baidu/authorize", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ port: 43822, nonce: "abcdef0123456789abcdef0123456789", public_key: publicJwk })
  }), env);
  const state = new URL((await start.json()).authorize_url).searchParams.get("state");
  const realFetch = globalThis.fetch;
  globalThis.fetch = async url => {
    assert.equal(new URL(url).searchParams.get("client_secret"), env.BAIDU_CLIENT_SECRET);
    return new Response(JSON.stringify({ access_token: "access-value", refresh_token: "refresh-value", expires_in: 3600 }), { status: 200 });
  };
  try {
    const response = await worker.fetch(new Request(`${env.BAIDU_REDIRECT_URI}?code=code-value&state=${encodeURIComponent(state)}`), env);
    assert.equal(response.status, 302);
    const local = new URL(response.headers.get("location"));
    assert.equal(local.origin, "http://127.0.0.1:43822");
    assert.equal(local.searchParams.get("nonce"), "abcdef0123456789abcdef0123456789");
    const envelope = JSON.parse(Buffer.from(local.searchParams.get("payload"), "base64url").toString("utf8"));
    assert.equal(envelope.alg, "RSA-OAEP-256+A256CBC-HS256");
    const keyMaterial = Buffer.from(await crypto.subtle.decrypt({ name: "RSA-OAEP" }, pair.privateKey, Buffer.from(envelope.key, "base64url")));
    const iv = Buffer.from(envelope.iv, "base64url");
    const ciphertext = Buffer.from(envelope.ciphertext, "base64url");
    const hmacKey = await crypto.subtle.importKey("raw", keyMaterial.subarray(32), { name: "HMAC", hash: "SHA-256" }, false, ["verify"]);
    assert.equal(await crypto.subtle.verify("HMAC", hmacKey, Buffer.from(envelope.mac, "base64url"), Buffer.concat([iv, ciphertext])), true);
    const aesKey = await crypto.subtle.importKey("raw", keyMaterial.subarray(0, 32), "AES-CBC", false, ["decrypt"]);
    const plaintext = await crypto.subtle.decrypt({ name: "AES-CBC", iv }, aesKey, ciphertext);
    const token = JSON.parse(Buffer.from(plaintext).toString("utf8"));
    assert.equal(token.access_token, "access-value");
    assert.equal(token.refresh_token, "refresh-value");
    assert.doesNotMatch(response.headers.get("location"), /access-value|refresh-value/);
  } finally {
    globalThis.fetch = realFetch;
  }
});

test("tampered state is rejected before token exchange", async () => {
  const response = await worker.fetch(new Request(`${env.BAIDU_REDIRECT_URI}?code=x&state=not-a-valid-state`), env);
  assert.equal(response.status, 400);
  assert.match(await response.text(), /request_failed/);
});

test("authorize rejects streamed bodies larger than 32 KiB", async () => {
  const response = await worker.fetch(new Request("https://auth.example.test/v1/baidu/authorize", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ padding: "x".repeat(33000) })
  }), env);
  assert.equal(response.status, 400);
  assert.match(await response.text(), /request_too_large/);
});

test("authorization endpoints return 429 when the binding denies a request", async () => {
  let receivedKey = "";
  const limitedEnv = {
    ...env,
    AUTH_RATE_LIMITER: {
      async limit({ key }) { receivedKey = key; return { success: false }; }
    }
  };
  const response = await worker.fetch(new Request("https://auth.example.test/v1/baidu/authorize", {
    method: "POST",
    headers: { "content-type": "application/json", "CF-Connecting-IP": "192.0.2.7" },
    body: "{}"
  }), limitedEnv);
  assert.equal(response.status, 429);
  assert.deepEqual(await response.json(), { error: "rate_limited" });
  assert.equal(receivedKey, "192.0.2.7:/v1/baidu/authorize");
});
