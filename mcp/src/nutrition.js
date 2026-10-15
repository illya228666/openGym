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
    tool('nutrition_search_products', 'Search your personal catalog first and reuse a matching product, including saved estimates. Values are per 100 grams. Preserve brand, flavor and preparation. If absent, USDA and Open Food Facts are optional accuracy aids; you may create an ai_estimate or user_provided custom product without them. Follow pagination before concluding absence or uniqueness.',
      { search: z.string().max(200).optional(), offset: z.number().int().nonnegative().optional(),
        brand: z.string().max(100).optional(), barcode: z.string().regex(/^[0-9]{8,14}$/).optional(),
        externalId: z.string().regex(/^[0-9]{8,14}$/).optional(), externalSource: z.literal('openfoodfacts').optional() },
      params => request('GET', '/api/nutrition/products', params)),
    tool('nutrition_search_external_products', 'Search Open Food Facts only after checking your personal catalog. Supply name/search, brand and/or exact barcode. Returns candidates, not verified nutrition. Different flavors, package sizes and preparation states are distinct. Ask the user when multiple plausible matches remain or details are insufficient; never select the first result or infer uniqueness from an incomplete page.',
      { search: z.string().max(200).optional(), brand: z.string().max(100).optional(),
        barcode: z.string().regex(/^[0-9]{8,14}$/).optional(), page: z.number().int().min(1).max(100).optional() },
      p => request('GET', '/api/nutrition/external/products', p), { openWorld: true }),
    tool('nutrition_get_external_product', 'Optionally read an Open Food Facts candidate. canImport=true supplies checked source nutrition per 100 grams and a fingerprint for import; OFF is community data, not guaranteed manufacturer verification. If unavailable or canImport=false, the separate nutrition_create_custom_product tool can save user_provided or clearly labeled ai_estimate values without OFF. Do not label those values as OFF-verified or treat portions/100ml as grams.',
      { external_id: z.string().regex(/^[0-9]{8,14}$/) },
      p => request('GET', `/api/nutrition/external/products/${p.external_id}`), { openWorld: true }),
    tool('nutrition_create_product', 'Import one unambiguously matched, verified OFF product into your personal catalog using its external ID and fingerprint from nutrition_get_external_product. Nutrition fetches and checks the source itself. No extra confirmation is needed for an unambiguous match with canImport=true. Reuses existing products, including after uncertain writes: retry with the same source/ID/fingerprint. On external_product_changed read again; product_deleted requires explicit user intent before any restoration. Then log the user-supplied grams with nutrition_create_entry.',
      { source: z.literal('openfoodfacts'), external_id: z.string().regex(/^[0-9]{8,14}$/), fingerprint: z.string().regex(/^[0-9a-f]{64}$/) },
      p => request('POST', '/api/nutrition/products/import', {}, { source: p.source, externalId: p.external_id, fingerprint: p.fingerprint }),
      { write: true, idempotent: true, openWorld: true }),
    tool('nutrition_search_reference_foods', 'Optionally search USDA FoodData Central using English food/preparation terms. Returns non-branded candidates and per100Grams when complete. These are reference averages, not laboratory measurements of the meal. USDA failure does not prevent creating a clearly labeled ai_estimate or user_provided custom product. Do not substitute an industrial product for homemade food.',
      { search: z.string().trim().min(1).max(200) }, p => request('GET', '/api/nutrition/reference-foods', p), { openWorld: true }),
    tool('nutrition_create_custom_product', 'Create or reuse personal food with all four per-100g values. data_origin: ai_estimate for reasonable AI estimates, user_provided for user values, verified_reference for independently checked USDA values. Estimates/user values need no external source or additional confirmation. When the user delegates estimation (including an ordinary request to log homemade food), choose reasonable values for the stated preparation, save ai_estimate and disclose that the values are approximate. Do not zero-fill unknowns; an estimated zero is allowed when appropriate. source is optional except for verified_reference, which retains USDA verification and preparation-analog confirmation. Omitting data_origin infers verified_reference when source is supplied, otherwise ai_estimate. food_state is raw/ready/cooked; cooked requires cooking_method. Retain product_id and identical payload on retries; use the returned id and values when a product is reused. Then call nutrition_create_entry with eaten grams/date and confirm only after it succeeds.',
      { product_id: uuid(), name: z.string().trim().min(1).max(200), brand: z.string().trim().max(100).nullable().optional(),
        calories: nutrient(1000), protein: nutrient(100), fat: nutrient(100), carbohydrates: nutrient(100),
        data_origin: z.enum(['verified_reference', 'ai_estimate', 'user_provided']).optional(),
        source: z.object({ provider: z.literal('usda'), food_id: z.string().regex(/^[1-9][0-9]{0,8}$/), url: z.string().max(300) }).strict().nullable().optional(),
        food_state: z.enum(['raw', 'ready', 'cooked']),
        cooking_method: z.enum(['cooked', 'roasted', 'baked', 'grilled', 'boiled', 'fried', 'air_fried', 'steamed', 'stewed', 'braised']).optional(),
        confirmed_preparation_analog: z.boolean().default(false) },
      p => request('POST', '/api/nutrition/products', {}, { id: p.product_id, name: p.name, brand: p.brand ?? null,
        calories: p.calories, protein: p.protein, fat: p.fat, carbohydrates: p.carbohydrates,
        source: p.source ? { provider: p.source.provider, foodId: p.source.food_id, url: p.source.url } : null,
        ...(p.data_origin ? { dataOrigin: p.data_origin } : {}),
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
