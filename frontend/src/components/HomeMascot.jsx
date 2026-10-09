import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'

const actions = ['idle-curious.gif', 'wave.gif', 'jump.gif']
const assetBase = `${import.meta.env.BASE_URL}sumrak/`

const randomBetween = (min, max) => min + Math.random() * (max - min)

export default function HomeMascot() {
  const [action, setAction] = useState('idle.gif')
  const [failedAction, setFailedAction] = useState(null)

  useEffect(() => {
    let showTimer
    let disposed = false

    const scheduleAction = delay => {
      showTimer = window.setTimeout(() => {
        if (disposed) return
        setAction(actions[Math.floor(Math.random() * actions.length)])
        showTimer = window.setTimeout(() => {
          if (disposed) return
          setAction('idle.gif')
          scheduleAction(randomBetween(1800, 4500))
        }, randomBetween(2200, 3600))
      }, delay)
    }

    scheduleAction(randomBetween(800, 1500))
    return () => {
      disposed = true
      window.clearTimeout(showTimer)
    }
  }, [])

  // The route container animates with transforms; on some mobile browsers it
  // becomes the containing/stacking context for fixed descendants. Mount the
  // mascot at document level so it stays anchored to the actual viewport.
  if (typeof document === 'undefined') return null
  return createPortal(<img
    className="home-mascot"
    src={`${assetBase}${failedAction === action ? 'idle.png' : action}`}
    onError={() => setFailedAction(action)}
    alt=""
    aria-hidden="true"
  />, document.body)
}
