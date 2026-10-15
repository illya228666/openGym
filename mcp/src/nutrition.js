import { z } from 'zod'

const uuid = () => z.uuid()
const date = () => z.string().regex(/^\d{4}-\d{2}-\d{2}$/).refine(value => {
  const parsed = new Date(value + 'T12:00:00Z')
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value
}, 'Invalid calendar date')
const quantity = () => z.number().positive().max(100000).refine(value =>
  Math.abs(value * 1000 - Math.round(value * 1000)) < 0.000001, 'At most three decimal places')
const time = () => z.string().regex(/^([01]\d|2[0-3]):[0-5]\d$/).nullable().optional()

const nutrient = max => z.number().min(0).max(max).refine(value =>
  Math.abs(value * 1000 - Math.round(value * 1000)) < 0.000001, 'At most three decimal places')

/** Nutrition API operations only; no persistence, catalog guesses or nutrient math. */
export function createNutritionTools(request) {
  const tool = (name, description, schema, handler, extra = {}) => ({ name, description, schema,
    scope: extra.write ? 'nutrition.write' : 'nutrition.read', handler, ...extra })
  return [
    tool('nutrition_search_products', 'Always search your personal catalog first. Reuse an unambiguous saved product; values are per 100 grams. Preserve brand, flavor, preparation and package variants. If absent, search Open Food Facts for packaged products or USDA reference foods for homemade food. Follow pagination before concluding absence or uniqueness.',
      { search: z.string().max(200).optional(), offset: z.number().int().nonnegative().optional(),
        brand: z.string().max(100).optional(), barcode: z.string().regex(/^[0-9]{8,14}$/).optional(),
        externalId: z.string().regex(/^[0-9]{8,14}$/).optional(), externalSource: z.literal('openfoodfacts').optional() },
      params => request('GET', '/api/nutrition/products', params)),
    tool('nutrition_search_external_products', 'Search Open Food Facts only after checking your personal catalog. Supply name/search, brand and/or exact barcode. Returns candidates, not verified nutrition. Different flavors, package sizes and preparation states are distinct. Ask the user when multiple plausible matches remain or details are insufficient; never select the first result or infer uniqueness from an incomplete page.',
      { search: z.string().max(200).optional(), brand: z.string().max(100).optional(),
        barcode: z.string().regex(/^[0-9]{8,14}$/).optional(), page: z.number().int().min(1).max(100).optional() },
      p => request('GET', '/api/nutrition/external/products', p), { openWorld: true }),
    tool('nutrition_get_external_product', 'Read a specific Open Food Facts candidate. Only canImport=true supplies complete checked nutrition per 100 grams and a fingerprint. OFF is community data, not guaranteed manufacturer verification. If canImport=false, explain issues and request missing information; do not invent nutrition or treat portions/100ml as grams.',
      { external_id: z.string().regex(/^[0-9]{8,14}$/) },
      p => request('GET', `/api/nutrition/external/products/${p.external_id}`), { openWorld: true }),
    tool('nutrition_create_product', 'Import one unambiguously matched, verified OFF product into your personal catalog using its external ID and fingerprint from nutrition_get_external_product. Nutrition fetches and checks the source itself. No extra confirmation is needed for an unambiguous match with canImport=true. Reuses existing products, including after uncertain writes: retry with the same source/ID/fingerprint. On external_product_changed read again; product_deleted requires explicit user intent before any restoration. Then log the user-supplied grams with nutrition_create_entry.',
      { source: z.literal('openfoodfacts'), external_id: z.string().regex(/^[0-9]{8,14}$/), fingerprint: z.string().regex(/^[0-9a-f]{64}$/) },
      p => request('POST', '/api/nutrition/products/import', {}, { source: p.source, externalId: p.external_id, fingerprint: p.fingerprint }),
      { write: true, idempotent: true, openWorld: true }),
    tool('nutrition_search_reference_foods', 'If the personal catalog has no suitable food, search USDA FoodData Central using English food/preparation terms. Returns non-branded reference candidates and per100Grams when complete, not a claim that a candidate matches the meal. Never substitute an industrial product for homemade food. Inspect descriptions; clarify ambiguous food or raw/cooked state. These are reference averages, not laboratory measurements of the meal.',
      { search: z.string().trim().min(1).max(200) }, p => request('GET', '/api/nutrition/reference-foods', p), { openWorld: true }),
    tool('nutrition_create_custom_product', 'Create or reuse a personal food from a USDA reference candidate. Supply all four per-100g values and source; the API independently verifies them. Missing values must never be replaced with zero. Retain one product_id and identical payload for retries; use the returned id (deduplication may return an existing product). food_state is raw, ready or cooked; cooked requires cooking_method. A different cooking method is an analog: ask the user first and only then set confirmed_preparation_analog=true. Raw/cooked mismatch is never allowed; do not convert raw to cooked mass. On success call nutrition_create_entry with the returned id, eaten grams and explicit date. Do not claim the diary was saved until that call succeeds.',
      { product_id: uuid(), name: z.string().trim().min(1).max(200), brand: z.string().trim().max(100).nullable().optional(),
        calories: nutrient(1000), protein: nutrient(100), fat: nutrient(100), carbohydrates: nutrient(100),
        source: z.object({ provider: z.literal('usda'), food_id: z.string().regex(/^[1-9][0-9]{0,8}$/), url: z.string().max(300) }).strict(),
        food_state: z.enum(['raw', 'ready', 'cooked']),
        cooking_method: z.enum(['cooked', 'roasted', 'baked', 'grilled', 'boiled', 'fried', 'air_fried', 'steamed', 'stewed', 'braised']).optional(),
        confirmed_preparation_analog: z.boolean().default(false) },
      p => request('POST', '/api/nutrition/products', {}, { id: p.product_id, name: p.name, brand: p.brand ?? null,
        calories: p.calories, protein: p.protein, fat: p.fat, carbohydrates: p.carbohydrates,
        source: { provider: p.source.provider, foodId: p.source.food_id, url: p.source.url },
        foodState: p.food_state, cookingMethod: p.cooking_method ?? null,
        confirmedPreparationAnalog: p.confirmed_preparation_analog }), { write: true, idempotent: true, openWorld: true }),
    tool('nutrition_get_product', 'Read a personal product by its ID from search results.',
      { product_id: uuid() }, p => request('GET', `/api/nutrition/products/${p.product_id}`)),
    tool('nutrition_get_diary', 'Read food entries and exact totals computed by Nutrition for an explicit local calendar date.',
      { date: date() }, p => request('GET', '/api/nutrition/diary', p)),
    tool('nutrition_get_entry', 'Read one diary entry before changing or deleting it.',
      { entry_id: uuid() }, p => request('GET', `/api/nutrition/entries/${p.entry_id}`)),
    tool('nutrition_create_entry', 'Record one unambiguously identified saved product and user-supplied grams, including a product just imported with nutrition_create_product; no additional confirmation is needed. Supply a new UUID entry_id once and reuse it with the identical payload on every retry. Do not guess product, raw/cooked state, grams from pieces or a local date.',
      { entry_id: uuid(), product_id: uuid(), quantity_grams: quantity(), date: date(), time: time() },
      p => request('POST', '/api/nutrition/entries', {}, { id: p.entry_id, productId: p.product_id,
        quantityGrams: p.quantity_grams, date: p.date, time: p.time ?? null }), { write: true, idempotent: true }),
    tool('nutrition_update_entry', 'Update a previously read entry. Send its complete quantity/date/time. Omit product_id to retain historical nutrition, even if its product was deleted; specify it only for an intentional product replacement. Concurrent edits are last-write-wins.',
      { entry_id: uuid(), product_id: uuid().optional(), quantity_grams: quantity(), date: date(), time: time() },
      p => request('PUT', `/api/nutrition/entries/${p.entry_id}`, {}, { productId: p.product_id ?? null,
        quantityGrams: p.quantity_grams, date: p.date, time: p.time ?? null }), { write: true, destructive: true }),
    tool('nutrition_delete_entry', 'Delete the specific entry the user has confirmed. Read it first and ask for confirmation; do not set confirmed for an ambiguous deletion.',
      { entry_id: uuid(), confirmed: z.literal(true) }, p => request('DELETE', `/api/nutrition/entries/${p.entry_id}`),
      { write: true, destructive: true, idempotent: true })
  ]
}
