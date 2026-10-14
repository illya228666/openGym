import https from 'node:https';
import dns from 'node:dns';
import { BlockList, isIP } from 'node:net';
import { errors } from 'oidc-provider';

export { MaxDynamicClients } from './mcp-config.js';
const MetadataBytes = 32768;
const MetadataTimeoutMilliseconds = 3000;
const blocked = new BlockList();
for (const [address, prefix] of [['0.0.0.0', 8], ['10.0.0.0', 8], ['100.64.0.0', 10], ['127.0.0.0', 8],
  ['169.254.0.0', 16], ['172.16.0.0', 12], ['192.0.0.0', 24], ['192.0.2.0', 24], ['192.168.0.0', 16],
  ['198.18.0.0', 15], ['198.51.100.0', 24], ['203.0.113.0', 24], ['224.0.0.0', 3]]) blocked.addSubnet(address, prefix, 'ipv4');
for (const [address, prefix] of [['2001::', 23], ['2001:db8::', 32], ['2002::', 16], ['3fff::', 20]])
  blocked.addSubnet(address, prefix, 'ipv6');

export function publicAddress(address) {
  const family = isIP(address);
  // IPv6 outside global unicast also includes mapped IPv4, NAT64 and link/local ranges.
  return family === 4 ? !blocked.check(address, 'ipv4')
    : family === 6 && /^[23]/i.test(address) && !blocked.check(address, 'ipv6');
}

export function metadataUrl(value) {
  if (typeof value !== 'string' || value.length > 2048 || /[\s\\\x00-\x1f\x7f]/.test(value)) return null;
  let url;
  try { url = new URL(value); } catch { return null; }
  return url.protocol === 'https:' && !url.username && !url.password && !url.hash
    && (!url.port || url.port === '443') && url.pathname !== '/' && !isIP(url.hostname.replace(/^\[|\]$/g, ''))
    && url.hostname.includes('.') && !url.hostname.endsWith('.') ? url : null;
}

/** Exact public HTTPS/loopback callbacks, code flow only. No metadata may trigger another fetch. */
export function validateClientMetadata(value, cimd = false) {
  const fail = message => { throw new errors.InvalidClientMetadata(message); };
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail('client metadata must be an object');
  if (cimd && (!metadataUrl(value.client_id) || typeof value.client_name !== 'string' || !value.client_name.trim()))
    fail('CIMD requires an HTTPS document client_id and client_name');
  if (value.client_name !== undefined && (typeof value.client_name !== 'string' || value.client_name.length > 100 || /[\x00-\x1f]/.test(value.client_name)))
    fail('invalid client_name');
  if (!Array.isArray(value.redirect_uris) || !value.redirect_uris.length || value.redirect_uris.length > 10) fail('1 to 10 exact redirect_uris required');
  for (const redirect of value.redirect_uris) {
    let uri;
    try { uri = new URL(redirect); } catch { fail('invalid redirect_uri'); }
    const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(uri.hostname);
    if (typeof redirect !== 'string' || redirect.length > 2048 || /[\s\\*\x00-\x1f]/.test(redirect)
      || uri.username || uri.password || uri.hash || !(uri.protocol === 'https:' || uri.protocol === 'http:' && loopback)
      || (!loopback && (isIP(uri.hostname.replace(/^\[|\]$/g, '')) || !uri.hostname.includes('.')))) fail('unsafe redirect_uri');
  }
  if ((value.token_endpoint_auth_method ?? 'none') !== 'none') fail('only public PKCE clients are supported');
  if (value.application_type !== undefined && !['web', 'native'].includes(value.application_type)) fail('invalid application_type');
  for (const [key, allowed] of [['grant_types', ['authorization_code', 'refresh_token']], ['response_types', ['code']]]) {
    if (value[key] !== undefined && (!Array.isArray(value[key]) || !value[key].length || value[key].some(item => !allowed.includes(item))))
      fail('unsupported ' + key);
  }
  for (const field of ['client_secret', 'sector_identifier_uri', 'request_uris', 'initiate_login_uri', 'jwks', 'jwks_uri'])
    if (value[field] !== undefined) fail('unsupported ' + field);
  return value;
}

/** ChatGPT's plural methods are capabilities, not an ordered preference. */
export function normalizeCimd(value) {
  const methods = value?.token_endpoint_auth_methods_supported;
  if (methods !== undefined) {
    if (!Array.isArray(methods) || !methods.length || methods.some(method => typeof method !== 'string') || !methods.includes('none'))
      throw new errors.InvalidClientMetadata('no supported public-client authentication method');
    value = { ...value, token_endpoint_auth_method: 'none' };
  }
  // Keys for private_key_jwt are not used or fetched when the negotiated method is none.
  if (value?.token_endpoint_auth_method === 'none') {
    value = { ...value }; delete value.jwks_uri; delete value.jwks;
  }
  return validateClientMetadata(value, true);
}

/** DNS validation is in the actual socket lookup; redirects, cookies and proxy input are never used. */
export function createMetadataFetch({ lookup = dns.lookup, request = https.request } = {}) {
  return async raw => {
    const url = metadataUrl(raw);
    if (!url) throw new errors.InvalidClient('unsafe metadata URL');
    const response = await new Promise((resolve, reject) => {
      const req = request(url, { method: 'GET', agent: false, headers: { Accept: 'application/json' },
        lookup: (hostname, options, callback) => lookup(hostname, { ...options, all: true }, (error, addresses) => {
          if (error) return callback(error);
          if (!addresses?.length || addresses.some(item => !publicAddress(item.address)))
            return callback(new Error('metadata DNS is not public'));
          callback(null, options.all ? addresses : addresses[0].address, addresses[0].family);
        }) }, incoming => {
        const chunks = []; let bytes = 0;
        if (incoming.statusCode !== 200 || !/^application\/(json|[^;]+\+json)(;|$)/i.test(incoming.headers['content-type'] || '')) {
          incoming.resume(); req.destroy(new Error('metadata must be JSON without redirects')); return;
        }
        incoming.on('data', chunk => {
          bytes += chunk.length;
          if (bytes > MetadataBytes) req.destroy(new Error('metadata too large')); else chunks.push(chunk);
        });
        incoming.on('error', reject);
        incoming.on('end', () => resolve({ text: Buffer.concat(chunks).toString('utf8'), headers: incoming.headers }));
      });
      const timer = setTimeout(() => req.destroy(new Error('metadata timeout')), MetadataTimeoutMilliseconds);
      req.on('close', () => clearTimeout(timer)); req.on('error', reject); req.end();
    });
    const metadata = normalizeCimd(JSON.parse(response.text));
    if (metadata.client_id !== raw) throw new errors.InvalidClientMetadata('metadata client_id mismatch');
    return new Response(JSON.stringify(metadata), { headers: { 'Content-Type': 'application/json',
      'Cache-Control': response.headers['cache-control'] || 'max-age=300' } });
  };
}
