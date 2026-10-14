import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { Readable } from 'node:stream';

export const documentUrl = 'https://client.example/oauth/client.json';
export const clientDocument = () => ({ client_id: documentUrl, client_name: 'Compatible client',
  redirect_uris: ['https://client.example/callback'], token_endpoint_auth_methods_supported: ['none', 'private_key_jwt'],
  token_endpoint_auth_method: 'private_key_jwt', jwks_uri: 'https://client.example/oauth/jwks.json' });

export function metadataTransport(document, { status = 200, type = 'application/json', inspect = () => {} } = {}) {
  return (url, options, receive) => {
    inspect(url, options);
    const req = new EventEmitter();
    req.destroy = error => { queueMicrotask(() => { req.emit('error', error); req.emit('close'); }); return req; };
    req.end = () => options.lookup(url.hostname, { all: false }, (error, address) => {
      if (error) return req.destroy(error);
      assert.equal(address, '93.184.216.34');
      const incoming = Readable.from([Buffer.from(typeof document === 'string' ? document : JSON.stringify(document))]);
      incoming.statusCode = status; incoming.headers = { 'content-type': type, 'cache-control': 'max-age=300' };
      incoming.on('end', () => req.emit('close')); receive(incoming);
    });
    return req;
  };
}
