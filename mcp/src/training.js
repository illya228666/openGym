import crypto from 'node:crypto'
import { z } from 'zod'
import { CATALOGUE, canonicalExId, exOr, registerCustom, searchExercises } from '../../frontend/src/lib/exercises.js'
import { workoutVolume, workoutDuration, modeOf, NOTE_MAX } from '../../frontend/src/lib/history.js'
import { buildCompletedWorkout } from '../../frontend/src/lib/finish-workout.js'
import { insertChronological } from '../../frontend/src/lib/backfill.js'
import { editCompletedSession, saveWorkoutEdit, deleteEditedWorkout } from '../../frontend/src/lib/session-edit.js'
import { legacySyncKey, rebuildPrHistory, MAX_DURATION_MIN } from '../../frontend/src/lib/workout-date.js'
import { hasCompletedWork, isSideSet, syncSideAggregate, modeForEntry, modeForSet } from '../../frontend/src/lib/workout-model.js'
import { exerciseMuscleSnapshot } from '../../frontend/src/lib/muscles.js'
import { isBellEx, bellsIn, withMeaning } from '../../frontend/src/lib/dumbbells.js'

const MAX_ENTRIES = 100
const MAX_SETS = 100
// A date-only record may already be today at UTC+14 even while the server is still yesterday.
const MAX_UTC_OFFSET_MS = 14 * 60 * 60 * 1000
const keyOf = w => w.id ?? legacySyncKey(w)
const fail = (code, message, status = 400, details = {}) => { throw Object.assign(new Error(message), { code, status, ...details }) }
const canonical = value => Array.isArray(value) ? value.map(canonical)
  : value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])])) : value
const hash = value => crypto.createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex')
// Stamps and receipts are server bookkeeping, not changes to the exercise data being edited.
export const workoutVersion = (w, unit) => hash({ unit, workout: Object.fromEntries(
  Object.entries(w).filter(([k]) => !['_mcp', '_ts', '_f', '_u'].includes(k))) })
export const workoutKey = keyOf

const date = z.string().regex(/^\d{4}-\d{2}-\d{2}$/).refine(value => {
  const parsed = new Date(value + 'T12:00:00Z')
  return Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value
}, 'Invalid calendar date')
const number = z.number().finite().nonnegative().max(100000)
const nullableNumber = number.nullable().optional()
const side = z.object({ w: nullableNumber, r: nullableNumber, sec: nullableNumber, min: nullableNumber,
  km: nullableNumber, dist: nullableNumber, mode: z.enum(['reps', 'time', 'cardio']).optional(),
  note: z.string().max(NOTE_MAX).nullable().optional(),
  speed: nullableNumber, done: z.boolean().optional(), rir: z.number().min(0).max(10).nullable().optional(),
  rpe: z.number().min(0).max(10).nullable().optional(), failure: z.boolean().optional(),
  type: z.enum(['straight', 'dropset', 'restpause']).optional(),
  drops: z.array(z.object({ w: number, r: number }).strict()).max(MAX_SETS).optional(),
  clusters: z.array(z.object({ r: number, restSec: number.optional() }).strict()).max(MAX_SETS).optional()
}).strict()
const set = side.extend({ set_id: z.string().min(1).optional(), phase: z.enum(['work', 'warmup']).optional(),
  warmup: z.boolean().optional(), at: z.number().finite().nonnegative().optional(),
  sides: z.object({ L: side, R: side }).strict().optional() }).strict()
const entry = z.object({ entry_id: z.string().min(1).optional(), exercise_id: z.string().min(1).optional(),
  mode: z.enum(['reps', 'time', 'cardio']).optional(), db_load: z.enum(['as', 'each', 'total']).optional(),
  note: z.string().max(NOTE_MAX).nullable().optional(),
  sets: z.array(set).max(MAX_SETS).optional() }).strict()
const fields = {
  date: date.optional(), name: z.string().trim().min(1).max(200).optional(),
  note: z.string().max(NOTE_MAX).nullable().optional(), bodyweight: nullableNumber,
  start_time: z.string().datetime({ offset: true }).nullable().optional(),
  duration_minutes: z.number().finite().min(1).max(MAX_DURATION_MIN).nullable().optional()
}
export const trainingSchemas = {
  create: { workout_id: z.uuid(), unit: z.enum(['kg', 'lb']), ...fields, date,
    entries: z.array(entry).min(1).max(MAX_ENTRIES) },
  update: { workout_id: z.string().min(1), expected_version: z.string().min(1), operation_id: z.uuid(),
    unit: z.enum(['kg', 'lb']), ...fields, entries: z.array(entry).max(MAX_ENTRIES).optional(),
    remove_entry_ids: z.array(z.string().min(1)).max(MAX_ENTRIES).optional(),
    remove_set_ids: z.array(z.string().min(1)).max(MAX_ENTRIES * MAX_SETS).optional() },
  delete: { workout_id: z.string().min(1), expected_version: z.string().min(1),
    operation_id: z.uuid(), confirmed: z.literal(true), unit: z.enum(['kg', 'lb']) }
}

// The editable projection is allowlisted: media, server stamps and unrelated profile data never
// become model-visible. Legacy references are valid only with the version returned beside them.
export function editableWorkout(w, S) {
  const unit = S.unit || 'kg'
  const duration = workoutDuration(w)
  return { workout_id: keyOf(w), version: workoutVersion(w, unit), unit, date: w.d,
    name: w.name || null, note: w.note || null, bodyweight: w.bw ?? null,
    start_time: Number.isFinite(w.start) ? new Date(w.start).toISOString() : null,
    duration_minutes: duration > 0 ? duration / 60000 : null,
    entries: (w.entries || []).map((e, i) => ({ entry_id: e.entryId || `legacy-entry-${i}`,
      exercise_id: e.id, mode: modeForEntry(e, modeOf({ ...e.target, id: e.id })), note: e.note || null,
      ...(e.target?.dbLoad ? { db_load: e.target.dbLoad } : {}),
      sets: (e.sets || []).map((s, j) => ({ set_id: s.setId || `legacy-set-${i}-${j}`, ...publicSet(s) })) })) }
}
function publicSet(s) {
  const out = {}
  for (const k of ['w', 'r', 'sec', 'min', 'speed', 'km', 'dist', 'mode', 'unit', 'note', 'done', 'rir', 'rpe', 'failure', 'type', 'phase', 'warmup', 'at'])
    if (s[k] !== undefined) out[k] = structuredClone(s[k])
  if (Array.isArray(s.drops)) out.drops = s.drops.map(({ w, r }) => ({ w, r }))
  if (Array.isArray(s.clusters)) out.clusters = s.clusters.map(({ r, restSec }) => ({ r, ...(restSec !== undefined ? { restSec } : {}) }))
  if (isSideSet(s)) out.sides = { L: publicSet(s.sides.L), R: publicSet(s.sides.R) }
  return out
}
function identify(w) {
  w.entries.forEach((e, i) => {
    e.entryId ||= `legacy-entry-${i}`
    e.sets.forEach((s, j) => { s.setId ||= `legacy-set-${i}-${j}` })
  })
}
function patchSet(existing, patch, complete) {
  const { set_id, sides, ...values } = patch
  const next = { ...existing, ...values }
  for (const k of Object.keys(values)) if (values[k] === null) delete next[k]
  if (sides) next.sides = { L: patchSet(existing.sides?.L || {}, sides.L, complete),
    R: patchSet(existing.sides?.R || {}, sides.R, complete) }
  if (complete && !('done' in next)) next.done = true
  return isSideSet(next) ? { ...syncSideAggregate(next), sides: next.sides } : next
}
function patchEntries(w, input, S) {
  const own = new Map((S.customEx || []).map(e => [e.id, e]))
  const changedRows = new Set()
  for (const patch of input.entries || []) {
    let e = patch.entry_id ? w.entries.find(e => e.entryId === patch.entry_id) : null
    if (patch.entry_id && !e) fail('not_found', 'Exercise occurrence not found', 404)
    if (!e) {
      if (!patch.exercise_id || !patch.sets?.length) fail('invalid_training', 'A new exercise needs exercise_id and sets')
      e = { id: canonicalExId(patch.exercise_id), entryId: crypto.randomUUID(), sets: [] }
      e.target = withMeaning(S, { id: e.id })
      w.entries.push(e)
    }
    const id = patch.exercise_id ? canonicalExId(patch.exercise_id) : e.id
    if (patch.exercise_id && id !== e.id) {
      e.id = id
      delete e.muscleSnapshot
      delete e.target
      delete e.planned
    }
    if (patch.exercise_id) {
      const ex = own.get(id) || exOr(id)
      if (ex.missing) fail('unknown_exercise', 'Search the catalogue or existing custom exercises first')
    }
    e.target = { ...(e.target || {}), id: e.id,
      ...(patch.mode ? { mode: patch.mode } : {}), ...(patch.db_load ? { dbLoad: patch.db_load } : {}) }
    if (!patch.entry_id && isBellEx(e.id) && bellsIn(e.target) === 2 && !e.target.dbLoad)
      fail('ambiguous_weight', 'Specify db_load: each or total for two dumbbells/kettlebells, or explicitly as entered')
    if (patch.note !== undefined) { if (patch.note === null) delete e.note; else e.note = patch.note }
    for (const row of patch.sets || []) {
      const index = row.set_id ? e.sets.findIndex(s => s.setId === row.set_id) : -1
      if (row.set_id && index < 0) fail('not_found', 'Set not found in this exercise occurrence', 404)
      const previous = index < 0 ? { setId: crypto.randomUUID() } : e.sets[index]
      const updated = patchSet(previous, row, index < 0)
      changedRows.add(updated)
      if (index < 0) e.sets.push(updated); else e.sets[index] = updated
    }
  }
  for (const id of input.remove_entry_ids || []) {
    if (!w.entries.some(e => e.entryId === id)) fail('not_found', 'Exercise occurrence not found', 404)
    w.entries = w.entries.filter(e => e.entryId !== id)
  }
  for (const id of input.remove_set_ids || []) {
    const e = w.entries.find(e => e.sets.some(s => s.setId === id))
    if (!e) fail('not_found', 'Set not found', 404)
    e.sets = e.sets.filter(s => s.setId !== id)
  }
  if (w.entries.length > MAX_ENTRIES || w.entries.some(e => e.sets.length > MAX_SETS)) fail('invalid_training', 'Workout is too large')
  for (const e of w.entries) for (const s of e.sets) {
    // A note/date correction must not reject or rewrite an unchanged legacy row.
    if (!changedRows.has(s)) continue
    const rows = isSideSet(s) ? [s.sides.L, s.sides.R] : [s]
    for (const row of rows) if (row.done) {
      const mode = modeForSet(row, { ...e.target, mode: modeOf({ ...e.target, id: e.id }) })
      if (mode === 'reps' && !(Number.isFinite(row.r) && row.r > 0 && Number.isFinite(row.w)))
        fail('invalid_training', 'Completed rep sets require explicit weight (0 for bodyweight) and reps')
      if (mode === 'time' && !(row.sec > 0)) fail('invalid_training', 'Completed timed sets require seconds')
      if (mode === 'cardio' && !(row.min > 0)) fail('invalid_training', 'Completed cardio sets require minutes')
    }
  }
  if (!w.entries.some(e => e.sets.some(hasCompletedWork))) fail('empty_workout', 'A completed workout needs completed work; delete explicitly instead')
}
function patchTime(w, input, now) {
  const previousDate = w.d
  const duration = input.duration_minutes === undefined ? (workoutDuration(w) || null) : input.duration_minutes === null ? null : input.duration_minutes * 60000
  if (input.date !== undefined) w.d = input.date
  if (input.start_time !== undefined) {
    if (input.start_time === null) delete w.start
    else {
      const start = Date.parse(input.start_time)
      if (start > now || input.start_time.slice(0, 10) !== w.d) fail('invalid_time', 'Start must be in the past and match the workout date')
      w.start = start
    }
  } else if (input.date !== undefined && Number.isFinite(w.start)) {
    // Move by calendar days, including starts whose original offset put them on another UTC day.
    // Callers wanting a different local clock/offset supply start_time explicitly.
    w.start += Date.parse(w.d + 'T12:00:00Z') - Date.parse(previousDate + 'T12:00:00Z')
  }
  if (Number.isFinite(w.start) && w.start > now) fail('invalid_time', 'Completed workout starts in the future')
  if (duration != null && Number.isFinite(w.start)) {
    if (w.start + duration > now) fail('invalid_time', 'Completed workout ends in the future')
    w.end = w.start + duration
    delete w.durationMs
  } else {
    delete w.end
    if (duration != null) w.durationMs = duration; else delete w.durationMs
  }
  if (w.d > new Date(now + MAX_UTC_OFFSET_MS).toISOString().slice(0, 10)) fail('invalid_time', 'Completed workout date is in the future')
}

/** Mutates only a private copy. The caller compares/commits synchronously through the sync writer. */
export function mutateTraining(S, action, raw, now = Date.now()) {
  const input = z.object(trainingSchemas[action]).strict().parse(raw)
  const unit = S.unit || 'kg'
  if (input.unit !== unit) fail('unit_conflict', 'Read the current profile unit before writing', 409)
  const fingerprint = hash(input)
  S.workouts ||= []
  const current = S.workouts.find(w => keyOf(w) === input.workout_id)
  if (action === 'create') {
    if (current) {
      if (current._mcp?.creationHash !== fingerprint) fail('id_conflict', 'Workout ID already used for a different request', 409)
      return { changed: false, workout_id: input.workout_id }
    }
    if (S.deleted?.workouts?.[input.workout_id] || S.resetIds?.workouts?.includes(input.workout_id))
      fail('workout_deleted', 'This workout was deleted; do not retry with a new ID', 409)
  } else {
    if (!current && action === 'delete' && S.deleted?.workouts?.[input.workout_id])
      return { changed: false, workout_id: input.workout_id, deleted: true }
    if (!current) fail('not_found', 'Workout not found; it may have been deleted', 404)
    const version = workoutVersion(current, unit)
    if (current._mcp?.lastOperation?.id === input.operation_id) {
      const op = current._mcp.lastOperation
      if (op.hash === fingerprint && op.after === version) return { changed: false, workout_id: input.workout_id }
      fail('operation_conflict', 'Operation ID reused or workout changed; read it again', 409, { currentVersion: version })
    }
    if (version !== input.expected_version) fail('version_conflict', 'Workout changed; read get_workout again before editing', 409, { currentVersion: version })
  }
  const savedActive = S.active
  delete S.active
  // registerCustom is synchronous and always undone; no profile index survives this operation.
  registerCustom(S.customEx || [])
  try {
    if (action === 'delete') {
      editCompletedSession(S, keyOf(current))
      deleteEditedWorkout(S)
      // stampPut records the deletion with a causal stamp during the atomic profile commit.
      return { changed: true, workout_id: input.workout_id, deleted: true }
    }
    let draft
    if (current) { editCompletedSession(S, keyOf(current)); draft = S.active; identify(draft) }
    else draft = { id: input.workout_id, d: input.date, name: input.name || 'Workout', entries: [], prs: [] }
    for (const [field, key] of [['name', 'name'], ['note', 'note'], ['bodyweight', 'bw']]) if (input[field] !== undefined) {
      if (input[field] === null) delete draft[key]; else draft[key] = input[field]
    }
    patchEntries(draft, input, S)
    let record
    if (current) {
      record = saveWorkoutEdit(S, now)
      identify(record)
    } else {
      record = buildCompletedWorkout(draft, { end: undefined, prs: [], snapshotFor: e => exerciseMuscleSnapshot(exOr(e.id)) })
      // buildCompletedWorkout intentionally whitelists completed-entry fields; retain occurrence IDs.
      const logged = draft.entries.filter(e => e.sets.some(hasCompletedWork))
      record.entries.forEach((e, i) => { e.entryId = logged[i].entryId })
      delete record.start; delete record.end
      S.workouts = insertChronological(S.workouts, record)
    }
    patchTime(record, input, now)
    record._ts = Math.max(now, Number(current?._ts) || 0) + 1
    record.vol = workoutVolume(record)
    const touched = [...new Set([...(current?.entries || []), ...record.entries].map(e => e.id))]
    S.workouts = insertChronological(S.workouts.filter(w => keyOf(w) !== keyOf(record)), record)
    S.workouts = rebuildPrHistory(S.workouts, touched, current ? record : null)
    record = S.workouts.find(w => keyOf(w) === input.workout_id)
    record._mcp = { ...current?._mcp, ...(action === 'create' ? { creationHash: fingerprint } : {}) }
    if (action === 'update') record._mcp.lastOperation = { id: input.operation_id, hash: fingerprint, after: workoutVersion(record, unit) }
    return { changed: true, workout_id: input.workout_id }
  } finally {
    registerCustom([])
    if (savedActive === undefined) delete S.active; else S.active = savedActive
  }
}

export const searchExerciseTool = {
  name: 'search_exercises', scope: 'training.read',
  description: 'Search the English exercise catalogue and this user’s existing custom exercises. Translate a natural-language query to English if needed. Returns IDs, mode, body part and equipment, never media. Does not create exercises.',
  schema: { query: z.string().trim().min(1).max(200), limit: z.number().int().min(1).max(50).default(15) },
  handler: ({ query, limit = 15 }, S) => {
    const custom = S?.customEx || []
    const ownIds = new Set(custom.map(e => e.id))
    const matches = searchExercises([...CATALOGUE.filter(e => !ownIds.has(e.id)), ...custom], query)
    return { total: matches.length, exercises: matches.slice(0, limit).map(e => ({ id: e.id, name: e.n,
      body_part: e.bp || null, equipment: e.eq || null, mode: modeOf({ id: e.id }), custom: ownIds.has(e.id) })) }
  }
}

export function createTrainingWriteTools(write) {
  return ['create', 'update', 'delete'].map(action => ({ name: action + '_workout', scope: 'training.write', write: true,
    destructive: action === 'delete', idempotent: true, schema: trainingSchemas[action],
    description: action === 'create' ? 'Save an already completed workout, not an active session. Use existing exercise IDs, stable workout UUID and explicit unit. Missing start/duration stay unknown. Repeat the identical payload and UUID after a lost response.'
      : action === 'update' ? 'Patch a completed workout after get_workout. Use editing.version and entry_id/set_id references; omitted fields are preserved. Entries/sets without IDs are appended; remove only explicit remove_entry_ids/remove_set_ids. Repeat operation_id and identical payload on retry. On conflict read again, never blindly overwrite.'
        : 'Delete the workout read with get_workout, only after the user confirms the exact target. Requires its version and confirmed:true. Never recreate a deleted workout on retry.',
    handler: input => write(action, input)
  }))
}
