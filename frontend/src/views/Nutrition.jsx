import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useStore } from '../store/useStore.js'
import { api } from '../lib/api.js'
import { t, dateLocale } from '../lib/i18n.js'
import { todayISO } from '../lib/format.js'
import Icon from '../components/Icon.jsx'
import { Button, NumberField, SearchField, Segmented, TextField } from '../components/ui.jsx'
import './nutrition.css'

const nutrients = [['calories', 'Calories (kcal)'], ['protein', 'Protein (g)'], ['fat', 'Fat (g)'], ['carbohydrates', 'Carbohydrates (g)']]
const format = (value, digits = 2) => new Intl.NumberFormat(dateLocale(), { maximumFractionDigits: digits }).format(value)
const validValues = values => values && nutrients.every(([key]) => Number.isFinite(values[key]) && values[key] >= 0)
const failureText = error => error.status === 401 ? t('Sign in to use Nutrition.')
  : error.data?.code === 'invalid_input' ? t('Check the name, date and nutritional values. Use up to 3 decimal places.')
    : error.status === 409 ? t('This request was already used. Close the form and check your diary before adding again.')
      : error.status === 404 || error.status === 410 ? t('This item is no longer available. Refresh and try again.')
        : t('Nutrition is unavailable. Please try again.')
const shiftDate = (date, days) => {
  const value = new Date(date + 'T12:00:00')
  value.setDate(value.getDate() + days)
  return `${String(value.getFullYear()).padStart(4, '0')}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`
}

function NutritionValues({ values }) {
  return <dl className="nutrition-values">{nutrients.map(([key, label]) => <div key={key}>
    <dt>{t(label)}</dt><dd>{format(values[key])}</dd>
  </div>)}</dl>
}

export default function Nutrition() {
  const nav = useNavigate()
  const user = useStore(s => s.user)
  return <>
    <div className="hdr">
      <button className="iconbtn" onClick={() => nav('/home')} aria-label={t('Back')}><Icon name="chevronLeft" /></button>
      <h1>{t('Nutrition')}</h1>
    </div>
    {user ? <FoodDiary key={user.id} /> : <div className="card"><p>{t('Sign in to use Nutrition.')}</p></div>}
  </>
}

function FoodDiary() {
  const [date, setDate] = useState(todayISO)
  const [tab, setTab] = useState('diary')
  const [attempt, setAttempt] = useState(0)
  const [diary, setDiary] = useState(null)
  const [portions, setPortions] = useState([])
  const [error, setError] = useState(null)
  const [form, setForm] = useState(null)
  const [busy, setBusy] = useState(false)
  const [deleting, setDeleting] = useState(null)
  const live = useRef(true)
  const writing = useRef(false)
  useEffect(() => { live.current = true; return () => { live.current = false } }, [])
  useEffect(() => {
    let current = true
    setDiary(null); setError(null)
    api('/api/nutrition/diary?date=' + date).then(body => {
      if (!Array.isArray(body.entries) || !validValues(body.totals) || body.date !== date
        || body.entries.some(entry => !entry || typeof entry.name !== 'string' || !validValues(entry.nutrition))) throw new Error('Invalid diary')
      if (current) setDiary(body)
    }).catch(e => { if (current) setError(e) })
    return () => { current = false }
  }, [date, attempt])
  useEffect(() => {
    let current = true
    api('/api/nutrition/portions').then(body => {
      if (!Array.isArray(body.portions)) throw new Error('Invalid portions')
      if (current) setPortions(body.portions)
    }).catch(() => { if (current) setPortions([]) })
    return () => { current = false }
  }, [attempt])
  const changeDate = next => { if (next) { setDate(next); setForm(null); setDeleting(null) } }
  const mutate = async operation => {
    if (writing.current) return false
    writing.current = true; setBusy(true); setError(null)
    try {
      await operation()
      if (live.current) { setForm(null); setDeleting(null); setAttempt(n => n + 1) }
      return true
    } catch (e) { if (live.current) setError(e) }
    finally { writing.current = false; if (live.current) setBusy(false) }
  }
  return <section className="nutrition-page">
    <Segmented value={tab} onChange={value => { if (!busy) { setTab(value); setForm(null); setDeleting(null); setError(null) } }}
      options={[{ value: 'diary', label: t('Food diary') }, { value: 'portions', label: t('My portions') }, { value: 'products', label: t('My products') }]} />
    {error && <div className="nutrition-error" role="alert"><p>{failureText(error)}</p>
      <Button disabled={busy} onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</Button></div>}
    {tab === 'diary' ? <>
      <div className="nutrition-date">
        <button className="iconbtn" disabled={busy || date === '0001-01-01'} aria-label={t('Previous day')} onClick={() => changeDate(shiftDate(date, -1))}><Icon name="chevronLeft" /></button>
        <TextField type="date" min="0001-01-01" max="9999-12-31" aria-label={t('Consumption date')} value={date} disabled={busy} onChange={e => changeDate(e.target.value)} />
        <button className="iconbtn" disabled={busy || date === '9999-12-31'} aria-label={t('Next day')} onClick={() => changeDate(shiftDate(date, 1))}><Icon name="chevronRight" /></button>
        <Button disabled={busy} onClick={() => changeDate(todayISO())}>{t('Today')}</Button>
      </div>
      {diary && <div className="card"><h2>{t('Daily totals')}</h2><NutritionValues values={diary.totals} /></div>}
      {!form && <Button variant="primary" icon="plus" disabled={busy} onClick={() => { setError(null); setForm({ kind: 'entry', date }) }}>{t('Add food')}</Button>}
      {portions.length > 0 && <div className="nutrition-portion-shortcuts"><h2>{t('Add a portion')}</h2>{portions.map(portion => <ApplyPortion key={portion.id + date} portion={portion} date={date} busy={busy}
        onApply={request => mutate(() => api(`/api/nutrition/portions/${portion.id}/apply`, { method: 'POST', body: JSON.stringify(request) }))} />)}</div>}
      {form?.kind === 'entry' && <EntryForm key={form.entry?.id || 'new'} entry={form.entry} date={form.date}
        busy={busy} onCancel={() => setForm(null)} onSave={request => mutate(() => api('/api/nutrition/entries' + (form.entry ? '/' + form.entry.id : ''),
          { method: form.entry ? 'PUT' : 'POST', body: JSON.stringify(request) }))} />}
      {!diary && !error && <p role="status">{t('Loading…')}</p>}
      {diary && <>
        <h2>{t('Consumed food')}</h2>
        {!diary.entries.length ? <p className="empty">{t('No food recorded for this day. Add your first product.')}</p>
          : <ul className="nutrition-list">{renderGroupedEntries(diary.entries).map(item => item.type === 'group'
            ? <li key={item.id} className="nutrition-portion-group"><details><summary><strong>{item.name}</strong></summary>
              <ul className="nutrition-list nutrition-portion-ingredients">{item.entries.map(entry => renderEntry(entry))}</ul>
              <NutritionValues values={item.entries.reduce((total, entry) => Object.fromEntries(nutrients.map(([key]) => [key, total[key] + entry.nutrition[key]])), { calories: 0, protein: 0, fat: 0, carbohydrates: 0 })} />
            </details></li>
            : renderEntry(item.entry))}</ul>}
      </>}
    </> : tab === 'products' ? <ProductCatalog key={attempt} busy={busy} onMutation={mutate} />
      : <PortionCatalog key={attempt} busy={busy} portions={portions} onMutation={mutate} />}
  </section>

  function renderEntry(entry) {
    return <li key={entry.id}>
      <div className="nutrition-row"><div><strong>{entry.name}</strong>{entry.brand && <p>{entry.brand}</p>}
        <p>{format(entry.quantity ?? entry.quantityGrams, 3)} {entry.unit === 'pieces' ? t('pcs') : t('g')} · {entry.time || t('No time')}</p></div>
        <div className="nutrition-actions"><Button disabled={busy} onClick={() => { setError(null); setDeleting(null); setForm({ kind: 'entry', entry, date: entry.date }) }}>{t('Edit')}</Button>
          <Button disabled={busy} onClick={() => setDeleting(entry.id)}>{t('Delete')}</Button></div></div>
      <NutritionValues values={entry.nutrition} />
      {deleting === entry.id && <div className="nutrition-actions"><span>{t('Delete this diary entry?')}</span>
        <Button disabled={busy} onClick={() => mutate(() => api('/api/nutrition/entries/' + entry.id, { method: 'DELETE' }))}>{t('Confirm')}</Button>
        <Button disabled={busy} onClick={() => setDeleting(null)}>{t('Cancel')}</Button></div>}
    </li>
  }
}

function renderGroupedEntries(entries) {
  const items = []
  const groups = new Map()
  for (const entry of entries) {
    if (!entry.portionGroupId) { items.push({ type: 'entry', id: entry.id, entry }); continue }
    let group = groups.get(entry.portionGroupId)
    if (!group) { group = { type: 'group', id: entry.portionGroupId, name: entry.portionName || '', entries: [] }; groups.set(entry.portionGroupId, group); items.push(group) }
    group.entries.push(entry)
  }
  return items
}

function ApplyPortion({ portion, date, busy, onApply }) {
  const [multiplier, setMultiplier] = useState(1)
  const [id, setId] = useState(() => crypto.randomUUID())
  const [submitted, setSubmitted] = useState(false)
  const available = portion.ingredients.every(item => item.productAvailable !== false)
  return <form className="nutrition-portion-apply" onSubmit={async e => {
    e.preventDefault()
    if (busy || !(multiplier > 0)) return
    setSubmitted(true)
    if (await onApply({ id, multiplier, date, time: null })) { setId(crypto.randomUUID()); setSubmitted(false) }
  }}>
    <strong>{portion.name}</strong>
    {!available && <span>{t('Replace unavailable products in this portion before adding it.')}</span>}
    <NumberField className="field" aria-label={t('Multiplier')} disabled={busy || submitted} value={multiplier} onChange={setMultiplier} />
    <Button type="submit" disabled={busy || !multiplier || !available}>{t('Add')}</Button>
  </form>
}

function PortionCatalog({ busy, portions, onMutation }) {
  const [editing, setEditing] = useState(null)
  const [deleting, setDeleting] = useState(null)
  return <>
    {!editing && <Button variant="primary" icon="plus" disabled={busy} onClick={() => setEditing({ name: '', ingredients: [] })}>{t('Create portion')}</Button>}
    {editing && <PortionForm key={editing.id || 'new'} portion={editing} busy={busy} onCancel={() => setEditing(null)}
      onSave={request => onMutation(() => api('/api/nutrition/portions' + (editing.id ? '/' + editing.id : ''),
        { method: editing.id ? 'PUT' : 'POST', body: JSON.stringify(request) }))} />}
    <ul className="nutrition-list">{portions.map(portion => <li key={portion.id}>
      <div className="nutrition-row"><strong>{portion.name}</strong><div className="nutrition-actions">
        <Button disabled={busy} onClick={() => { setDeleting(null); setEditing(portion) }}>{t('Edit')}</Button>
        <Button disabled={busy} onClick={() => setDeleting(portion.id)}>{t('Delete')}</Button></div></div>
      {deleting === portion.id && <div className="nutrition-actions"><span>{t('Delete this portion?')}</span>
        <Button disabled={busy} onClick={() => onMutation(() => api('/api/nutrition/portions/' + portion.id, { method: 'DELETE' }))}>{t('Confirm')}</Button>
        <Button disabled={busy} onClick={() => setDeleting(null)}>{t('Cancel')}</Button></div>}
      <ul className="nutrition-portion-ingredients">{portion.ingredients.map((ingredient, index) => <li key={index}>{ingredient.productName} · {format(ingredient.quantity, 3)} {ingredient.unit === 'pieces' ? t('pcs') : t('g')}</li>)}</ul>
    </li>)}</ul>
    {!portions.length && !editing && <p className="empty">{t('No portions yet.')}</p>}
  </>
}

function PortionForm({ portion, busy, onCancel, onSave }) {
  const [name, setName] = useState(portion.name || '')
  const [ingredients, setIngredients] = useState((portion.ingredients || []).map(item => ({ ...item })))
  const [product, setProduct] = useState(null)
  const [submitted, setSubmitted] = useState(false)
  const [id] = useState(() => portion.id || crypto.randomUUID())
  const addProduct = () => {
    if (!product) return
    setIngredients(current => [...current, { productId: product.id, productName: product.name, productBrand: product.brand,
      gramsPerPiece: product.gramsPerPiece, quantity: 100, unit: 'grams' }])
    setProduct(null)
  }
  const updateIngredient = (index, changes) => setIngredients(current => current.map((item, i) => i === index ? { ...item, ...changes } : item))
  return <div className="card nutrition-form">
    <h2>{portion.id ? t('Edit portion') : t('Create portion')}</h2>
    <label>{t('Portion name')}<TextField disabled={busy || (!portion.id && submitted)} value={name} maxLength={200} onChange={e => setName(e.target.value)} required /></label>
    <ProductPicker disabled={busy || (!portion.id && submitted)} selected={product?.id} onSelect={setProduct} />
    {product && <div className="nutrition-actions"><span>{product.name}</span><Button disabled={busy || (!portion.id && submitted)} onClick={addProduct}>{t('Add ingredient')}</Button></div>}
    <ul className="nutrition-list">{ingredients.map((item, index) => <li key={index}>
      <div className="nutrition-fields"><strong>{item.productName}</strong>
        <label>{t('Quantity')}<NumberField className="field" disabled={busy || (!portion.id && submitted)} aria-label={`${t('Quantity')} ${index + 1}`} value={item.quantity} nullable onChange={value => updateIngredient(index, { quantity: value })} required /></label>
        <label>{t('Unit')}<select className="field" disabled={busy || (!portion.id && submitted)} value={item.unit} onChange={e => {
          const next = e.target.value
          const grams = item.unit === 'pieces' ? item.quantity * item.gramsPerPiece : item.quantity
          updateIngredient(index, { unit: next, quantity: Math.round((next === 'pieces' ? grams / item.gramsPerPiece : grams) * 1000) / 1000 })
        }}><option value="grams">{t('g')}</option><option value="pieces" disabled={!(item.gramsPerPiece > 0)}>{t('pcs')}</option></select></label>
        <Button type="button" disabled={busy || (!portion.id && submitted)} onClick={() => setIngredients(current => current.filter((_, i) => i !== index))}>{t('Remove')}</Button>
      </div>
    </li>)}</ul>
    <div className="nutrition-actions"><Button variant="primary" disabled={busy || !name.trim() || !ingredients.length || ingredients.some(item => !(item.quantity > 0))}
      onClick={() => { if (!portion.id) setSubmitted(true); onSave({ ...(portion.id ? {} : { id }), name, ingredients: ingredients.map(({ productId, quantity, unit }) => ({ productId, quantity, unit })) }) }}>{t('Save')}</Button>
      <Button disabled={busy} onClick={onCancel}>{t('Cancel')}</Button></div>
  </div>
}

function ProductPicker({ selected, onSelect, refresh = 0, disabled = false, renderActions }) {
  const [search, setSearch] = useState('')
  const [offset, setOffset] = useState(0)
  const [page, setPage] = useState(null)
  const [error, setError] = useState(null)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    let current = true
    setPage(null); setError(null)
    const timer = setTimeout(() => {
      api(`/api/nutrition/products?search=${encodeURIComponent(search)}&offset=${offset}`).then(body => {
        if (!Array.isArray(body.products) || typeof body.hasMore !== 'boolean'
          || body.products.some(product => !product || typeof product.name !== 'string' || !validValues(product))) throw new Error('Invalid products')
        if (current) setPage(body)
      }).catch(e => { if (current) setError(e) })
    }, 200)
    return () => { current = false; clearTimeout(timer) }
  }, [search, offset, refresh, attempt])
  return <div className="nutrition-picker">
    <SearchField aria-label={t('Search products')} placeholder={t('Search products')} value={search} disabled={disabled}
      onChange={e => { setSearch(e.target.value); setOffset(0) }} onClear={() => { setSearch(''); setOffset(0) }} />
    {error ? <div role="alert"><p>{failureText(error)}</p><Button type="button" onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</Button></div>
      : !page ? <p role="status">{t('Loading…')}</p>
        : !page.products.length ? <p>{t('No products found. Create your own product to start.')}</p>
          : <ul className="nutrition-list">{page.products.map(product => <li key={product.id}>
            <div className="nutrition-row">
              {onSelect ? <button type="button" className="nutrition-choice" disabled={disabled} aria-pressed={selected === product.id} onClick={() => onSelect(product)}>
                <strong>{product.name}</strong>{product.brand && <span>{product.brand}</span>}
                <span>{format(product.calories)} {t('Calories (kcal)')} / 100 {t('Grams')}</span></button>
                : <div><strong>{product.name}</strong>{product.brand && <p>{product.brand}</p>}<NutritionValues values={product} /></div>}
              {renderActions?.(product)}
            </div>
          </li>)}</ul>}
    {page && (offset > 0 || page.hasMore) && <div className="nutrition-actions">
      <Button type="button" disabled={disabled || offset === 0} onClick={() => setOffset(n => Math.max(0, n - 50))}>{t('Previous products')}</Button>
      <Button type="button" disabled={disabled || !page.hasMore} onClick={() => setOffset(n => n + 50)}>{t('Next products')}</Button>
    </div>}
  </div>
}

function EntryForm({ entry, date, busy, onSave, onCancel }) {
  const [product, setProduct] = useState(null)
  const [quantity, setQuantity] = useState(entry?.quantity ?? entry?.quantityGrams ?? 100)
  const [unit, setUnit] = useState(entry?.unit ?? 'grams')
  const [originalGramsPerPiece, setOriginalGramsPerPiece] = useState(entry?.gramsPerPiece ?? null)
  const [day, setDay] = useState(date)
  const [time, setTime] = useState(entry?.time || '')
  const [creating, setCreating] = useState(false)
  const [refresh, setRefresh] = useState(0)
  // Kept for the lifetime of this form, including an uncertain network failure.
  const [id] = useState(() => entry?.id || crypto.randomUUID())
  useEffect(() => {
    if (!entry || originalGramsPerPiece != null) return
    let current = true
    api(`/api/nutrition/products/${entry.productId}`).then(result => {
      if (current && Number.isFinite(result.gramsPerPiece) && result.gramsPerPiece > 0) setOriginalGramsPerPiece(result.gramsPerPiece)
    }).catch(() => {})
    return () => { current = false }
  }, [entry, originalGramsPerPiece])
  const gramsPerPiece = product ? product.gramsPerPiece : originalGramsPerPiece
  const changeUnit = nextUnit => {
    if (nextUnit === unit) return
    if (nextUnit === 'pieces' && !(gramsPerPiece > 0)) return
    const grams = unit === 'grams' ? quantity : quantity * gramsPerPiece
    setQuantity(Math.round((nextUnit === 'grams' ? grams : grams / gramsPerPiece) * 1000) / 1000)
    setUnit(nextUnit)
  }
  return <div className="card nutrition-form">
    <h2>{entry ? t('Edit food') : t('Add food')}</h2>
    {entry && !product && <p>{t('Keeping the original nutritional snapshot.')}: <strong>{entry.name}</strong></p>}
    {product && <p>{t('Selected product')}: <strong>{product.name}</strong></p>}
    {entry && product && <Button disabled={busy} onClick={() => setProduct(null)}>{t('Keep original product')}</Button>}
    <ProductPicker selected={product?.id} disabled={busy} refresh={refresh} onSelect={setProduct} />
    <Button disabled={busy} onClick={() => setCreating(value => !value)}>{t('Create product')}</Button>
    {creating && <ProductForm busy={busy} onCancel={() => setCreating(false)} onSave={async request => {
      const result = await api('/api/nutrition/products', { method: 'POST', body: JSON.stringify(request) })
      setProduct(result); setCreating(false); setRefresh(n => n + 1)
    }} />}
    <form onSubmit={e => { e.preventDefault(); if (!busy) onSave({ ...(entry ? {} : { id }), productId: product?.id || null, quantity, unit, date: day, time: time || null }) }}>
      <div className="nutrition-fields">
        <label>{t('Quantity')}<NumberField className="field" disabled={busy} aria-label={t('Quantity')} value={quantity} nullable onChange={setQuantity} required /></label>
        <label>{t('Unit')}<select className="field" aria-label={t('Unit')} disabled={busy} value={unit} onChange={e => changeUnit(e.target.value)}>
          <option value="grams">{t('g')}</option><option value="pieces" disabled={!(gramsPerPiece > 0)}>{t('pcs')}</option>
        </select></label>
        <label>{t('Consumption date')}<TextField disabled={busy} type="date" min="0001-01-01" max="9999-12-31" value={day} onChange={e => setDay(e.target.value)} required /></label>
        <label>{t('Time (optional)')}<TextField disabled={busy} type="time" value={time} onChange={e => setTime(e.target.value)} /></label>
      </div>
      <div className="nutrition-actions"><Button variant="primary" type="submit" disabled={busy || creating || (!entry && !product) || !quantity || quantity > 100000 || !day}>{busy ? t('Saving…') : t('Save')}</Button>
        <Button type="button" disabled={busy} onClick={onCancel}>{t('Cancel')}</Button></div>
    </form>
  </div>
}

function ProductCatalog({ busy, onMutation }) {
  const [editing, setEditing] = useState(null)
  const [deleting, setDeleting] = useState(null)
  return <>
    <Button variant="primary" icon="plus" disabled={busy} onClick={() => setEditing({})}>{t('Create product')}</Button>
    {editing && <ProductForm key={editing.id || 'new'} product={editing} busy={busy} onCancel={() => setEditing(null)}
      onSave={request => onMutation(() => api('/api/nutrition/products' + (editing.id ? '/' + editing.id : ''), { method: editing.id ? 'PUT' : 'POST', body: JSON.stringify(request) }))} />}
    <ProductPicker disabled={busy} renderActions={product => <div className="nutrition-actions">
      <Button disabled={busy} onClick={() => { setDeleting(null); setEditing(product) }}>{t('Edit')}</Button>
      <Button disabled={busy} onClick={() => setDeleting(product.id)}>{t('Delete')}</Button>
      {deleting === product.id && <div className="nutrition-confirm"><p>{t('Delete product? Your diary history will be kept.')}</p>
        <Button disabled={busy} onClick={() => onMutation(() => api('/api/nutrition/products/' + product.id, { method: 'DELETE' }))}>{t('Confirm')}</Button>
        <Button disabled={busy} onClick={() => setDeleting(null)}>{t('Cancel')}</Button></div>}
    </div>} />
  </>
}

function ProductForm({ product = {}, busy, onSave, onCancel }) {
  const [draft, setDraft] = useState({ name: product.name || '', brand: product.brand || '', gramsPerPiece: product.gramsPerPiece ?? null,
    ...Object.fromEntries(nutrients.map(([key]) => [key, product[key] ?? null])) })
  const [pending, setPending] = useState(false)
  const [error, setError] = useState(null)
  const saving = useRef(false)
  const update = (key, value) => setDraft(current => ({ ...current, [key]: value }))
  return <form className="nutrition-product-form" onSubmit={async e => {
    e.preventDefault()
    if (saving.current || busy) return
    saving.current = true; setPending(true); setError(null)
    try { await onSave(draft) } catch (e) { setError(e) }
    finally { saving.current = false; setPending(false) }
  }}>
    <h2>{product.id ? t('Edit product') : t('Create product')}</h2>
    <label>{t('Product name')}<TextField disabled={pending || busy} value={draft.name} maxLength={200} required onChange={e => update('name', e.target.value)} /></label>
    <label>{t('Brand (optional)')}<TextField disabled={pending || busy} value={draft.brand} maxLength={100} onChange={e => update('brand', e.target.value)} /></label>
    <label>{t('Weight 1 piece (g)')}<NumberField className="field" disabled={pending || busy} aria-label={t('Weight 1 piece (g)')} value={draft.gramsPerPiece} nullable onChange={value => update('gramsPerPiece', value)} /></label>
    <p>{t('Nutritional values per 100 g')}</p>
    <div className="nutrition-fields">{nutrients.map(([key, label]) => <label key={key}>{t(label)}
      <NumberField className="field" disabled={pending || busy} value={draft[key]} nullable required aria-label={t(label)} onChange={value => update(key, value)} /></label>)}</div>
    {error && <p role="alert">{failureText(error)}</p>}
    <div className="nutrition-actions"><Button type="submit" variant="primary" disabled={pending || busy || !draft.name.trim() || nutrients.some(([key]) => draft[key] == null || draft[key] > (key === 'calories' ? 1000 : 100))}>{pending || busy ? t('Saving…') : t('Save')}</Button>
      <Button type="button" disabled={pending || busy} onClick={onCancel}>{t('Cancel')}</Button></div>
  </form>
}
