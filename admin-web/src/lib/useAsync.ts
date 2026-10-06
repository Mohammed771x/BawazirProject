import { useCallback, useEffect, useRef, useState } from 'react'

export interface AsyncState<T> {
  data: T | null
  error: Error | null
  loading: boolean
  reload: () => void
}

/**
 * Loads `fn` whenever `deps` change; the latest call wins.
 *
 * Keeps the previous data on screen while a filter change reloads, so the
 * page dims rather than flashing back to skeletons on every click.
 */
export function useAsync<T>(fn: () => Promise<T>, deps: unknown[]): AsyncState<T> {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<Error | null>(null)
  const [loading, setLoading] = useState(true)
  const [tick, setTick] = useState(0)
  const call = useRef(0)

  useEffect(() => {
    const id = ++call.current
    setLoading(true)
    setError(null)
    fn()
      .then((d) => { if (id === call.current) setData(d) })
      .catch((e: Error) => { if (id === call.current) setError(e) })
      .finally(() => { if (id === call.current) setLoading(false) })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick])

  const reload = useCallback(() => setTick((t) => t + 1), [])
  return { data, error, loading, reload }
}
