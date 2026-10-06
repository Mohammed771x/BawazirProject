import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, isMock, onSignedOut } from '../api'
import type { Meta } from '../api/types'

interface Auth {
  meta: Meta | null
  ready: boolean
  signIn(email: string, password: string): Promise<void>
  signOut(): Promise<void>
}

const AuthContext = createContext<Auth | null>(null)

/**
 * Who is signed in, decided by the server.
 *
 * The route guard here is a convenience. The protection is the API: every
 * `/api/admin/intel` route checks the role itself and answers 403 to anyone
 * else (docs/07-SECURITY.md §3) — hiding a page is not authorization.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [meta, setMeta] = useState<Meta | null>(null)
  const [ready, setReady] = useState(false)

  const loadMeta = useCallback(async () => {
    try {
      setMeta(await api.meta())
    } catch {
      setMeta(null)
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    onSignedOut(() => setMeta(null))
    if (isMock || api.hasSession?.()) void loadMeta()
    else setReady(true)
  }, [loadMeta])

  const signIn = async (email: string, password: string) => {
    await api.login(email, password)
    await loadMeta()
  }

  const signOut = async () => {
    await api.logout()
    setMeta(null)
  }

  return <AuthContext.Provider value={{ meta, ready, signIn, signOut }}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth outside AuthProvider')
  return ctx
}
