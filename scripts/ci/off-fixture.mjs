// Test-only OFF/USDA responses. Synthetic nutrition, never a live food source.
import http from 'node:http'

const code = '7622210449283'
const product = { code, product_name: 'Fixture Milka Alpenmilch', brands: 'Milka', quantity: '100 g',
  nutrition: { input_sets: [{ source: 'packaging', preparation: 'as_sold', per: '100g', nutrients: {
    'energy-kcal': { value: 530, unit: 'kcal' }, proteins: { value: 6.3, unit: 'g' },
    fat: { value: 29, unit: 'g' }, carbohydrates: { value: 59, unit: 'g' }
  } }] } }
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://fixture')
  if (url.pathname === '/fdc/v1/foods/search' || url.pathname === '/fdc/v1/food/171077') {
    const search = url.pathname.endsWith('/search')
    const food = { fdcId: 171077, description: 'Fixture chicken breast, meat only, cooked, roasted', dataType: 'SR Legacy',
      foodNutrients: [[1008, 165, 'kcal'], [1003, 31.02, 'g'], [1004, 3.57, 'g'], [1005, 0, 'g']].map(([id, value, unitName]) =>
        search ? { nutrientId: id, value, unitName } : { nutrient: { id, unitName }, amount: value }) }
    res.writeHead(200, { 'Content-Type': 'application/json' })
    res.end(JSON.stringify(search ? { foods: [food] } : food))
  } else if (url.pathname === '/cgi/search.pl') {
    res.writeHead(200, { 'Content-Type': 'application/json' })
    res.end(JSON.stringify({ products: [product], count: 1 }))
  } else if (url.pathname === '/api/v3.6/product/' + code) {
    res.writeHead(200, { 'Content-Type': 'application/json' })
    res.end(JSON.stringify({ product }))
  } else { res.writeHead(404); res.end('{}') }
})
server.listen(Number(process.env.OFF_FIXTURE_PORT || 5099), '0.0.0.0', () =>
  console.log('OFF fixture listening on ' + server.address().port))
