import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createMetadataFetch, metadataUrl, publicAddress, validateClientMetadata, normalizeCimd } from '../mcp-clients.js';
import { documentUrl, clientDocument, metadataTransport } from './mcp-metadata-fixture.mjs';

const publicLookup = (_host, _options, callback) => callback(null, [{ address: '93.184.216.34', family: 4 }]);

test('CIMD negotiates ChatGPT plural methods without trusting the legacy preference', () => {
  const metadata = normalizeCimd(clientDocument());
  assert.equal(metadata.token_endpoint_auth_method, 'none');
  assert.equal(metadata.jwks_uri, undefined);
  assert.deepEqual(metadata.redirect_uris, ['https://client.example/callback']);
  assert.throws(() => normalizeCimd({ ...clientDocument(), token_endpoint_auth_methods_supported: ['private_key_jwt'] }), { error: 'invalid_client_metadata' });
  assert.throws(() => normalizeCimd({ ...clientDocument(), token_endpoint_auth_methods_supported: 'none' }), { error: 'invalid_client_metadata' });
  assert.throws(() => normalizeCimd({ ...clientDocument(), client_secret: 'secret' }), { error: 'invalid_client_metadata' });
  assert.throws(() => normalizeCimd({ ...clientDocument(), client_name: '' }), { error: 'invalid_client_metadata' });
});

test('OAuth metadata rejects unsafe callbacks and flows while accepting public code clients', () => {
  const good = { redirect_uris: ['https://client.example/callback'], token_endpoint_auth_method: 'none' };
  assert.deepEqual(validateClientMetadata(good), good);
  assert.ok(validateClientMetadata({ redirect_uris: ['http://127.0.0.1:4173/callback'], application_type: 'native' }));
  for (const redirect of ['http://evil.example/callback', 'https://client.example/*', 'https://user:pass@client.example/cb',
    'https://client.example/cb#fragment', 'javascript:alert(1)', 'https://192.168.0.1/cb', 'https://intranet/cb', 'https://client.example\\evil/cb'])
    assert.throws(() => validateClientMetadata({ ...good, redirect_uris: [redirect] }));
  for (const value of [{ redirect_uris: [] }, { redirect_uris: Array(11).fill(good.redirect_uris[0]) },
    { ...good, sector_identifier_uri: 'https://evil.example/sector' }, { ...good, jwks_uri: 'https://evil.example/key' },
    { ...good, request_uris: ['https://evil.example/request'] }, { ...good, token_endpoint_auth_method: 'client_secret_basic' },
    { ...good, grant_types: ['client_credentials'] }, { ...good, response_types: ['token'] }]) assert.throws(() => validateClientMetadata(value));
});

test('CIMD URL and DNS checks block loopback, reserved and encoded SSRF targets', () => {
  assert.ok(metadataUrl(documentUrl));
  for (const url of ['http://client.example/client.json', 'https://localhost/client.json', 'https://127.0.0.1/a',
    'https://0x7f000001/a', 'https://[::ffff:7f00:1]/a', 'https://client.example:444/a',
    'https://u:p@client.example/a', 'https://client.example/a#x', 'https://client.example/'])
    assert.equal(metadataUrl(url), null, url);
  for (const address of ['127.0.0.1', '10.0.0.1', '169.254.169.254', '100.64.0.1', '172.16.0.1', '192.168.1.1',
    '198.18.0.1', '198.51.100.1', '224.0.0.1', '::1', '::ffff:93.184.216.34', 'fc00::1', 'fe80::1', 'ff02::1',
    '64:ff9b::7f00:1', '2001:db8::1', '2002:7f00:1::']) assert.equal(publicAddress(address), false, address);
  for (const address of ['93.184.216.34', '1.1.1.1', '2606:4700::1111']) assert.equal(publicAddress(address), true, address);
});

test('CIMD HTTPS fetch pins public DNS and rejects redirects, oversized, malformed and mismatched documents', async () => {
  const request = metadataTransport(clientDocument(), { inspect: (url, options) => {
    assert.equal(url.href, documentUrl); assert.equal(options.agent, false);
    assert.deepEqual(options.headers, { Accept: 'application/json' });
  } });
  const response = await createMetadataFetch({ lookup: publicLookup, request })(documentUrl);
  assert.equal((await response.json()).token_endpoint_auth_method, 'none');
  for (const [document, options] of [[clientDocument(), { status: 302 }], [clientDocument(), { type: 'text/html' }],
    ['x'.repeat(32769), {}], ['{broken', {}], [{ ...clientDocument(), client_id: 'https://evil.example/client.json' }, {}]])
    await assert.rejects(createMetadataFetch({ lookup: publicLookup, request: metadataTransport(document, options) })(documentUrl));
  await assert.rejects(createMetadataFetch({ request, lookup: (_host, options, callback) => {
    assert.equal(options.all, true);
    callback(null, [{ address: '93.184.216.34', family: 4 }, { address: '127.0.0.1', family: 4 }]);
  } })(documentUrl), /not public/);
});
