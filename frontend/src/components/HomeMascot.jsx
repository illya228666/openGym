import { useEffect, useState } from 'react'

const actions = ['idle-curious.gif', 'wave.gif', 'jump.gif']
const assetBase = `${import.meta.env.BASE_URL}sumrak/`

const randomBetween = (min, max) => min + Math.random() * (max - min)

export default function HomeMascot() {
  const [action, setAction] = useState('idle.gif')

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

  return <img
    className="home-mascot"
    src={`${assetBase}${action}`}
    alt=""
    aria-hidden="true"
  />
}
