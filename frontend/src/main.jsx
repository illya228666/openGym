import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App.jsx'
import { MOBILE } from './lib/mobile.js'
import { useStore } from './store/useStore.js'
import { startMediaSync } from './lib/media-sync.js'
import { startNativeKeyboard } from './lib/native-keyboard.js'
import './index.css'

// App.jsx restores per-route scroll itself; the browser's own attempt races it.
if ('scrollRestoration' in history) history.scrollRestoration = 'manual'

createRoot(document.getElementById('root')).render(
  <StrictMode><App /></StrictMode>
)

// The photos and videos of custom exercises, in every build (the phone and the demo included):
// uploads of what the server lacks, the local clean-up, and the plan's files kept offline.
startMediaSync(useStore)

// Android 15 does not resize the page for the soft keyboard; the app says how much it covers and
// this keeps the focused field above it. Idle everywhere else.
startNativeKeyboard()

// Not in the mobile build: the native shell already serves everything from disk.
if (!MOBILE && 'serviceWorker' in navigator && location.protocol === 'https:') {
  // Installed mobile web apps can keep an old JS bundle running after a new worker activates.
  // Check for updates when reopened and reload once if the page is safe to refresh.
  const sw = navigator.serviceWorker
  const hadController = !!sw.controller
  let pendingRefresh = false
  let refreshed = false
  const refreshIfSafe = () => {
    if (!pendingRefresh || refreshed || document.visibilityState !== 'visible') return
    // Never interrupt an in-progress workout just to pick up a new version.
    if (useStore.getState().S?.active) return
    refreshed = true
    window.location.reload()
  }
  sw.addEventListener('controllerchange', () => {
    if (!hadController) return // first install: the current page already has its own bundle
    pendingRefresh = true
    refreshIfSafe()
  })
  sw.register('sw.js').then(reg => {
    reg.update().catch(() => {})
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState !== 'visible') return
      refreshIfSafe()
      reg.update().catch(() => {})
    })
  }).catch(() => {})
  // The plan's exercise media, kept by the worker for a workout opened without a network (#281).
  // It only fetches ahead while the page runs as the installed app; a tab keeps what it has shown.
  import('./lib/media-prefetch.js').then(m => m.startMediaPrefetch(useStore)).catch(() => {})
}
