import { useEffect, useState } from 'react'
import { useStore } from '../store/useStore.js'
import { useUI } from '../store/useUI.js'
import { api } from '../lib/api.js'
import { copyText } from '../lib/clipboard.js'
import { t, dateLocale } from '../lib/i18n.js'
import { DEMO } from '../lib/demo.js'
import { confirmSheet } from '../sheets.jsx'
import { Section, Row, Button, Switch } from '../components/ui.jsx'

const scopeLabels = {
  'training.read': 'Read training plans and statistics',
  'nutrition.read': 'Read products, diary and nutritional totals',
  'nutrition.write': 'Import products and create, edit and delete food diary entries',
}

/** Public URL, account-owned grants and one administrator enable switch. */
export default function McpSettings() {
  const user = useStore(s => s.user)
  const toast = useUI(s => s.toast)
  const [state, setState] = useState(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [revision, setRevision] = useState(0)
  const ownerId = user?.id
  const signedIn = !!user && !DEMO
  useEffect(() => {
    let current = true
    setError(''); setState(null)
    if (signedIn) Promise.all([api('/api/mcp/settings'), api('/api/mcp/connections')])
      .then(([settings, grants]) => { if (current) setState({ ownerId, settings, connections: grants.connections || [] }) })
      .catch(() => { if (current) setError(t('Could not load MCP settings. Try again.')) })
    return () => { current = false }
  }, [ownerId, user?.admin, signedIn, revision])
  const loaded = state?.ownerId === ownerId ? state : null
  const toggle = async enabled => {
    if (busy || !user?.admin) return
    setBusy(true); setError('')
    try {
      const settings = await api('/api/mcp/settings', { method: 'PUT', body: JSON.stringify({ enabled }) })
      if (useStore.getState().user?.id !== ownerId) return
      setState(previous => ({ ...previous, settings }))
      toast(t('MCP settings saved'))
    } catch { if (useStore.getState().user?.id === ownerId) setError(t('Could not save MCP settings.')) }
    finally { setBusy(false) }
  }
  const revoke = grant => confirmSheet({
    title: t('Revoke MCP access?'),
    message: t('Disconnect {0} from your OpenGym account. It must request your permission again.', grant.clientName || grant.clientId),
    confirmText: t('Revoke access'), danger: true,
    onConfirm: async () => {
      if (busy || useStore.getState().user?.id !== ownerId) return
      setBusy(true); setError('')
      try {
        await api('/api/mcp/connections/revoke', { method: 'POST', body: JSON.stringify({ grantId: grant.grantId }) })
        if (useStore.getState().user?.id !== ownerId) return
        setState(previous => ({ ...previous, connections: previous.connections.filter(item => item.grantId !== grant.grantId) }))
        toast(t('MCP access revoked'))
      } catch { if (useStore.getState().user?.id === ownerId) setError(t('Could not revoke access. Reload and try again.')) }
      finally { setBusy(false) }
    },
  })
  return <>
    <Section title={t('MCP connection')}>
      {!signedIn ? <Row icon="personCircle" title={t('Sign in to manage MCP connections.')} />
        : !loaded ? <div className="mcp-content" role={error ? 'alert' : 'status'}>{error || t('Loading…')}</div> : <>
          <Row icon="cloud" title={t('Remote MCP')} value={loaded.settings.enabled ? t('On') : t('Off')}
            subtitle={!loaded.settings.enabled ? t('Remote MCP is disabled on this server.') : t('Connect an AI client using OAuth; never share your OpenGym session.')}>
            {user.admin && <Switch checked={loaded.settings.enabled} disabled={busy}
              aria-label={t('Enable remote MCP')} onChange={toggle} />}
          </Row>
          <Row icon="link" title={t('MCP URL')} subtitle={<span className="mcp-value" dir="ltr">{loaded.settings.url}</span>}
            onClick={async () => toast(await copyText(loaded.settings.url) ? t('Copied') : t('Could not copy. Select and copy the value.'))} />
        </>}
    </Section>
    <Section title={t('Connect ChatGPT or another MCP client')}>
      <div className="mcp-content">
        <p>{t('Add the MCP URL in your AI client, sign in to OpenGym and approve the requested permissions. OAuth registration is automatic.')}</p>
        {signedIn && user.admin && <p className="muted small">{t('The switch applies immediately. Turning MCP off preserves connections but blocks new requests.')}</p>}
      </div>
    </Section>
    {loaded && <Section title={t('Your MCP connections')} footer={t('Connections stay active until you revoke access here. Signing out of OpenGym does not disconnect them.')}>
      {!loaded.connections.length ? <Row title={t('No MCP connections yet.')} /> : loaded.connections.map(grant =>
        <Row key={grant.grantId} icon="shield" title={grant.clientName || grant.clientId}
          subtitle={<><span className="mcp-value" dir="ltr">{grant.clientId}</span><br />
            {new Date(grant.created).toLocaleString(dateLocale())}<br />{grant.scopes.map(scope => t(scopeLabels[scope] || scope)).join(' · ')}</>}>
          <Button variant="danger" disabled={busy} onClick={() => revoke(grant)}>{t('Revoke access')}</Button>
        </Row>)}
    </Section>}
    {error && loaded && <div className="mcp-content" role="alert">{error}</div>}
    {signedIn && <Button icon="reset" disabled={busy} onClick={() => setRevision(value => value + 1)}>{t('Reload')}</Button>}
  </>
}
