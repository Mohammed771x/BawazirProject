import type {
  AttentionGroups, AuditItem, Behavior, CohortSide, FeedbackPage, Filters, FrictionDetail,
  InquirySummary, Investigation, Learning, Meta, Overview, Retention, Session, SkillDetail,
  SystemInfo, TimelineEvent, User360, UsersList,
} from './types'

/**
 * The admin website's whole contract with the backend.
 *
 * Two implementations, swapped in `api/index.ts`: `HttpAdminApi` against the
 * real WordOS API, and `MockAdminApi` for working on the UI with no backend.
 * Pages depend on this interface only — the same split the Flutter app uses
 * (`WordOsApi` / `MockWordOsApi`).
 */
export interface AdminApi {
  login(email: string, password: string): Promise<Session>
  logout(): Promise<void>
  meta(): Promise<Meta>
  overview(f: Filters): Promise<Overview>
  learning(f: Filters): Promise<Learning>
  skill(skill: string, f: Filters): Promise<SkillDetail>
  behavior(f: Filters): Promise<Behavior>
  friction(key: string, f: Filters): Promise<FrictionDetail>
  retention(f: Filters): Promise<Retention>
  users(f: Filters & { q?: string; sort?: string }): Promise<UsersList>
  attention(f: Filters): Promise<AttentionGroups>
  user(id: string): Promise<User360>
  timeline(id: string, verbose: boolean): Promise<{ events: TimelineEvent[] }>
  compare(a: string, b: string, f: Filters): Promise<{ a: CohortSide; b: CohortSide }>
  feedback(status?: string, category?: string): Promise<FeedbackPage>
  addNote(feedbackId: string, body: string): Promise<void>
  setFeedbackHandled(feedbackId: string, handled: boolean): Promise<void>
  inquiries(): Promise<{ items: InquirySummary[] }>
  inquiry(id: string): Promise<Investigation>
  ask(section: string, question: string, f: Filters): Promise<Investigation>
  system(): Promise<SystemInfo>
  audit(): Promise<{ items: AuditItem[] }>
}

/** An error the UI can show: a code to branch on and a message to read. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  constructor(status: number, code: string, message: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

const STORAGE_KEY = 'wordos.admin.session'

/**
 * Where the session lives.
 *
 * `sessionStorage`, not `localStorage`: it dies with the tab. An admin session
 * opens every learner's history, and a token that outlives the window on a
 * shared machine is the easiest way to lose that.
 */
export const sessionStore = {
  read(): Session | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY)
      return raw ? (JSON.parse(raw) as Session) : null
    } catch {
      return null
    }
  },
  write(s: Session | null) {
    try {
      if (s) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s))
      else sessionStorage.removeItem(STORAGE_KEY)
    } catch {
      /* private mode: the session simply lasts as long as the page */
    }
  },
}

function query(params: Record<string, string | number | undefined | null>): string {
  const q = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') q.set(k, String(v))
  }
  const s = q.toString()
  return s ? `?${s}` : ''
}

export class HttpAdminApi implements AdminApi {
  private session: Session | null = sessionStore.read()
  private refreshing: Promise<boolean> | null = null
  private readonly base: string
  private readonly onSignedOut: () => void

  constructor(base: string, onSignedOut: () => void) {
    this.base = base
    this.onSignedOut = onSignedOut
  }

  private async request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
    const headers = new Headers(init.headers)
    if (init.body) headers.set('Content-Type', 'application/json')
    if (this.session) headers.set('Authorization', `Bearer ${this.session.token}`)

    let response: Response
    try {
      response = await fetch(`${this.base}${path}`, { ...init, headers })
    } catch {
      throw new ApiError(0, 'NETWORK', 'تعذر الوصول إلى الخادم. تحقق من الاتصال.')
    }

    // An expired access token is renewed once, quietly; a second 401 means the
    // session is really over.
    if (response.status === 401 && retry && this.session && (await this.refresh())) {
      return this.request<T>(path, init, false)
    }
    if (response.status === 401) {
      this.setSession(null)
      this.onSignedOut()
    }

    if (!response.ok) {
      let code = 'ERROR'
      let message = 'حدث خطأ غير متوقع.'
      try {
        const body = await response.json()
        code = body?.error?.code ?? code
        message = body?.error?.message ?? message
      } catch {
        /* not JSON */
      }
      if (response.status === 403) message = 'ليست لديك صلاحية لهذا القسم.'
      if (response.status === 429) message = 'طلبات كثيرة. انتظر قليلًا ثم حاول مجددًا.'
      if (response.status === 503) message = 'الخادم مشغول الآن. حاول بعد لحظات.'
      throw new ApiError(response.status, code, message)
    }

    if (response.status === 204) return undefined as T
    return (await response.json()) as T
  }

  private setSession(s: Session | null) {
    this.session = s
    sessionStore.write(s)
  }

  private refresh(): Promise<boolean> {
    this.refreshing ??= (async () => {
      try {
        const r = await fetch(`${this.base}/auth/refresh`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken: this.session?.refreshToken }),
        })
        if (!r.ok) return false
        this.setSession((await r.json()) as Session)
        return true
      } catch {
        return false
      } finally {
        this.refreshing = null
      }
    })()
    return this.refreshing
  }

  async login(email: string, password: string): Promise<Session> {
    const s = await this.request<Session>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }, false)
    if (s.user.role !== 'OWNER' && s.user.role !== 'ANALYST') {
      throw new ApiError(403, 'NOT_ADMIN', 'هذا الحساب لا يملك صلاحية الدخول إلى لوحة الإدارة.')
    }
    this.setSession(s)
    return s
  }

  async logout(): Promise<void> {
    const refreshToken = this.session?.refreshToken
    try {
      if (refreshToken) {
        await this.request('/auth/logout', { method: 'POST', body: JSON.stringify({ refreshToken }) }, false)
      }
    } finally {
      this.setSession(null)
    }
  }

  meta = () => this.request<Meta>('/admin/intel/meta')
  overview = (f: Filters) => this.request<Overview>(`/admin/intel/overview${query({ ...f })}`)
  learning = (f: Filters) => this.request<Learning>(`/admin/intel/learning${query({ ...f })}`)
  skill = (skill: string, f: Filters) =>
    this.request<SkillDetail>(`/admin/intel/skills/${skill}${query({ ...f })}`)
  behavior = (f: Filters) => this.request<Behavior>(`/admin/intel/behavior${query({ ...f })}`)
  friction = (key: string, f: Filters) =>
    this.request<FrictionDetail>(`/admin/intel/friction/${key}${query({ ...f })}`)
  retention = (f: Filters) => this.request<Retention>(`/admin/intel/retention${query({ ...f })}`)
  users = (f: Filters & { q?: string; sort?: string }) =>
    this.request<UsersList>(`/admin/intel/users${query({ ...f })}`)
  attention = (f: Filters) => this.request<AttentionGroups>(`/admin/intel/users/attention${query({ ...f })}`)
  user = (id: string) => this.request<User360>(`/admin/intel/users/${id}`)
  timeline = (id: string, verbose: boolean) =>
    this.request<{ events: TimelineEvent[] }>(`/admin/intel/users/${id}/timeline${query({ verbose: verbose ? 'true' : undefined })}`)
  compare = (a: string, b: string, f: Filters) =>
    this.request<{ a: CohortSide; b: CohortSide }>(`/admin/intel/cohorts/compare${query({ ...f, a, b })}`)
  feedback = (status?: string, category?: string) =>
    this.request<FeedbackPage>(`/admin/intel/feedback${query({ status, category })}`)
  addNote = async (id: string, body: string) => {
    await this.request(`/admin/intel/feedback/${id}/notes`, { method: 'POST', body: JSON.stringify({ body }) })
  }
  setFeedbackHandled = async (id: string, handled: boolean) => {
    await this.request(`/admin/intel/feedback/${id}`, { method: 'PATCH', body: JSON.stringify({ handled }) })
  }
  inquiries = () => this.request<{ items: InquirySummary[] }>('/admin/intel/inquiries')
  inquiry = (id: string) => this.request<Investigation>(`/admin/intel/inquiries/${id}`)
  ask = (section: string, question: string, filter: Filters) =>
    this.request<Investigation>('/admin/intel/inquiries', {
      method: 'POST',
      body: JSON.stringify({ section, question, filter }),
    })
  system = () => this.request<SystemInfo>('/admin/intel/system')
  audit = () => this.request<{ items: AuditItem[] }>('/admin/intel/audit')

  hasSession = () => this.session !== null
}
