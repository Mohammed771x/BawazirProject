import { RotateCcw } from 'lucide-react'
import { useAuth } from '../auth/AuthContext'
import { useFilters } from '../lib/filters'
import { SKILL_LABEL } from '../lib/format'

const RANGES = [
  { days: 1, label: 'اليوم' },
  { days: 7, label: '7 أيام' },
  { days: 30, label: '30 يومًا' },
  { days: 90, label: '90 يومًا' },
]

/**
 * The global filters (admin brief §29): one row, above everything, and a reset.
 */
export function FilterBar() {
  const { meta } = useAuth()
  const { filters, set, reset, active } = useFilters()
  const custom = !!filters.from

  return (
    <div className="filters" role="group" aria-label="الفلاتر">
      <div className="tabs" role="radiogroup" aria-label="الفترة">
        {RANGES.map((r) => (
          <button key={r.days} className={`tab ${!custom && filters.days === r.days ? 'active' : ''}`} onClick={() => set({ days: r.days })}>{r.label}</button>
        ))}
        <button className={`tab ${custom ? 'active' : ''}`} onClick={() => !custom && set({ from: isoDay(-29), to: isoDay(0) })}>مخصص</button>
      </div>
      {custom && (
        <span className="row" style={{ gap: 4 }}>
          <input className="input ltr" type="date" aria-label="من" value={filters.from} max={filters.to} onChange={(e) => set({ from: e.target.value })} />
          <span className="muted">—</span>
          <input className="input ltr" type="date" aria-label="إلى" value={filters.to} min={filters.from} onChange={(e) => set({ to: e.target.value, from: filters.from })} />
        </span>
      )}
      <select className="select" aria-label="المهارة" value={filters.skill ?? ''} onChange={(e) => set({ skill: e.target.value })}>
        <option value="">كل المهارات</option>
        {Object.entries(SKILL_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
      </select>
      <select className="select" aria-label="المستوى" value={filters.level ?? ''} onChange={(e) => set({ level: e.target.value })}>
        <option value="">كل المستويات</option>
        {(meta?.levels ?? []).map((l) => <option key={l} value={l}>{l}</option>)}
      </select>
      <select className="select" aria-label="الاهتمام" value={filters.interest ?? ''} onChange={(e) => set({ interest: e.target.value })}>
        <option value="">كل الاهتمامات</option>
        {(meta?.interests ?? []).map((i) => <option key={i.interest} value={i.interest}>{i.interest} ({i.count})</option>)}
      </select>
      <select className="select" aria-label="النشاط" value={filters.status ?? ''} onChange={(e) => set({ status: e.target.value })}>
        <option value="">نشطون وغير نشطين</option>
        <option value="active">النشطون</option>
        <option value="inactive">غير النشطين</option>
      </select>
      <select className="select" aria-label="Cohort" value={filters.segment ?? ''} onChange={(e) => set({ segment: e.target.value })}>
        <option value="">كل الـCohorts</option>
        {(meta?.segments ?? []).filter((s) => s.key !== 'all').map((s) => <option key={s.key} value={s.key}>{s.label}</option>)}
      </select>
      {active > 0 && (
        <button className="btn ghost sm" onClick={reset}><RotateCcw /> إعادة الضبط</button>
      )}
    </div>
  )
}

function isoDay(offset: number) {
  const d = new Date()
  d.setDate(d.getDate() + offset)
  return d.toISOString().slice(0, 10)
}
