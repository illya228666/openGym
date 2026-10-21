// Disposable CI/native stack only. This creates accounts/products and must never target production.
import assert from 'node:assert/strict'
import crypto from 'node:crypto'

const base = process.argv[2] || 'http://localhost:8080'
const target = new URL(base)
assert.ok(process.argv.includes('--disposable') && target.protocol === 'http:' &&
  ['localhost', '127.0.0.1'].includes(target.hostname), 'Explicit --disposable and a loopback HTTP stack are required')
const call = (path, options = {}) => fetch(base + path, { ...options, redirect: 'manual', signal: AbortSignal.timeout(15000) })
// Exercise media are baked into web, never fetched from a dataset at container startup.
for (const [path, mime] of [['still/0025.webp', 'image/webp'], ['clip/0025.mp4', 'video/mp4']]) {
  const response = await call('/exercise-media/' + path, { headers: { Range: 'bytes=0-0' } })
  assert.equal(response.status, 206)
  assert.match(response.headers.get('content-type'), new RegExp('^' + mime))
  assert.equal(response.headers.get('cross-origin-resource-policy'), 'same-origin')
  assert.match(response.headers.get('cache-control'), /immutable/)
  await response.body.cancel()
}
const notice = await call('/exercise-media/NOTICE.md')
assert.equal(notice.status, 200)
assert.match(await notice.text(), /Gym visual/)
// The disposable stack creates its administrator first (before Nutrition account smoke).
const adminName = 'MCP admin ' + crypto.randomUUID().slice(0, 8)
const password = 'MCP River stone garden 734!'
const registerAccount = async name => {
  const result = await call('/api/register/password', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: base },
    body: JSON.stringify({ name, password }) })
  assert.equal(result.status, 200)
  return result.headers.getSetCookie()[0].split(';')[0]
}
const adminCookie = await registerAccount(adminName)
assert.equal((await (await call('/api/me', { headers: { Cookie: adminCookie } })).json()).user.admin, true)
const toggle = async enabled => {
  const result = await call('/api/mcp/settings', { method: 'PUT', headers: { Cookie: adminCookie, Origin: base, 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled }) })
  assert.equal(result.status, 200)
  assert.equal((await result.json()).enabled, enabled)
}
assert.equal((await call('/mcp')).status, 404)
await toggle(true)
const initialChallenge = await call('/mcp')
assert.equal(initialChallenge.status, 401)
assert.match(initialChallenge.headers.get('www-authenticate'), /scope="training.read training.write nutrition.read nutrition.write"/)
const name = 'MCP CI ' + crypto.randomUUID().slice(0, 8)
let cookie = await registerAccount(name)
// Exercise existing OpenGym password sign-in, not a manufactured session.
const login = await call('/api/login/password', { method: 'POST', headers: { Origin: base, 'Content-Type': 'application/json' }, body: JSON.stringify({ name, password }) })
assert.equal(login.status, 200)
cookie = login.headers.getSetCookie()[0].split(';')[0]
const jar = new Map([cookie.split('=')])
const browser = async (path, options = {}) => {
  const response = await call(path, { ...options, headers: { ...options.headers, Cookie: [...jar].map(([key, value]) => `${key}=${value}`).join('; ') } })
  for (const item of response.headers.getSetCookie()) {
    const pair = item.split(';')[0]; const split = pair.indexOf('=')
    jar.set(pair.slice(0, split), pair.slice(split + 1))
  }
  return response
}
const resource = (await (await call('/.well-known/oauth-protected-resource')).json()).resource
const metadata = await (await call('/.well-known/oauth-authorization-server')).json()
assert.equal(metadata.client_id_metadata_document_supported, true)
const registration = await call(new URL(metadata.registration_endpoint).pathname, { method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ client_name: 'CI automatic MCP', redirect_uris: ['https://client.example/callback'], token_endpoint_auth_method: 'none' }) })
assert.equal(registration.status, 201)
const client = await registration.json()
assert.equal(client.client_secret, undefined)
async function authorize(scope) {
  const verifier = crypto.randomBytes(32).toString('base64url')
  let response = await browser(new URL(metadata.authorization_endpoint).pathname + '?' + new URLSearchParams({
    client_id: client.client_id, redirect_uri: 'https://client.example/callback', response_type: 'code', resource,
    scope, prompt: 'consent', state: 'ci-state',
    code_challenge: crypto.createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256'
  }))
  let code
  for (let step = 0; step < 12; step++) {
    const location = response.headers.get('location')
    if (location?.startsWith('https://client.example/callback')) {
      const callback = new URL(location)
      assert.equal(callback.searchParams.get('error'), null)
      assert.equal(callback.searchParams.get('state'), 'ci-state')
      code = callback.searchParams.get('code'); break
    }
    if (location) {
      const next = new URL(location, base)
      assert.equal(next.origin, target.origin)
      response = await browser(next.pathname + next.search); continue
    }
    const html = await response.text()
    assert.equal(response.status, 200)
    // The Node consent policy is forwarded through nginx, which adds its own
    // frame-ancestors policy. Fetch combines both CSP headers with a comma.
    const consentPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self' https://client.example; frame-ancestors 'none'"
    const receivedPolicy = response.headers.get('content-security-policy')
    assert.ok(
      receivedPolicy === consentPolicy || receivedPolicy === consentPolicy + ", frame-ancestors 'none'",
      'Unexpected consent Content-Security-Policy: ' + receivedPolicy
    )
    const csrf = /name="csrf" value="([^"]+)"/.exec(html)?.[1]
    assert.ok(csrf)
    response = await browser(new URL(response.url).pathname, { method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', Origin: base }, body: new URLSearchParams({ csrf, decision: 'allow' }) })
  }
  assert.ok(code)
  const exchange = await call(new URL(metadata.token_endpoint).pathname, { method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({
      client_id: client.client_id, grant_type: 'authorization_code', code, code_verifier: verifier,
      redirect_uri: 'https://client.example/callback', resource
    }) })
  assert.equal(exchange.status, 200)
  return (await exchange.json()).access_token
}
const token = await authorize('training.read training.write nutrition.read nutrition.write offline_access')
const rpc = async (method, params, expected = 200, accessToken = token) => {
  const result = await call('/mcp', { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', Authorization: 'Bearer ' + accessToken },
    body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) })
  assert.equal(result.status, expected)
  const text = await result.text()
  return JSON.parse(result.headers.get('content-type')?.startsWith('text/event-stream') ? /^data: (.+)$/m.exec(text)[1] : text)
}
const tool = async (name, args, error) => {
  const envelope = await rpc('tools/call', { name, arguments: args })
  assert.equal(envelope.result.isError || false, !!error, JSON.stringify(envelope))
  if (error) assert.equal(envelope.result.structuredContent.code, error)
  return envelope.result.structuredContent
}
assert.equal((await rpc('tools/list', {})).result.tools.length, 31)
await toggle(false)
await rpc('tools/list', {}, 404)
assert.equal((await call('/oauth/token')).status, 404)
assert.equal((await call('/api/me', { headers: { Cookie: cookie } })).status, 200)
await toggle(true)
assert.equal((await rpc('tools/list', {})).result.tools.length, 31)
// One completed-workout lifecycle through real OAuth, shared calculations and durable sync.
const workoutArgs = { workout_id: crypto.randomUUID(), unit: 'kg', date: '2026-10-19',
  entries: [{ exercise_id: '0025', sets: [{ w: 60, r: 8 }, { w: 60, r: 8 }] }] }
const workout = await tool('create_workout', workoutArgs)
assert.equal(workout.start_time, null)
assert.equal(workout.duration_minutes, null)
assert.deepEqual(await tool('create_workout', workoutArgs), workout)
let logged = await tool('get_workout', { workout_id: workout.workout_id })
assert.equal(logged.volume, 960)
const workoutEdit = { workout_id: workout.workout_id, unit: 'kg', expected_version: logged.editing.version,
  operation_id: crypto.randomUUID(), note: 'MCP workout smoke', duration_minutes: 42,
  entries: [{ entry_id: workout.entries[0].entry_id, sets: [{ set_id: workout.entries[0].sets[0].set_id, w: 62.5 }] }] }
const corrected = await tool('update_workout', workoutEdit)
assert.deepEqual(await tool('update_workout', workoutEdit), corrected)
logged = await tool('get_workout', { workout_id: workout.workout_id })
assert.equal(logged.volume, 980)
assert.equal(logged.editing.note, 'MCP workout smoke')
assert.equal(logged.editing.duration_minutes, 42)
await tool('update_workout', { ...workoutEdit, operation_id: crypto.randomUUID() }, 'version_conflict')
const legacyTrainingToken = await authorize('training.read')
const deniedTraining = await rpc('tools/call', { name: 'create_workout', arguments: { ...workoutArgs, workout_id: crypto.randomUUID() } }, 200, legacyTrainingToken)
assert.equal(deniedTraining.result.structuredContent.code, 'insufficient_scope')
await tool('delete_workout', { workout_id: workout.workout_id, unit: 'kg', expected_version: corrected.version,
  operation_id: crypto.randomUUID(), confirmed: true })
await tool('create_workout', workoutArgs, 'workout_deleted')
assert.equal((await tool('nutrition_search_products', {})).products.length, 0)
if (process.argv.includes('--offline-estimates')) {
  await tool('nutrition_search_reference_foods', { search: 'chicken breast' }, 'source_unavailable')
  await tool('nutrition_get_external_product', { external_id: '7622210449283' }, 'external_unavailable')
}
const estimateArguments = { product_id: crypto.randomUUID(), name: 'Air-fried chicken breast without oil',
  calories: 165, protein: 31, fat: 3.6, carbohydrates: 0, data_origin: 'ai_estimate',
  food_state: 'cooked', cooking_method: 'air_fried' }
const estimatedProduct = await tool('nutrition_create_custom_product', estimateArguments)
assert.equal(estimatedProduct.provenance.dataOrigin, 'ai_estimate')
assert.equal(estimatedProduct.provenance.source, null)
assert.deepEqual(await tool('nutrition_create_custom_product', estimateArguments), estimatedProduct)
const estimatedEntry = { entry_id: crypto.randomUUID(), product_id: estimatedProduct.id,
  quantity_grams: 150, date: '2026-10-16' }
const firstEstimate = await tool('nutrition_create_entry', estimatedEntry)
assert.deepEqual(firstEstimate.nutrition, { calories: 247.5, protein: 46.5, fat: 5.4, carbohydrates: 0 })
assert.equal(firstEstimate.provenance.dataOrigin, 'ai_estimate')
assert.deepEqual(await tool('nutrition_create_entry', estimatedEntry), firstEstimate)
assert.equal((await tool('nutrition_get_diary', { date: estimatedEntry.date })).entries.length, 1)
const reusedEstimate = (await tool('nutrition_search_products', { search: estimateArguments.name })).products
assert.equal(reusedEstimate.length, 1)
assert.equal(reusedEstimate[0].id, estimatedProduct.id)
assert.equal((await tool('nutrition_create_custom_product', { ...estimateArguments, product_id: crypto.randomUUID() })).id, estimatedProduct.id)
await tool('nutrition_create_entry', { ...estimatedEntry, entry_id: crypto.randomUUID(), product_id: reusedEstimate[0].id })
const repeatedDay = await tool('nutrition_get_diary', { date: estimatedEntry.date })
assert.equal(repeatedDay.entries.length, 2)
assert.equal(repeatedDay.totals.calories, 495)
assert.equal((await tool('nutrition_search_products', { search: estimateArguments.name })).products.length, 1)
console.log('MCP estimate scenario passed: empty catalog -> ai_estimate -> 150g -> diary -> product reuse without duplicates.')
if (process.argv.includes('--offline-estimates')) {
  const readOnlyToken = await authorize('nutrition.read')
  const deniedEstimate = await rpc('tools/call', { name: 'nutrition_create_custom_product', arguments: estimateArguments }, 200, readOnlyToken)
  assert.equal(deniedEstimate.result.structuredContent.code, 'insufficient_scope')
  console.log('Offline estimate smoke passed through real OAuth, MCP and .NET with USDA and OFF unreachable.')
  process.exit(0)
}
const customId = crypto.randomUUID()
await tool('nutrition_create_custom_product', { product_id: customId, name: 'Rejected source',
  calories: 165, protein: 31.02, fat: 3.57, carbohydrates: 0, food_state: 'cooked', cooking_method: 'roasted',
  source: { provider: 'usda', food_id: '171077', url: 'http://169.254.169.254/private' } }, 'unsupported_source')
assert.equal((await tool('nutrition_search_products', {})).products.length, 1)
// CI uses its existing external-catalog fixture. --with-usda selects an actual external-service check.
let customArguments
if (process.argv.includes('--with-usda') || process.argv.includes('--usda-fixture')) {
  const { foods } = await tool('nutrition_search_reference_foods', { search: 'chicken breast meat only cooked roasted' })
  const reference = foods.find(food => /chicken/i.test(food.description) && /breast/i.test(food.description)
    && /roasted/i.test(food.description) && !/\b(raw|skin|oil|fried)\b/i.test(food.description) && food.per100Grams)
  assert.ok(reference, 'No complete, suitable USDA reference; clarify instead of inventing a product')
  const custom = { product_id: customId, name: 'Cooked chicken breast (USDA reference)', ...reference.per100Grams,
    food_state: 'cooked', cooking_method: 'roasted', source: { provider: 'usda', food_id: reference.foodId, url: reference.url } }
  customArguments = custom
  const food = await tool('nutrition_create_custom_product', custom)
  assert.equal(food.provenance.source.verificationStatus, 'source_verified_reference')
  assert.deepEqual(await tool('nutrition_create_custom_product', custom), food)
  assert.equal((await tool('nutrition_create_custom_product', { ...custom, product_id: crypto.randomUUID() })).id, food.id)
  const eaten = { entry_id: crypto.randomUUID(), product_id: food.id, quantity_grams: 150, date: '2026-10-15' }
  const recorded = await tool('nutrition_create_entry', eaten)
  assert.equal(recorded.quantityGrams, 150)
  assert.equal(recorded.nutrition.calories, food.calories * 1.5)
  assert.deepEqual(await tool('nutrition_create_entry', eaten), recorded)
  assert.equal((await tool('nutrition_get_diary', { date: eaten.date })).entries.length, 1)
  console.log(`${process.argv.includes('--usda-fixture') ? 'USDA fixture' : 'Live USDA'} + real OAuth MCP + .NET: empty catalog -> verified custom food -> 150 g, retries passed.`)
}
const productResponse = await call('/api/nutrition/products', { method: 'POST', headers: {
  Cookie: cookie, Origin: base, 'Content-Type': 'application/json'
}, body: JSON.stringify({ name: 'MCP buckwheat', calories: 300, protein: 10, fat: 2, carbohydrates: 60 }) })
assert.equal(productResponse.status, 201)
const product = await productResponse.json()
assert.equal((await tool('nutrition_search_products', { search: 'buckwheat' })).products[0].id, product.id)
const entry = { entry_id: crypto.randomUUID(), product_id: product.id, quantity_grams: 200, date: '2026-10-13', time: '12:00' }
const saved = await tool('nutrition_create_entry', entry)
assert.equal(saved.nutrition.calories, 600)
assert.deepEqual(await tool('nutrition_create_entry', entry), saved)
await tool('nutrition_create_entry', { ...entry, quantity_grams: 300 }, 'idempotency_conflict')
const diary = await tool('nutrition_get_diary', { date: entry.date })
assert.equal(diary.entries.length, 1)
assert.equal(diary.totals.calories, 600)
const changed = await tool('nutrition_update_entry', { entry_id: entry.entry_id, quantity_grams: 100, date: entry.date })
assert.equal(changed.nutrition.calories, 300)
await tool('nutrition_delete_entry', { entry_id: entry.entry_id, confirmed: true })
await tool('nutrition_create_entry', entry, 'entry_deleted')
assert.equal((await tool('nutrition_get_diary', { date: entry.date })).entries.length, 0)
// Full import through the real OAuth -> MCP -> Node -> .NET chain. Only OFF is a fixture.
assert.equal((await tool('nutrition_search_products', { barcode: '7622210449283' })).products.length, 0)
const candidates = await tool('nutrition_search_external_products', { search: 'Fixture Milka Alpenmilch', brand: 'Milka' })
assert.equal(candidates.hasMore, false)
const external = await tool('nutrition_get_external_product', { external_id: candidates.products[0].externalId })
assert.equal(external.canImport, true)
assert.equal(external.per100Grams.calories, 530)
const importArgs = { source: external.source, external_id: external.externalId, fingerprint: external.fingerprint }
const imports = await Promise.all(Array.from({ length: 4 }, () => tool('nutrition_create_product', importArgs)))
const imported = imports[0]
assert.ok(imports.every(p => p.id === imported.id))
const chocolateArgs = { entry_id: crypto.randomUUID(), product_id: imported.id, quantity_grams: 40, date: '2026-10-14' }
const chocolate = await tool('nutrition_create_entry', chocolateArgs)
assert.equal(chocolate.nutrition.calories, 212)
assert.deepEqual(await tool('nutrition_create_entry', chocolateArgs), chocolate)
assert.equal((await tool('nutrition_search_products', { barcode: external.externalId })).products[0].id, imported.id)
assert.equal((await tool('nutrition_get_diary', { date: chocolateArgs.date })).entries.length, 1)
const readToken = await authorize('nutrition.read')
if (customArguments) {
  const deniedCustom = await rpc('tools/call', { name: 'nutrition_create_custom_product', arguments: customArguments }, 200, readToken)
  assert.equal(deniedCustom.result.structuredContent.code, 'insufficient_scope')
}
const denied = await rpc('tools/call', { name: 'nutrition_create_product', arguments: importArgs }, 200, readToken)
assert.equal(denied.result.structuredContent.code, 'insufficient_scope')
const allowed = await rpc('tools/call', { name: 'nutrition_get_external_product', arguments: { external_id: external.externalId } }, 200, readToken)
assert.equal(allowed.result.structuredContent.canImport, true)
const connectionsResponse = await call('/api/mcp/connections', { headers: { Cookie: cookie } })
assert.equal(connectionsResponse.status, 200)
const connections = await connectionsResponse.json()
assert.equal(connections.connections.length, 3)
await toggle(false)
for (const connection of connections.connections) {
  const revoke = await call('/api/mcp/connections/revoke', { method: 'POST', headers: { Cookie: cookie, Origin: base, 'Content-Type': 'application/json' },
    body: JSON.stringify({ grantId: connection.grantId }) })
  assert.equal(revoke.status, 200)
}
await toggle(true)
await rpc('tools/list', {}, 401)
await call('/api/logout/all', { method: 'POST', headers: { Cookie: cookie, Origin: base, 'Content-Type': 'application/json' }, body: '{}' })
await rpc('tools/list', {}, 401)
assert.equal((await call('/api/mcp/connections', { headers: { Cookie: cookie } })).status, 401)
console.log('MCP OAuth + real Nutrition API: DCR/PKCE, OFF search/verify/import, concurrent reuse, 40g diary snapshots, read-only scope and revocation passed.')
