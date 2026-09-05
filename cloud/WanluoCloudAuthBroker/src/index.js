const BAIDU_AUTHORIZE = "https://openapi.baidu.com/oauth/2.0/authorize";
const BAIDU_TOKEN = "https://openapi.baidu.com/oauth/2.0/token";
const STATE_LIFETIME_SECONDS = 600;
const MAX_JSON_BYTES = 32768;

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      if (request.method === "GET" && url.pathname === "/health") {
        return json({ ok: true, service: "wanluo-cloud-auth-broker", version: 1 });
      }
      if (request.method === "POST" && url.pathname === "/v1/baidu/authorize") {
        const limited = await rateLimit(request, env, url.pathname);
        if (limited) return limited;
        return await authorize(request, env);
      }
      if (request.method === "GET" && url.pathname === "/oauth/baidu/callback") {
        return await callback(url, env);
      }
      if (request.method === "POST" && url.pathname === "/v1/baidu/refresh") {
        const limited = await rateLimit(request, env, url.pathname);
        if (limited) return limited;
        return await refresh(request, env);
      }
      return json({ error: "not_found" }, 404);
    } catch (error) {
      return json({ error: "request_failed", message: safeMessage(error) }, 400);
    }
  }
};

async function authorize(request, env) {
  requireConfig(env);
  const body = await readJson(request);
  validateClientEnvelope(body);
  const state = await encryptState({
    v: 1,
    exp: Math.floor(Date.now() / 1000) + STATE_LIFETIME_SECONDS,
    port: body.port,
    nonce: body.nonce,
    publicKey: body.public_key
  }, env.STATE_ENCRYPTION_KEY);
  const url = new URL(BAIDU_AUTHORIZE);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("client_id", env.BAIDU_CLIENT_ID);
  url.searchParams.set("redirect_uri", env.BAIDU_REDIRECT_URI);
  url.searchParams.set("scope", "basic,netdisk");
  url.searchParams.set("display", "popup");
  url.searchParams.set("state", state);
  return json({ authorize_url: url.toString(), expires_in: STATE_LIFETIME_SECONDS });
}

async function callback(url, env) {
  requireConfig(env);
  const state = await decryptState(requiredQuery(url, "state"), env.STATE_ENCRYPTION_KEY);
  validateState(state);
  const code = requiredQuery(url, "code");
  const token = await exchangeToken({
    grant_type: "authorization_code",
    code,
    client_id: env.BAIDU_CLIENT_ID,
    client_secret: env.BAIDU_CLIENT_SECRET,
    redirect_uri: env.BAIDU_REDIRECT_URI
  });
  const envelope = await encryptForClient(tokenPayload(token), state.publicKey);
  const local = new URL(`http://127.0.0.1:${state.port}/baidu-oauth`);
  local.searchParams.set("nonce", state.nonce);
  local.searchParams.set("payload", base64UrlEncode(textEncoder.encode(JSON.stringify(envelope))));
  return new Response(null, { status: 302, headers: securityHeaders({ Location: local.toString() }) });
}

async function refresh(request, env) {
  requireConfig(env);
  const body = await readJson(request);
  validateClientEnvelope(body);
  if (typeof body.refresh_token !== "string" || body.refresh_token.length < 8 || body.refresh_token.length > 8192) {
    throw new Error("invalid_refresh_token");
  }
  const token = await exchangeToken({
    grant_type: "refresh_token",
    refresh_token: body.refresh_token,
    client_id: env.BAIDU_CLIENT_ID,
    client_secret: env.BAIDU_CLIENT_SECRET
  });
  const envelope = await encryptForClient(tokenPayload(token), body.public_key);
  return json({ nonce: body.nonce, payload: base64UrlEncode(textEncoder.encode(JSON.stringify(envelope))) });
}

async function exchangeToken(parameters) {
  const url = new URL(BAIDU_TOKEN);
  for (const [key, value] of Object.entries(parameters)) url.searchParams.set(key, value);
  const response = await fetch(url, { method: "GET", redirect: "manual" });
  if (response.status >= 300 && response.status < 400) {
    throw new Error("baidu_token_redirect_rejected");
  }
  const token = await response.json();
  if (!response.ok || token.error || !token.access_token || !token.refresh_token) {
    throw new Error(`baidu_token_error:${token.error_description || token.error || response.status}`);
  }
  return token;
}

function tokenPayload(token) {
  return {
    access_token: token.access_token,
    refresh_token: token.refresh_token,
    expires_in: Number(token.expires_in || 2592000),
    issued_at: new Date().toISOString()
  };
}

async function encryptState(value, secret) {
  const key = await importAesKey(secret);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const plaintext = textEncoder.encode(JSON.stringify(value));
  const ciphertext = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, plaintext));
  const combined = new Uint8Array(iv.length + ciphertext.length);
  combined.set(iv); combined.set(ciphertext, iv.length);
  return base64UrlEncode(combined);
}

async function decryptState(encoded, secret) {
  const bytes = base64UrlDecode(encoded);
  if (bytes.length < 29) throw new Error("invalid_state");
  const key = await importAesKey(secret);
  const plaintext = await crypto.subtle.decrypt({ name: "AES-GCM", iv: bytes.slice(0, 12) }, key, bytes.slice(12));
  return JSON.parse(textDecoder.decode(plaintext));
}

async function encryptForClient(value, publicJwk) {
  const publicKey = await crypto.subtle.importKey("jwk", publicJwk, { name: "RSA-OAEP", hash: "SHA-256" }, false, ["encrypt"]);
  const keyMaterial = crypto.getRandomValues(new Uint8Array(64));
  const aesKey = await crypto.subtle.importKey("raw", keyMaterial.slice(0, 32), "AES-CBC", false, ["encrypt"]);
  const hmacKey = await crypto.subtle.importKey("raw", keyMaterial.slice(32), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const iv = crypto.getRandomValues(new Uint8Array(16));
  const ciphertext = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-CBC", iv }, aesKey, textEncoder.encode(JSON.stringify(value))));
  const authenticated = concatBytes(iv, ciphertext);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", hmacKey, authenticated));
  const wrappedKey = new Uint8Array(await crypto.subtle.encrypt({ name: "RSA-OAEP" }, publicKey, keyMaterial));
  return { v: 1, alg: "RSA-OAEP-256+A256CBC-HS256", key: base64UrlEncode(wrappedKey), iv: base64UrlEncode(iv), ciphertext: base64UrlEncode(ciphertext), mac: base64UrlEncode(mac) };
}

async function importAesKey(secret) {
  const bytes = base64UrlDecode(secret);
  if (bytes.length !== 32) throw new Error("invalid_state_encryption_key");
  return crypto.subtle.importKey("raw", bytes, "AES-GCM", false, ["encrypt", "decrypt"]);
}

function validateClientEnvelope(body) {
  if (!Number.isInteger(body.port) || body.port < 1024 || body.port > 65535) throw new Error("invalid_local_port");
  if (typeof body.nonce !== "string" || body.nonce.length < 16 || body.nonce.length > 128) throw new Error("invalid_nonce");
  const key = body.public_key;
  if (!key || key.kty !== "RSA" || typeof key.n !== "string" || typeof key.e !== "string") throw new Error("invalid_public_key");
}

function validateState(state) {
  validateClientEnvelope({ port: state.port, nonce: state.nonce, public_key: state.publicKey });
  const now = Math.floor(Date.now() / 1000);
  if (!Number.isInteger(state.exp) || state.exp < now || state.exp > now + STATE_LIFETIME_SECONDS + 30) throw new Error("expired_state");
}

function requireConfig(env) {
  for (const name of ["BAIDU_CLIENT_ID", "BAIDU_CLIENT_SECRET", "BAIDU_REDIRECT_URI", "STATE_ENCRYPTION_KEY"]) {
    if (!env[name] || String(env[name]).startsWith("REPLACE_")) throw new Error(`missing_configuration:${name}`);
  }
  const redirect = new URL(env.BAIDU_REDIRECT_URI);
  if (redirect.protocol !== "https:") throw new Error("redirect_uri_must_be_https");
}

async function readJson(request) {
  const header = request.headers.get("content-length");
  if (header !== null) {
    const declared = Number(header);
    if (!Number.isFinite(declared) || declared < 0 || declared > MAX_JSON_BYTES) throw new Error("request_too_large");
  }
  if (!request.body) throw new Error("invalid_json");
  const reader = request.body.getReader();
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > MAX_JSON_BYTES) {
        await reader.cancel("request_too_large");
        throw new Error("request_too_large");
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  try { return JSON.parse(textDecoder.decode(bytes)); }
  catch { throw new Error("invalid_json"); }
}

async function rateLimit(request, env, route) {
  if (!env.AUTH_RATE_LIMITER) return null;
  const address = request.headers.get("CF-Connecting-IP") || "unknown";
  const result = await env.AUTH_RATE_LIMITER.limit({ key: `${address}:${route}` });
  return result.success ? null : json({ error: "rate_limited" }, 429);
}

function requiredQuery(url, name) {
  const value = url.searchParams.get(name);
  if (!value) throw new Error(`missing_${name}`);
  return value;
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: securityHeaders({ "Content-Type": "application/json; charset=utf-8" }) });
}

function securityHeaders(extra = {}) {
  return {
    "Cache-Control": "no-store",
    "Content-Security-Policy": "default-src 'none'; frame-ancestors 'none'",
    "Referrer-Policy": "no-referrer",
    "Strict-Transport-Security": "max-age=31536000; includeSubDomains",
    "X-Content-Type-Options": "nosniff",
    ...extra
  };
}

function safeMessage(error) {
  const message = error instanceof Error ? error.message : "unknown_error";
  return message.replace(/[?&](client_secret|refresh_token|code)=[^&\s]+/gi, "?$1=REDACTED").slice(0, 240);
}

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder();

function base64UrlEncode(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function base64UrlDecode(value) {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - normalized.length % 4) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, char => char.charCodeAt(0));
}

function concatBytes(first, second) {
  const result = new Uint8Array(first.length + second.length);
  result.set(first); result.set(second, first.length);
  return result;
}

export const testing = { encryptState, decryptState, encryptForClient, base64UrlEncode, base64UrlDecode, readJson, rateLimit };
