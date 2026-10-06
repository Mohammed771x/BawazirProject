import { AlertTriangle, ArrowDownRight, ArrowUpRight, Info, Inbox, Minus, RefreshCw, X } from 'lucide-react'
import { useEffect, type ReactNode } from 'react'
import type { Format, Kpi } from '../api/types'
import { change, formatChange, formatValue } from '../lib/format'

export function Card({ title, subtitle, actions, children, className = '', flush }: {
  title?: ReactNode; subtitle?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; flush?: boolean
}) {
  return (
    <section className={`card ${flush ? 'flush' : ''} ${className}`}>
      {(title || actions) && (
        <div className="card-head" style={flush ? { padding: '18px 20px 0' } : undefined}>
          <div>
            {title && <h2>{title}</h2>}
            {subtitle && <p>{subtitle}</p>}
          </div>
          {actions && <div className="actions">{actions}</div>}
        </div>
      )}
      {children}
    </section>
  )
}

export function Help({ text }: { text?: string | null }) {
  if (!text) return null
  return (
    <span className="help" tabIndex={0} aria-label={text}>
      <Info />
      <span className="tip" role="tooltip">{text}</span>
    </span>
  )
}

/**
 * Change against the previous period.
 *
 * `goodWhenUp` decides the colour, not the arrow: abandonment rising is red.
 */
export function Delta({ value, previous, format, goodWhenUp = true }: {
  value: number | null; previous: number | null; format: Format; goodWhenUp?: boolean
}) {
  const d = change(value, previous, format)
  if (d === null) return null
  const flat = Math.abs(d) < 0.005
  const good = flat ? null : (d > 0) === goodWhenUp
  const Icon = flat ? Minus : d > 0 ? ArrowUpRight : ArrowDownRight
  return (
    <span className={`delta ${flat ? 'flat' : good ? 'up' : 'down'}`} title="مقارنة بالفترة السابقة">
      <Icon />
      {formatChange(d, format)}
    </span>
  )
}

const BAD_WHEN_UP = new Set(['abandonment', 'failRate', 'dropOff', 'overdue', 'recall_failure'])

export function KpiTile({ kpi, onClick, goodWhenUp }: { kpi: Kpi; onClick?: () => void; goodWhenUp?: boolean }) {
  return (
    <div className={`kpi ${onClick ? 'clickable' : ''}`} onClick={onClick} role={onClick ? 'button' : undefined} tabIndex={onClick ? 0 : undefined}>
      <div className="kpi-label">{kpi.label}<Help text={kpi.hint} /></div>
      <div className="kpi-value">{formatValue(kpi.value, kpi.format, true)}</div>
      <div className="kpi-foot">
        <Delta value={kpi.value} previous={kpi.previous} format={kpi.format} goodWhenUp={goodWhenUp ?? !BAD_WHEN_UP.has(kpi.key)} />
        {kpi.previous !== null && <span>السابق {formatValue(kpi.previous, kpi.format, true)}</span>}
      </div>
    </div>
  )
}

export function Skeleton({ h = 16, w = '100%', r }: { h?: number; w?: number | string; r?: number }) {
  return <div className="skeleton" style={{ height: h, width: w, borderRadius: r }} />
}

export function PageSkeleton() {
  return (
    <div className="stack lg" aria-busy="true" aria-label="جارٍ التحميل">
      <div className="kpis">{Array.from({ length: 4 }, (_, i) => <div key={i} className="kpi"><Skeleton h={12} w="50%" /><Skeleton h={28} w="40%" /><Skeleton h={10} w="60%" /></div>)}</div>
      <div className="grid g-main">
        <div className="card"><Skeleton h={14} w="30%" /><div style={{ height: 14 }} /><Skeleton h={220} /></div>
        <div className="card"><Skeleton h={14} w="40%" /><div style={{ height: 14 }} /><Skeleton h={220} /></div>
      </div>
    </div>
  )
}

export function Empty({ title = 'لا توجد بيانات', text, icon }: { title?: string; text?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="state">
      {icon ?? <Inbox />}
      <h3>{title}</h3>
      {text && <p>{text}</p>}
    </div>
  )
}

export function ErrorState({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  return (
    <div className="state error" role="alert">
      <AlertTriangle />
      <h3>تعذر تحميل البيانات</h3>
      <p>{error.message}</p>
      {onRetry && <button className="btn sm" onClick={onRetry}><RefreshCw /> إعادة المحاولة</button>}
    </div>
  )
}

/** Wraps a page body in its loading / error / data states. */
export function Loader<T>({ state, children, skeleton }: {
  state: { data: T | null; error: Error | null; loading: boolean; reload: () => void }
  children: (data: T) => ReactNode
  skeleton?: ReactNode
}) {
  if (state.error && !state.data) return <div className="card"><ErrorState error={state.error} onRetry={state.reload} /></div>
  if (!state.data) return <>{skeleton ?? <PageSkeleton />}</>
  return <div style={{ opacity: state.loading ? 0.6 : 1, transition: 'opacity .15s' }} className="stack lg">{children(state.data)}</div>
}

export function Tabs<T extends string>({ value, onChange, items }: {
  value: T; onChange: (v: T) => void; items: { value: T; label: ReactNode }[]
}) {
  return (
    <div className="tabs" role="tablist">
      {items.map((i) => (
        <button key={i.value} role="tab" aria-selected={value === i.value} className={`tab ${value === i.value ? 'active' : ''}`} onClick={() => onChange(i.value)}>
          {i.label}
        </button>
      ))}
    </div>
  )
}

export function Drawer({ open, onClose, title, children, actions }: {
  open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; actions?: ReactNode
}) {
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])
  if (!open) return null
  return (
    <>
      <div className="overlay" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-modal="true">
        <div className="drawer-head">
          <h2 style={{ fontSize: 16 }}>{title}</h2>
          <div className="row">{actions}<button className="btn ghost sm" onClick={onClose} aria-label="إغلاق"><X /></button></div>
        </div>
        <div className="drawer-body">{children}</div>
      </aside>
    </>
  )
}

export function SeverityBadge({ severity }: { severity: string }) {
  const map: Record<string, [string, string]> = { high: ['bad', 'مرتفع'], medium: ['warn', 'متوسط'], low: ['', 'منخفض'], none: ['', 'لا بيانات'] }
  const [cls, label] = map[severity] ?? ['', severity]
  return <span className={`badge ${cls}`}>{label}</span>
}

export const CATEGORY_TONE: Record<string, string> = { learning: 'brand', ux: 'warn', content: 'bad', ai: '', unknown: '' }
export const CATEGORY_NAME: Record<string, string> = { learning: 'تعلم', ux: 'UX', content: 'محتوى', ai: 'AI', unknown: 'غير مصنف', feedback: 'Feedback', users: 'المستخدمون' }
