import crypto from 'node:crypto';
import fs from 'node:fs';
import { atomicWrite } from './durable.js';
import { MaxDynamicClients } from './mcp-config.js';

const MaxRecords = 10000;
const RefreshReplayHistorySeconds = 30 * 86400;
const RenameAttempts = 5;
const RenameRetryMilliseconds = 25;
// shortcut: one Node writer; use a transactional shared adapter before scaling API replicas.

/** Single Node writer. Persist OAuth artifacts separately, encrypted with a domain-separated key.
 * Loss/corruption fails closed; do not recreate this file after a failed restore. */
export function createOAuthStore(file, secret, active = () => true) {
  const key = crypto.createHmac('sha256', secret).update('opengym-oauth-store-v1').digest();
  let rows = new Map();
  try {
    const document = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (document.version !== 1) throw new Error('unsupported OAuth store');
    const decipher = crypto.createDecipheriv('aes-256-gcm', key, Buffer.from(document.iv, 'base64'));
    decipher.setAuthTag(Buffer.from(document.tag, 'base64'));
    const entries = JSON.parse(Buffer.concat([decipher.update(Buffer.from(document.data, 'base64')), decipher.final()]).toString('utf8'));
    if (!Array.isArray(entries) || entries.length > MaxRecords || entries.some(row =>
      !Array.isArray(row) || typeof row[0] !== 'string' || !row[1]?.payload
      || (row[1].expiresAt !== null && !Number.isFinite(row[1].expiresAt))))
      throw new Error('invalid OAuth records');
    rows = new Map(entries);
  } catch (error) {
    if (error.code !== 'ENOENT') throw new Error('OAuth store unreadable; restore it with the matching data/secret', { cause: error });
  }
  const index = (model, id) => `${model}:${crypto.createHash('sha256').update(String(id)).digest('hex')}`;
  let durableRows = new Map(rows);
  const alive = row => !!row && (row.expiresAt === null || row.expiresAt > Date.now());
  const sweep = () => { for (const [id, row] of rows) if (!alive(row)) rows.delete(id); };
  const save = () => {
    sweep();
    const iv = crypto.randomBytes(12);
    const cipher = crypto.createCipheriv('aes-256-gcm', key, iv);
    const data = Buffer.concat([cipher.update(JSON.stringify([...rows])), cipher.final()]);
    try {
      const document = JSON.stringify({ version: 1, iv: iv.toString('base64'), tag: cipher.getAuthTag().toString('base64'), data: data.toString('base64') });
      for (let attempt = 0; attempt < RenameAttempts; attempt++) {
        try { atomicWrite(file, document, 0o600); break; }
        catch (error) {
          if (error.syscall !== 'rename' || !['EPERM', 'EBUSY'].includes(error.code) || attempt === RenameAttempts - 1) throw error;
          // Windows scanners can briefly lock the destination. Stay synchronous to preserve single-writer ordering.
          Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, RenameRetryMilliseconds);
        }
      }
      durableRows = new Map(rows);
    } catch (error) {
      rows = new Map(durableRows);
      throw error;
    }
  };
  const get = (model, id) => {
    const row = rows.get(index(model, id));
    return alive(row) ? structuredClone(row.payload) : undefined;
  };
  const set = (model, id, payload, seconds = 30 * 86400) => {
    sweep();
    if (rows.size >= MaxRecords && !rows.has(index(model, id))) throw new Error('OAuth storage capacity exceeded');
    rows.set(index(model, id), { payload: structuredClone(payload), expiresAt: seconds === null ? null : Date.now() + seconds * 1000 });
    save();
  };
  const remove = (model, id) => { rows.delete(index(model, id)); save(); };
  const revoke = grantId => {
    for (const [id, row] of rows) if (row.payload.grantId === grantId) rows.delete(id);
    rows.delete(index('Grant', grantId));
    rows.delete(index('OpenGymGrant', grantId));
    save();
  };
  class Adapter {
    constructor(model) { this.model = model; }
    // Registered clients must outlive tokens and grants, including API restarts.
    async upsert(id, payload, expiresIn) {
      if (this.model === 'Client' && !get('Client', id) && [...rows.keys()].filter(key => key.startsWith('Client:')).length >= MaxDynamicClients)
        throw new Error('OAuth client capacity exceeded');
      // The adapter owns connection lifetime; do not persist provider-generated grant/refresh expiry.
      if (this.model === 'Grant' || this.model === 'RefreshToken') {
        payload = { ...payload };
        delete payload.exp;
        expiresIn = payload.consumed ? RefreshReplayHistorySeconds : null;
      }
      set(this.model, id, payload, this.model === 'Client' ? null : expiresIn);
    }
    async find(id) {
      const payload = get(this.model, id);
      const grantId = this.model === 'Grant' ? id : payload?.grantId;
      const binding = grantId && get('OpenGymGrant', grantId);
      return binding && !active(binding) ? undefined : payload;
    }
    async findByUid(uid) { return this.findBy('uid', uid); }
    async findByUserCode(code) { return this.findBy('userCode', code); }
    async findBy(field, value) {
      for (const [id, row] of rows) if (id.startsWith(this.model + ':') && alive(row) && row.payload[field] === value)
        return structuredClone(row.payload);
    }
    async consume(id) {
      const payload = get(this.model, id);
      if (payload) set(this.model, id, { ...payload, consumed: Math.floor(Date.now() / 1000) },
        this.model === 'RefreshToken' ? RefreshReplayHistorySeconds
          : Math.max(1, (rows.get(index(this.model, id)).expiresAt - Date.now()) / 1000));
    }
    async destroy(id) { this.model === 'Grant' ? revoke(id) : remove(this.model, id); }
    async revokeByGrantId(id) { revoke(id); }
  }
  // Upgrade only live, still-authorized legacy grants; never resurrect expired or session-revoked access.
  let migrated = false;
  for (const [id, row] of rows) {
    if (!id.startsWith('OpenGymGrant:') || !alive(row) || row.payload.persistent || !active(row.payload)) continue;
    const grantId = row.payload.grantId;
    const grant = get('Grant', grantId);
    if (!grant || grant.exp <= Math.floor(Date.now() / 1000)) continue;
    rows.set(id, { payload: { ...row.payload, persistent: true }, expiresAt: null });
    for (const [artifactId, artifact] of rows) {
      if (!alive(artifact) || artifact.payload.consumed || artifact.payload.exp <= Math.floor(Date.now() / 1000)) continue;
      if (artifactId !== index('Grant', grantId)
        && !(artifactId.startsWith('RefreshToken:') && artifact.payload.grantId === grantId)) continue;
      const payload = { ...artifact.payload };
      delete payload.exp;
      rows.set(artifactId, { payload, expiresAt: null });
    }
    migrated = true;
  }
  if (migrated) save();
  return { Adapter, get, set, remove, revoke, list: model => [...rows]
    .filter(([id, row]) => id.startsWith(model + ':') && alive(row))
    .map(([, row]) => structuredClone(row.payload)) };
}
