import { LogIn } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useAuth } from './AuthContext'

export function LoginPage() {
  const { signIn } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await signIn(email.trim(), password)
    } catch (err) {
      setError((err as Error).message || 'تعذر تسجيل الدخول.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <form className="login-card" onSubmit={submit}>
        <div className="brand" style={{ padding: 0 }}>
          <div className="brand-mark">W</div>
          <div>
            <div className="brand-title">WordOS Admin</div>
            <div className="brand-sub">Product Intelligence & Learning Analytics</div>
          </div>
        </div>
        <div>
          <h1 style={{ fontSize: 18 }}>تسجيل الدخول</h1>
          <p className="muted" style={{ margin: '4px 0 0', fontSize: 13 }}>للمالك والمحللين فقط. حسابات المتعلمين لا تملك صلاحية الدخول.</p>
        </div>
        {error && <div className="alert" role="alert">{error}</div>}
        <div className="field">
          <label htmlFor="email">البريد الإلكتروني</label>
          <input id="email" className="input ltr" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="password">كلمة المرور</label>
          <input id="password" className="input ltr" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </div>
        <button className="btn primary" disabled={busy} style={{ height: 40 }}>
          <LogIn /> {busy ? 'جارٍ الدخول…' : 'دخول'}
        </button>
      </form>
    </div>
  )
}
