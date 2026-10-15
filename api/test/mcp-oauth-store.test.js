import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createOAuthStore } from '../mcp-oauth-store.js';

test('registered OAuth clients remain durable beyond grant expiry', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-client-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'oauth.json');
  const store = createOAuthStore(file, 'secret');
  await new store.Adapter('Client').upsert('client', { client_id: 'client', redirect_uris: ['https://client.example/callback'] });
  const now = Date.now(); t.mock.method(Date, 'now', () => now + 31 * 86400000);
  const restarted = createOAuthStore(file, 'secret');
  assert.equal((await new restarted.Adapter('Client').find('client')).client_id, 'client');
});

test('persistent OAuth grants survive inactivity while consumed refresh history expires at 30 days', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-persistent-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'oauth.json');
  let store = createOAuthStore(file, 'secret');
  const now = Date.now();
  let elapsed = 0;
  t.mock.method(Date, 'now', () => now + elapsed);
  store.set('OpenGymGrant', 'grant', { grantId: 'grant', userId: 'alice', persistent: true }, null);
  await new store.Adapter('Grant').upsert('grant', { accountId: 'alice', exp: 1 }, 1);
  const refresh = new store.Adapter('RefreshToken');
  await refresh.upsert('used', { grantId: 'grant', exp: 1 }, 1);
  await refresh.upsert('current', { grantId: 'grant', exp: 1 }, 1);
  await refresh.consume('used');
  elapsed = 30 * 86400000 - 1;
  store = createOAuthStore(file, 'secret');
  assert.ok((await new store.Adapter('RefreshToken').find('used')).consumed);
  elapsed++;
  assert.equal(await new store.Adapter('RefreshToken').find('used'), undefined);
  elapsed = 100 * 365 * 86400000;
  store = createOAuthStore(file, 'secret');
  assert.equal(store.get('Grant', 'grant').exp, undefined);
  assert.equal(store.get('RefreshToken', 'current').grantId, 'grant');
  assert.equal(store.get('RefreshToken', 'current').exp, undefined);
  assert.equal(store.list('OpenGymGrant').length, 1);
  store.revoke('grant');
  assert.equal(store.get('RefreshToken', 'current'), undefined);
  assert.equal(createOAuthStore(file, 'secret').get('Grant', 'grant'), undefined);
});

test('legacy migration preserves only live authorized connections without reviving old tokens or scopes', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-migration-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'oauth.json');
  let store = createOAuthStore(file, 'secret');
  for (const id of ['live', 'session-revoked', 'expired', 'expired-payload', 'missing-grant']) {
    const seconds = id === 'expired' ? -1 : 600;
    store.set('Grant', id, { accountId: 'alice', exp: Math.floor(Date.now() / 1000) + seconds }, seconds);
    store.set('OpenGymGrant', id, { grantId: id, userId: 'alice', sessionVersion: 0, scopes: ['training.read'] }, seconds);
    store.set('RefreshToken', id, { grantId: id, exp: Math.floor(Date.now() / 1000) + seconds }, seconds);
  }
  store.set('Grant', 'expired-payload', { accountId: 'alice', exp: 1 }, 600);
  store.remove('Grant', 'missing-grant');
  store.set('RefreshToken', 'old-expired', { grantId: 'live', exp: 1 }, -1);
  store.set('RefreshToken', 'consumed', { grantId: 'live', consumed: 1, exp: 2 }, 600);
  store = createOAuthStore(file, 'secret', binding => binding.grantId !== 'session-revoked');
  assert.equal(store.get('OpenGymGrant', 'live').persistent, true);
  assert.deepEqual(store.get('OpenGymGrant', 'live').scopes, ['training.read']);
  assert.equal(store.get('OpenGymGrant', 'session-revoked').persistent, undefined);
  assert.equal(store.get('OpenGymGrant', 'missing-grant').persistent, undefined);
  assert.equal(store.get('OpenGymGrant', 'expired-payload').persistent, undefined);
  assert.equal(store.get('OpenGymGrant', 'expired'), undefined);
  assert.equal(store.get('RefreshToken', 'old-expired'), undefined);
  assert.equal(store.get('RefreshToken', 'consumed').exp, 2);
  const now = Date.now(); t.mock.method(Date, 'now', () => now + 31 * 86400000);
  store = createOAuthStore(file, 'secret');
  assert.equal(store.get('Grant', 'live').exp, undefined);
  assert.equal(store.get('RefreshToken', 'live').exp, undefined);
  assert.equal(store.get('RefreshToken', 'session-revoked'), undefined);
  assert.equal(store.get('OpenGymGrant', 'missing-grant'), undefined);
});

test('OAuth artifacts survive restart encrypted and revoke as one grant', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-store-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'oauth.json');
  const store = createOAuthStore(file, 'test-secret');
  const grants = new store.Adapter('Grant');
  const tokens = new store.Adapter('AccessToken');
  await grants.upsert('g1', { accountId: 'alice' }, 3600);
  await tokens.upsert('sensitive-access-token', { grantId: 'g1', accountId: 'alice' }, 600);
  assert.ok(!fs.readFileSync(file, 'utf8').includes('alice'));
  assert.ok(!fs.readFileSync(file, 'utf8').includes('sensitive-access-token'));
  const restarted = createOAuthStore(file, 'test-secret');
  assert.equal((await new restarted.Adapter('AccessToken').find('sensitive-access-token')).accountId, 'alice');
  restarted.revoke('g1');
  assert.equal(await new restarted.Adapter('AccessToken').find('sensitive-access-token'), undefined);
  assert.equal(await new restarted.Adapter('Grant').find('g1'), undefined);
  assert.throws(() => createOAuthStore(file, 'wrong-secret'), /unreadable/);
  fs.writeFileSync(file, '{}');
  assert.throws(() => createOAuthStore(file, 'test-secret'), /unreadable/);
});

test('OAuth adapter rejects expired and account-revoked artifacts and preserves consume state', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-expiry-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  let active = true;
  const store = createOAuthStore(path.join(directory, 'oauth.json'), 'secret', () => active);
  const adapter = new store.Adapter('AuthorizationCode');
  await adapter.upsert('expired', { accountId: 'alice' }, -1);
  assert.equal(await adapter.find('expired'), undefined);
  store.set('OpenGymGrant', 'g1', { userId: 'alice' });
  await adapter.upsert('code', { grantId: 'g1', uid: 'interaction' }, 600);
  await adapter.consume('code');
  assert.ok((await adapter.find('code')).consumed > 0);
  assert.equal((await adapter.findByUid('interaction')).grantId, 'g1');
  active = false;
  assert.equal(await adapter.find('code'), undefined);
});

test('OAuth storage write failure never acknowledges an unpersisted grant or revocation', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-failure-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'oauth.json');
  const store = createOAuthStore(file, 'secret');
  store.set('OpenGymGrant', 'g1', { grantId: 'g1', userId: 'alice' });
  // A directory in the atomic writer's temporary-file slot fails on Linux and Windows.
  fs.mkdirSync(file + '.tmp');
  assert.throws(() => store.set('OpenGymGrant', 'g2', { grantId: 'g2', userId: 'bob' }));
  assert.equal(store.get('OpenGymGrant', 'g2'), undefined);
  assert.throws(() => store.revoke('g1'));
  assert.equal(store.get('OpenGymGrant', 'g1').userId, 'alice');
  assert.deepEqual(createOAuthStore(file, 'secret').list('OpenGymGrant'), [store.get('OpenGymGrant', 'g1')]);
});

test('OAuth storage tolerates transient rename locks but bounds retries and fails closed', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-oauth-lock-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'oauth.json');
  const store = createOAuthStore(file, 'secret');
  const rename = fs.renameSync;
  let attempts = 0;
  let locked = false;
  t.mock.method(fs, 'renameSync', (source, destination) => {
    attempts++;
    if (locked || attempts <= 2) throw Object.assign(new Error('temporary lock'), { syscall: 'rename', code: 'EPERM' });
    return rename(source, destination);
  });
  store.set('OpenGymGrant', 'g1', { grantId: 'g1', userId: 'alice' });
  assert.equal(attempts, 3);
  assert.equal(createOAuthStore(file, 'secret').get('OpenGymGrant', 'g1').userId, 'alice');
  locked = true;
  attempts = 0;
  assert.throws(() => store.revoke('g1'), { code: 'EPERM' });
  assert.equal(attempts, 5);
  assert.equal(store.get('OpenGymGrant', 'g1').userId, 'alice');
  assert.equal(createOAuthStore(file, 'secret').get('OpenGymGrant', 'g1').userId, 'alice');
});
