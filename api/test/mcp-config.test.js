import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { createMcpConfiguration } from '../mcp-config.js';

const origin = 'https://gym.example';
const directory = t => {
  const data = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-mcp-settings-'));
  t.after(() => fs.rmSync(data, { recursive: true, force: true })); return data;
};
test('MCP switch applies immediately and persists without manual clients', t => {
  const data = directory(t);
  const config = createMcpConfiguration({ data, origin, env: {} });
  assert.deepEqual(config.status(), { enabled: false, url: origin + '/mcp' });
  config.save({ enabled: true });
  assert.deepEqual(config.status(), { enabled: true, url: origin + '/mcp' });
  assert.deepEqual(config.effective.clients, []);
  assert.equal(createMcpConfiguration({ data, origin, env: { MCP_ENABLED: '0' } }).status().enabled, true);
  config.save({ enabled: false });
  assert.equal(config.effective.enabled, false);
  config.save({ enabled: true });
  assert.equal(config.effective.enabled, true);
});
test('MCP switch preserves legacy URL and clients but rejects live protocol reconfiguration', t => {
  const data = directory(t);
  const settings = { enabled: true, url: origin + '/gym/mcp', clients: [
    { client_id: 'legacy', client_name: 'Legacy', redirect_uris: ['https://client.example/callback'] }
  ] };
  fs.writeFileSync(path.join(data, 'mcp-settings.json'), JSON.stringify({ version: 1, settings }));
  const config = createMcpConfiguration({ data, origin, env: {} });
  config.save({ enabled: false }); config.save({ enabled: true });
  assert.deepEqual(config.effective, settings);
  for (const value of [{ enabled: 'true' }, {}, { enabled: true, clients: [] }, { enabled: true, url: 'https://evil.example/mcp' }])
    assert.throws(() => config.save(value), { status: 400 });
  assert.deepEqual(createMcpConfiguration({ data, origin, env: {} }).effective, settings);
  const local = createMcpConfiguration({ data: directory(t), origin: 'http://gym.local', env: {} });
  assert.throws(() => local.save({ enabled: true }), { status: 400 });
  assert.equal(local.effective.enabled, false);
});
test('MCP persistence failure keeps the active switch unchanged and corrupt settings fail closed', t => {
  const data = directory(t);
  const config = createMcpConfiguration({ data, origin, env: {} });
  const file = path.join(data, 'mcp-settings.json');
  fs.mkdirSync(file + '.tmp');
  assert.throws(() => config.save({ enabled: true }));
  assert.equal(config.status().enabled, false);
  fs.writeFileSync(file, '{}');
  assert.throws(() => createMcpConfiguration({ data, origin, env: {} }), /unreadable/);
});
