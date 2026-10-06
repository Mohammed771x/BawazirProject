import { HttpAdminApi, type AdminApi } from './client'
import { MockAdminApi } from './mock'

/**
 * Which backend the site talks to.
 *
 * `VITE_ADMIN_MOCK=true` swaps in invented data for UI work. Anything else is
 * the real API, same-origin under `/api` — the Vite dev server proxies it to
 * the local backend, and in production the backend serves this site itself.
 */
export const isMock = import.meta.env.VITE_ADMIN_MOCK === 'true'

let signedOut: () => void = () => {}
export const onSignedOut = (handler: () => void) => {
  signedOut = handler
}

export const api: AdminApi & { hasSession?: () => boolean } = isMock
  ? new MockAdminApi()
  : new HttpAdminApi(import.meta.env.VITE_ADMIN_API_BASE ?? '/api', () => signedOut())

export type { AdminApi }
