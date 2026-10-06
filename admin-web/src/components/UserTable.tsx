import { ChevronLeft } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { UserRow } from '../api/types'
import { FLAG_LABEL, levelLabel, relative } from '../lib/format'
import { Empty } from './ui'

const STATUS: Record<UserRow['status'], [string, string]> = {
  active: ['good', 'نشط'], inactive: ['', 'غير نشط'], new: ['brand', 'جديد'],
}

export const initials = (name: string) => name.trim().split(/\s+/).slice(0, 2).map((p) => p[0]).join('')

type SortKey = 'name' | 'lastActiveAt' | 'activeDays' | 'sessions' | 'words' | 'mastered' | 'joinedAt'

/**
 * Learners, as every list in the admin area shows them — and the way into User 360.
 */
export function UserTable({ rows, evidenceLabel, compact, empty }: {
  rows: UserRow[]; evidenceLabel?: string; compact?: boolean; empty?: string
}) {
  const navigate = useNavigate()
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 }>({ key: 'lastActiveAt', dir: -1 })
  const sorted = useMemo(() => {
    const val = (r: UserRow) => {
      const v = r[sort.key]
      return typeof v === 'string' ? (sort.key === 'name' ? v : new Date(v).getTime()) : v ?? 0
    }
    return [...rows].sort((a, b) => (val(a) > val(b) ? 1 : val(a) < val(b) ? -1 : 0) * sort.dir)
  }, [rows, sort])

  if (rows.length === 0) return <Empty title={empty ?? 'لا يوجد مستخدمون'} text="جرّب توسيع الفترة أو إزالة بعض الفلاتر." />

  const th = (key: SortKey, label: string, numeric = false) => (
    <th className={`sortable ${numeric ? 'n' : ''}`} onClick={() => setSort((s) => ({ key, dir: s.key === key ? (s.dir === 1 ? -1 : 1) : -1 }))}
      aria-sort={sort.key === key ? (sort.dir === 1 ? 'ascending' : 'descending') : 'none'}>
      {label}{sort.key === key ? (sort.dir === 1 ? ' ↑' : ' ↓') : ''}
    </th>
  )

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            {th('name', 'المستخدم')}
            {evidenceLabel !== undefined && <th>{evidenceLabel || 'الدليل'}</th>}
            {th('lastActiveAt', 'آخر نشاط')}
            {!compact && th('activeDays', 'أيام النشاط', true)}
            {!compact && th('sessions', 'Sessions', true)}
            {th('words', 'الكلمات', true)}
            {th('mastered', 'متقنة', true)}
            {!compact && <th>المستوى</th>}
            <th>الحالة</th>
            <th aria-label="فتح"></th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((r) => (
            <tr key={r.id} className="clickable" onClick={() => navigate(`/users/${r.id}`)}>
              <td>
                <div className="user-cell">
                  <span className="avatar">{initials(r.name)}</span>
                  <div style={{ minWidth: 0 }}>
                    <div style={{ fontWeight: 500 }}>{r.name}</div>
                    <div className="sub">{r.email}</div>
                  </div>
                </div>
              </td>
              {evidenceLabel !== undefined && <td className="text-2" style={{ fontSize: 12.5 }}>{r.evidence ?? '—'}</td>}
              <td className="text-2" style={{ whiteSpace: 'nowrap' }}>{relative(r.lastActiveAt)}</td>
              {!compact && <td className="n">{r.activeDays}</td>}
              {!compact && <td className="n">{r.sessions}</td>}
              <td className="n">{r.words}</td>
              <td className="n">{r.mastered}</td>
              {!compact && <td className="ltr">{levelLabel(r.level)}</td>}
              <td>
                <div className="row wrap" style={{ gap: 4 }}>
                  <span className={`badge ${STATUS[r.status][0]}`}>{STATUS[r.status][1]}</span>
                  {!compact && r.flags.slice(0, 2).map((f) => <span key={f} className="badge warn">{FLAG_LABEL[f] ?? f}</span>)}
                </div>
              </td>
              <td><ChevronLeft size={16} className="muted" /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
