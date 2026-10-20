import { test, expect } from 'vitest'
import crypto from 'node:crypto'
import { mutateTraining, editableWorkout, searchExerciseTool } from '../src/training.js'
import { stampPut } from '../../api/sync-stamps.js'
import { stampChange } from '../../frontend/src/lib/sync-merge.js'
import { createTrainingTools } from '../src/tools.js'

const id = 'a164d2ef-8b28-4db5-93ac-f06b721ef935'
const state = () => ({ unit: 'kg', workouts: [], customEx: [], exWeights: {}, routines: [] })
const create = () => ({ workout_id: id, unit: 'kg', date: '2026-10-19',
  entries: [{ exercise_id: '0025', sets: [{ w: 60, r: 8 }, { w: 60, r: 8 }] }] })
const view = S => editableWorkout(S.workouts[0], S)
const edit = (S, patch) => ({ workout_id: id, unit: 'kg', expected_version: view(S).version,
  operation_id: crypto.randomUUID(), ...patch })
const commit = (S, action, input) => {
  const next = structuredClone(S)
  const result = mutateTraining(next, action, input)
  if (result.changed) {
    next._ts = stampChange(S, next)
    stampPut(S, next, { overRead: true, stamped: true })
  }
  return { next, result }
}

test('create and retry preserve unknown clocks and save exactly one workout', () => {
  const first = commit(state(), 'create', create())
  const S = first.next
  expect(S.workouts).toHaveLength(1)
  expect(S.workouts[0].vol).toBe(960)
  expect(S.workouts[0]).not.toHaveProperty('start')
  expect(S.workouts[0]).not.toHaveProperty('end')
  expect(view(S)).toMatchObject({ start_time: null, duration_minutes: null })
  expect(commit(S, 'create', create()).result.changed).toBe(false)
  expect(() => commit(S, 'create', { ...create(), note: 'different payload' })).toThrow('different request')
  const read = createTrainingTools({ getState: () => S }).find(t => t.name === 'get_workout').handler({ workout_id: id })
  expect(read.editing.version).toBe(view(S).version)
  expect(read.volume).toBe(960)
})

test('patch and retry append once and reject stale edits including changed profile units', () => {
  let S = commit(state(), 'create', create()).next
  const request = edit(S, { entries: [{ entry_id: view(S).entries[0].entry_id, sets: [{ w: 62.5, r: 6 }] }] })
  S = commit(S, 'update', request).next
  expect(S.workouts[0].entries[0].sets).toHaveLength(3)
  expect(S.workouts[0].vol).toBe(1335)
  expect(commit(S, 'update', request).result.changed).toBe(false)
  expect(() => commit(S, 'update', { ...request, operation_id: crypto.randomUUID() })).toThrow('changed')
  expect(() => commit({ ...S, unit: 'lb' }, 'update', edit(S, { note: 'wrong unit' }))).toThrow('unit')
  S = commit(S, 'update', edit(S, { note: 'phone edit follows' })).next
  expect(() => commit(S, 'update', request)).toThrow('changed')
})

test('occurrence and set IDs distinguish duplicate exercises and preserve complex metadata', () => {
  let S = state()
  S.workouts = [{ id, d: '2026-10-19', note: 'Keep', media: [{ hash: 'private-media' }],
    entries: [
      { id: '0025', sg: 'pair', sets: [{ w: 40, r: 5, done: true, failure: true,
        type: 'dropset', drops: [{ w: 30, r: 4 }] }] },
      { id: '0025', sg: 'pair', sets: [{ w: 50, r: 6, done: true }] }
    ] }]
  const before = view(S)
  expect(JSON.stringify(before)).not.toContain('private-media')
  expect(S.workouts[0].entries[0]).not.toHaveProperty('entryId')
  S = commit(S, 'update', edit(S, { entries: [{ entry_id: before.entries[1].entry_id,
    sets: [{ set_id: before.entries[1].sets[0].set_id, w: 55 }] }] })).next
  expect(S.workouts[0].entries[0].sets[0]).toMatchObject({ w: 40, failure: true, type: 'dropset', drops: [{ w: 30, r: 4 }] })
  expect(S.workouts[0].entries[1].sets[0].w).toBe(55)
  expect(S.workouts[0].media).toEqual([{ hash: 'private-media' }])
  expect(view(S).entries.map(e => e.entry_id)).toEqual(before.entries.map(e => e.entry_id))
  expect(S.workouts[0].note).toBe('Keep')
})

test('duration can be supplied without inventing a start and clocks can be filled later', () => {
  let S = commit(state(), 'create', create()).next
  S = commit(S, 'update', edit(S, { duration_minutes: 42 })).next
  expect(S.workouts[0]).toHaveProperty('durationMs', 42 * 60000)
  expect(S.workouts[0]).not.toHaveProperty('start')
  S = commit(S, 'update', edit(S, { start_time: '2026-10-19T18:30:00+02:00' })).next
  expect(S.workouts[0].end - S.workouts[0].start).toBe(42 * 60000)
  expect(S.workouts[0]).not.toHaveProperty('durationMs')
  S = commit(S, 'update', edit(S, { start_time: null, duration_minutes: null })).next
  expect(view(S)).toMatchObject({ start_time: null, duration_minutes: null })
})

test('deletion is confirmed and tombstones prevent retry resurrection', () => {
  let S = commit(state(), 'create', create()).next
  expect(() => commit(S, 'delete', edit(S, {}))).toThrow()
  const request = edit(S, { confirmed: true })
  S = commit(S, 'delete', request).next
  expect(S.workouts).toEqual([])
  expect(S.deleted.workouts[id]).toBeGreaterThan(0)
  expect(commit(S, 'delete', request).result).toMatchObject({ deleted: true, changed: false })
  expect(() => commit(S, 'create', create())).toThrow('deleted')
})

test('search and mutations isolate existing custom exercises by profile', () => {
  const alice = state(), bob = state()
  alice.customEx = [{ id: 'custom', n: 'Private sled', bp: 'upper legs', eq: 'sled', tg: 'quads' }]
  expect(searchExerciseTool.handler({ query: 'Private sled' }, alice).exercises[0]).toMatchObject({ id: 'custom', name: 'Private sled' })
  expect(searchExerciseTool.handler({ query: 'Private sled' }, bob).exercises).toEqual([])
  const request = { ...create(), entries: [{ exercise_id: 'custom', sets: [{ w: 40, r: 8 }] }] }
  expect(commit(alice, 'create', request).next.workouts[0].vol).toBe(320)
  expect(() => commit(bob, 'create', request)).toThrow('catalogue')
  expect(alice.workouts).toEqual([])
  expect(bob.workouts).toEqual([])
})

test('per-side drop and rest-pause volumes use shared workout semantics', () => {
  const request = { ...create(), entries: [{ exercise_id: '0025', sets: [{ sides: {
    L: { w: 20, r: 5, type: 'dropset', drops: [{ w: 10, r: 3 }] },
    R: { w: 25, r: 8, type: 'restpause', clusters: [{ r: 3, restSec: 15 }] }
  } }] }] }
  const S = commit(state(), 'create', request).next
  expect(S.workouts[0].vol).toBe(330)
  expect(view(S).entries[0].sets[0].sides.R.clusters).toEqual([{ r: 3, restSec: 15 }])
})

test('invalid dates, unknown exercises and arbitrary profile fields never save', () => {
  for (const patch of [{ date: '2026-02-30' }, { date: '2099-01-01' }, { active: {} },
    { entries: [{ exercise_id: 'missing', sets: [{ w: 40, r: 5 }] }] },
    { entries: [{ exercise_id: '0025', sets: [{ r: 5 }] }] }]) {
    const S = state()
    expect(() => commit(S, 'create', { ...create(), ...patch })).toThrow()
    expect(S.workouts).toEqual([])
  }
})

test('timed and cardio work retain their mode while explicit removals address only selected sets', () => {
  let S = commit(state(), 'create', { ...create(), entries: [
    { exercise_id: '0025', mode: 'time', sets: [{ sec: 45 }] },
    { exercise_id: '0025', mode: 'cardio', sets: [{ min: 20, speed: 8, km: 2.5 }, { min: 5 }] }
  ] }).next
  const before = view(S)
  expect(before.entries.map(e => e.mode)).toEqual(['time', 'cardio'])
  S = commit(S, 'update', edit(S, { remove_set_ids: [before.entries[1].sets[1].set_id] })).next
  expect(view(S).entries[0].sets[0]).toMatchObject({ sec: 45, done: true })
  expect(view(S).entries[1].sets).toHaveLength(1)
  expect(view(S).entries[1].sets[0]).toMatchObject({ min: 20, speed: 8, km: 2.5 })
})
