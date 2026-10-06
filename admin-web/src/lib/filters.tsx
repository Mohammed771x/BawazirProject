import { useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import type { Filters } from '../api/types'

const KEYS = ['days', 'from', 'to', 'level', 'skill', 'interest', 'status', 'segment'] as const

/**
 * The global filters (admin brief §29), kept in the URL.
 *
 * In the URL rather than in memory, so a filtered view is a link: an admin can
 * send "Speaking, B1, last 7 days" to someone and they see exactly that.
 */
export function useFilters() {
  const [params, setParams] = useSearchParams()

  const filters = useMemo<Filters>(() => {
    const f: Filters = {}
    for (const k of KEYS) {
      const v = params.get(k)
      if (!v) continue
      if (k === 'days') f.days = Number(v)
      else f[k] = v
    }
    if (!f.days && !f.from) f.days = 30
    return f
  }, [params])

  const set = (patch: Partial<Filters>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined || v === null || v === '') next.delete(k)
      else next.set(k, String(v))
    }
    if (patch.days) { next.delete('from'); next.delete('to') }
    if (patch.from) next.delete('days')
    setParams(next, { replace: true })
  }

  const reset = () => {
    const next = new URLSearchParams()
    // Page-local parameters (an open tab, a drawer) survive a filter reset.
    for (const [k, v] of params) if (!(KEYS as readonly string[]).includes(k)) next.set(k, v)
    setParams(next, { replace: true })
  }

  const active = KEYS.filter((k) => k !== 'days' && params.get(k)).length + (params.get('days') && params.get('days') !== '30' ? 1 : 0)

  return { filters, set, reset, active, params, setParams }
}

/** The filter key a page depends on, for `useAsync` deps. */
export const filterKey = (f: Filters) => JSON.stringify(f)
