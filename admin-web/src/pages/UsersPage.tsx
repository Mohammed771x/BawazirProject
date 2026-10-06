import { Search } from 'lucide-react'
import { useState } from 'react'
import { api } from '../api'
import type { CohortSide } from '../api/types'
import { BarChart, ChartFrame, SKILL_COLOR } from '../components/charts/Charts'
import { Card, Empty, Loader, Tabs } from '../components/ui'
import { UserTable } from '../components/UserTable'
import { useAuth } from '../auth/AuthContext'
import { filterKey, useFilters } from '../lib/filters'
import { formatValue, skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'

type Tab = 'all' | 'attention' | 'cohorts'

export function UsersPage() {
  const { params, setParams } = useFilters()
  const tab = (params.get('tab') as Tab) || 'all'
  const setTab = (t: Tab) => {
    const next = new URLSearchParams(params)
    if (t === 'all') next.delete('tab'); else next.set('tab', t)
    setParams(next, { replace: true })
  }
  return (
    <>
      <div className="page-head">
        <div>
          <h1>المستخدمون</h1>
          <p>من هم، من يحتاج إلى انتباه، وكيف تختلف المجموعات — اضغط أي مستخدم لفتح User 360.</p>
        </div>
        <Tabs value={tab} onChange={setTab} items={[
          { value: 'all', label: 'كل المستخدمين' },
          { value: 'attention', label: 'يحتاجون إلى انتباه' },
          { value: 'cohorts', label: 'مقارنة Cohorts' },
        ]} />
      </div>
      {tab === 'all' && <AllUsers />}
      {tab === 'attention' && <AttentionView />}
      {tab === 'cohorts' && <CohortCompare />}
    </>
  )
}

function AllUsers() {
  const { filters } = useFilters()
  const { meta } = useAuth()
  const [q, setQ] = useState('')
  const [query, setQuery] = useState('')
  const state = useAsync(() => api.users({ ...filters, q: query }), [filterKey(filters), query])
  return (
    <Card flush title={state.data ? `${state.data.total} مستخدمًا${state.data.segment ? ` · ${state.data.segment}` : ''}` : 'المستخدمون'}
      subtitle="كل الفلاتر في الأعلى تنطبق هنا، ومنها Cohort"
      actions={
        <form className="row" onSubmit={(e) => { e.preventDefault(); setQuery(q) }}>
          <input className="input" placeholder={meta?.canSeeContact ? 'ابحث بالاسم أو البريد أو الهاتف' : 'ابحث بالاسم'} value={q} onChange={(e) => setQ(e.target.value)} style={{ width: 240 }} />
          <button className="btn sm"><Search /> بحث</button>
        </form>
      }>
      <Loader state={state}>{(d) => <UserTable rows={d.rows} />}</Loader>
    </Card>
  )
}

function AttentionView() {
  const { filters } = useFilters()
  const state = useAsync(() => api.attention(filters), [filterKey(filters)])
  const [open, setOpen] = useState<string | null>(null)
  return (
    <Loader state={state}>{(d) => d.groups.length === 0
      ? <div className="card"><Empty title="لا أحد يحتاج إلى انتباه الآن" text="لم تنطبق أي قاعدة على أي مستخدم في النطاق الحالي." /></div>
      : (
        <>
          <p className="muted" style={{ margin: 0 }}>{d.total} مستخدمًا تنطبق عليهم قاعدة واحدة على الأقل. كل قاعدة مكتوبة بوضوح، وكل حالة معها الدليل الذي أطلقها — لا توجد درجة مخاطرة غامضة.</p>
          <div className="grid g-3">
            {d.groups.map((g) => (
              <button key={g.key} className={`kpi clickable`} style={{ textAlign: 'start', borderColor: open === g.key ? 'var(--brand)' : undefined }} onClick={() => setOpen(open === g.key ? null : g.key)}>
                <div className="kpi-label">{g.label}</div>
                <div className="kpi-value">{g.users.length}</div>
                <div className="kpi-foot">{g.description}</div>
              </button>
            ))}
          </div>
          {d.groups.filter((g) => !open || g.key === open).map((g) => (
            <Card key={g.key} flush title={g.label} subtitle={g.description}>
              <UserTable rows={g.users} evidenceLabel="الدليل" compact />
            </Card>
          ))}
        </>
      )}
    </Loader>
  )
}

function CohortCompare() {
  const { filters } = useFilters()
  const { meta } = useAuth()
  const [a, setA] = useState('all')
  const [b, setB] = useState('joined:30')
  const state = useAsync(() => api.compare(a, b, filters), [a, b, filterKey(filters)])
  const segments = meta?.segments ?? []

  return (
    <>
      <Card title="قارن بين مجموعتين" subtitle="أي Cohort مقابل أي Cohort — المقاييس نفسها على الجانبين">
        <div className="row wrap" style={{ gap: 12 }}>
          <label className="field" style={{ minWidth: 220 }}><span style={{ fontSize: 12.5 }}>المجموعة أ</span>
            <select className="select" value={a} onChange={(e) => setA(e.target.value)}>{segments.map((s) => <option key={s.key} value={s.key}>{s.label}</option>)}</select>
          </label>
          <span className="muted" style={{ marginTop: 20 }}>مقابل</span>
          <label className="field" style={{ minWidth: 220 }}><span style={{ fontSize: 12.5 }}>المجموعة ب</span>
            <select className="select" value={b} onChange={(e) => setB(e.target.value)}>{segments.map((s) => <option key={s.key} value={s.key}>{s.label}</option>)}</select>
          </label>
        </div>
      </Card>
      <Loader state={state}>{(d) => <CompareResult a={d.a} b={d.b} />}</Loader>
    </>
  )
}

function CompareResult({ a, b }: { a: CohortSide; b: CohortSide }) {
  const labels = a.skills.map((s) => skillLabel(s.skill))
  const series = [
    { name: a.label, values: a.skills.map((s) => s.firstAttemptAccuracy), color: 'var(--series-1)' },
    { name: b.label, values: b.skills.map((s) => s.firstAttemptAccuracy), color: 'var(--series-2)' },
  ]
  return (
    <div className="grid g-2">
      <Card flush title="المقاييس">
        <table className="table">
          <thead><tr><th></th><th className="n">{a.label}</th><th className="n">{b.label}</th></tr></thead>
          <tbody>
            <tr><td>عدد المستخدمين</td><td className="n">{a.size}</td><td className="n">{b.size}</td></tr>
            {a.metrics.map((m, i) => (
              <tr key={m.key}><td>{m.label}</td><td className="n">{formatValue(m.value, m.format)}</td><td className="n">{formatValue(b.metrics[i]?.value ?? null, m.format)}</td></tr>
            ))}
          </tbody>
        </table>
      </Card>
      <Card title="دقة المحاولة الأولى لكل مهارة" subtitle={a.size < 10 || b.size < 10 ? 'تنبيه: إحدى المجموعتين صغيرة، فالفرق قد لا يكون ذا معنى.' : undefined}>
        <ChartFrame labels={labels} series={series} unit="pct"><BarChart labels={labels} series={series} unit="pct" /></ChartFrame>
        <div className="row wrap" style={{ gap: 10, marginTop: 10 }}>
          {a.skills.map((s, i) => (
            <span key={s.skill} className="badge"><span className="dot" style={{ background: SKILL_COLOR[s.skill] }} />
              {skillLabel(s.skill)}: انسحاب {formatValue(s.abandonmentRate, 'pct')} / {formatValue(b.skills[i]?.abandonmentRate ?? null, 'pct')}</span>
          ))}
        </div>
      </Card>
    </div>
  )
}
