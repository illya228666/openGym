import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createMcpOAuth } from '../mcp-oauth.js';
import { createMetadataFetch } from '../mcp-clients.js';
import { documentUrl, clientDocument, metadataTransport } from './mcp-metadata-fixture.mjs';

for (const scenario of [
  { label: 'read-only', scopes: ['training.read'] },
  { label: 'full access', scopes: ['training.read', 'nutrition.read', 'nutrition.write'] }
])
test('CIMD consent and persistent refresh preserve scopes, account checks and revocation (' + scenario.label + ')', async t => {
  const data = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-cimd-'));
  let oauth; let fetches = 0;
  const json = (res, status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  const server = http.createServer(async (req, res) => {
    try { if (!await oauth.handle(req, res, new URL(req.url, 'http://x').pathname)) json(res, 404, {}); }
    catch (error) { json(res, 400, { error: error.message }); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { await new Promise(resolve => server.close(resolve)); fs.rmSync(data, { recursive: true, force: true }); });
  const origin = 'http://localhost:' + server.address().port;
  const user = { id: 'alice', name: 'Alice' };
  let deleted = false;
  const options = { data, secret: 'secret', origin, userById: id => !deleted && id === user.id ? user : null,
    readSession: req => req.headers.cookie?.includes('opengym=alice') ? user : null, json,
    metadataFetch: createMetadataFetch({ lookup: (_host, _options, callback) => callback(null, [{ address: '93.184.216.34', family: 4 }]),
      request: metadataTransport({ ...clientDocument(), grant_types: ['authorization_code', 'refresh_token'] }, { inspect: () => fetches++ }) }) };
  oauth = createMcpOAuth(options);
  const call = (pathname, options = {}) => fetch(origin + pathname, { ...options, redirect: 'manual' });
  const metadata = await (await call('/.well-known/oauth-authorization-server')).json();
  assert.equal(metadata.client_id_metadata_document_supported, true);
  assert.equal(metadata.registration_endpoint, origin + '/oauth/reg');
  const verifier = crypto.randomBytes(32).toString('base64url');
  const params = { client_id: documentUrl, redirect_uri: 'https://client.example/callback', scope: scenario.scopes.join(' ') + ' offline_access',
    response_type: 'code', resource: origin + '/mcp', state: 'state', prompt: 'consent',
    code_challenge: crypto.createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256' };
  const jar = new Map();
  const browser = async (pathname, options = {}) => {
    const response = await call(pathname, { ...options, headers: { ...options.headers,
      Cookie: [...jar].map(([key, value]) => `${key}=${value}`).join('; ') } });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(';')[0]; const split = pair.indexOf('='); jar.set(pair.slice(0, split), pair.slice(split + 1));
    }
    return response;
  };
  for (const redirect of ['https://evil.example/callback', 'https://client.example/not-registered', 'https://client.example/callback?unexpected=1']) {
    const rejected = await call('/oauth/auth?' + new URLSearchParams({ ...params, redirect_uri: redirect }));
    assert.equal(rejected.status, 400); assert.equal(rejected.headers.get('location'), null);
  }
  let response = await browser('/oauth/auth?' + new URLSearchParams(params));
  let code; let unsignedSeen = false;
  for (let step = 0; step < 12; step++) {
    const location = response.headers.get('location');
    if (location?.startsWith(params.redirect_uri)) {
      const callback = new URL(location); assert.equal(callback.searchParams.get('state'), 'state');
      assert.equal(callback.searchParams.get('error'), null); code = callback.searchParams.get('code'); break;
    }
    if (location) { const next = new URL(location, origin); response = await browser(next.pathname + next.search); continue; }
    const html = await response.text(); assert.equal(response.status, 200, html);
    assert.equal(response.headers.get('content-security-policy'),
      "default-src 'none'; style-src 'unsafe-inline'; form-action 'self' https://client.example; frame-ancestors 'none'");
    for (const scope of scenario.scopes) assert.ok(html.includes('<li>' + scope + '</li>'));
    if (!scenario.scopes.includes('nutrition.write')) assert.ok(!html.includes('<li>nutrition.write</li>'));
    if (!unsignedSeen) {
      assert.match(html, /Sign in to/); assert.doesNotMatch(html, /<form/);
      unsignedSeen = true; jar.set('opengym', 'alice'); response = await browser(new URL(response.url).pathname); continue;
    }
    const csrf = /name="csrf" value="([^"]+)"/.exec(html)?.[1]; assert.ok(csrf);
    const forged = await browser(new URL(response.url).pathname, { method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ csrf: 'wrong', decision: 'allow' }) });
    assert.equal(forged.status, 403);
    response = await browser(new URL(response.url).pathname, { method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ csrf, decision: 'allow' }) });
  }
  assert.ok(code); assert.equal(unsignedSeen, true); assert.equal(fetches, 1);
  const exchanged = await call('/oauth/token', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: documentUrl, grant_type: 'authorization_code', code, code_verifier: verifier,
      redirect_uri: params.redirect_uri, resource: params.resource }) });
  assert.equal(exchanged.status, 200, await exchanged.clone().text());
  let tokens = await exchanged.json();
  assert.ok(tokens.refresh_token, JSON.stringify(tokens));
  assert.equal(tokens.expires_in, 600);
  assert.equal((await oauth.verify(tokens.access_token, 'training.read')).user.id, 'alice');
  if (scenario.scopes.includes('nutrition.write')) {
    assert.deepEqual((await oauth.verify(tokens.access_token, 'nutrition.write')).scopes, scenario.scopes);
  } else await assert.rejects(oauth.verify(tokens.access_token, 'nutrition.write'), { status: 403 });
  const grant = oauth.grants('alice')[0]; assert.equal(grant.clientId, documentUrl);
  assert.equal(grant.clientName, 'Compatible client');
  assert.equal(oauth.store.get('Grant', grant.grantId).exp, undefined);
  assert.equal(oauth.store.list('RefreshToken').find(token => token.grantId === grant.grantId).exp, undefined);
  const refresh = token => call('/oauth/token', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'refresh_token', client_id: documentUrl, refresh_token: token, resource: params.resource }) });
  const initial = tokens;
  const now = Date.now();
  let elapsed = 31 * 86400000;
  t.mock.method(Date, 'now', () => now + elapsed);
  oauth = createMcpOAuth(options);
  await assert.rejects(oauth.verify(tokens.access_token), { status: 401 });
  assert.equal(oauth.grants('alice').length, 1);
  let renewed = await refresh(tokens.refresh_token);
  assert.equal(renewed.status, 200, await renewed.clone().text());
  tokens = await renewed.json();
  assert.equal((await oauth.verify(tokens.access_token)).user.id, 'alice');
  elapsed += 31 * 86400000;
  oauth = createMcpOAuth(options);
  renewed = await refresh(tokens.refresh_token);
  assert.equal(renewed.status, 200, await renewed.clone().text());
  tokens = await renewed.json();
  // An old consumed token is rejected after history expires without revoking the current chain.
  assert.equal((await refresh(initial.refresh_token)).status, 400);
  assert.deepEqual((await oauth.verify(tokens.access_token)).scopes, scenario.scopes);
  const principal = await oauth.verify(tokens.access_token);
  const delegated = oauth.nutritionCredential(principal, 'GET', '/api/nutrition/diary');
  user.sv = 1;
  assert.equal((await oauth.verify(tokens.access_token)).user.id, 'alice');
  assert.equal(oauth.grants('alice').length, 1);
  if (scenario.scopes.includes('nutrition.read')) assert.equal(oauth.nutritionIdentity(delegated).user.id, 'alice');
  user.disabled = true;
  await assert.rejects(oauth.verify(tokens.access_token), { status: 401 });
  assert.equal((await refresh(tokens.refresh_token)).status, 400);
  assert.equal(oauth.nutritionIdentity(delegated), null);
  user.disabled = false;
  deleted = true;
  await assert.rejects(oauth.verify(tokens.access_token), { status: 401 });
  assert.equal((await refresh(tokens.refresh_token)).status, 400);
  deleted = false;
  oauth.store.revoke(grant.grantId);
  await assert.rejects(oauth.verify(tokens.access_token), { status: 401 });
  assert.equal((await refresh(tokens.refresh_token)).status, 400);
  assert.equal(oauth.nutritionIdentity(delegated), null);
});
