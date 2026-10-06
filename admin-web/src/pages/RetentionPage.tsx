import { useState } from 'react'
import { api } from '../api'
import type { Retention } from '../api/types'
import { ChartFrame, LineChart, SKILL_COLOR, seriesColor } from '../components/charts/Charts'
import { Card, Empty, KpiTile, Loader, Tabs } from '../components/ui'
import { filterKey, useFilters } from '../lib/filters'
import { formatDate, formatValue, skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'
import { Review } from './LearningPage'

export function RetentionPage() {
  const { filters } = useFilters()
  const state = useAsync(() => api.retention(filters), [filterKey(filters)])
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Retention</h1>
          <p>هل يعود المستخدمون؟ وهل يتذكرون الكلمات؟ — بيانات حقيقية لتطوير خوارزمية التعلم، لا افتراض أن الجدولة الحالية مثالية.</p>
        </div>
      </div>
      <Loader state={state}>{(d) => <Body d={d} />}</Loader>
    </>
  )
}

function Body({ d }: { d: Retention }) {
  return (
    <>
      <div className="kpis">{d.kpis.slice(0, 4).map((k) => <KpiTile key={k.key} kpi={k} />)}</div>
      <RecallCard d={d} />
      <div className="grid g-main">
        <CohortCard d={d} />
        <Card title="Recovery After Failure" subtitle="كلمات فشلت في مهارة ثم نجحت فيها لاحقًا">
          {d.recovery.failed === 0 ? <Empty title="لا حالات فشل في الفترة" /> : (
            <div className="stack lg">
              <div><div className="kpi-value">{formatValue(d.recovery.rate, 'pct')}</div><div className="muted">{d.recovery.recovered} من {d.recovery.failed} تعافت</div></div>
              <div className="muted">الوسيط حتى التعافي: <b className="num" style={{ color: 'var(--text)' }}>{formatValue(d.recovery.medianDays, 'num')} يوم</b></div>
              <div className="row" style={{ gap: 24 }}>
                {d.kpis.slice(4).map((k) => <div key={k.key}><div className="muted" style={{ fontSize: 12 }}>{k.label}</div><div style={{ fontSize: 20, fontWeight: 600 }}>{formatValue(k.value, k.format)}</div></div>)}
              </div>
            </div>
          )}
        </Card>
      </div>
      <div className="section-title">Weekly Review</div>
      <Review data={d} />
    </>
  )
}

/**
 * Time since the previous exposure against the actual recall result — the
 * evidence the scheduling rule should eventually be tuned on.
 */
function RecallCard({ d }: { d: Retention }) {
  const [view, setView] = useState<'all' | 'skills' | 'review'>('all')
  const labels = d.recall.curve.map((c) => c.label)
  const series = view === 'skills'
    ? d.recall.bySkill.filter((s) => s.curve.some((c) => c.attempts > 0)).map((s) => ({ name: skillLabel(s.skill), values: s.curve.map((c) => (c.attempts >= 3 ? c.success : null)), color: SKILL_COLOR[s.skill] }))
    : [{ name: 'نجاح التذكر', values: (view === 'review' ? d.recall.review : d.recall.curve).map((c) => (c.attempts > 0 ? c.success : null)), color: seriesColor(0) }]
  const counts = (view === 'review' ? d.recall.review : d.recall.curve)
  return (
    <Card title="احتمال التذكر مع الوقت" subtitle={`نجاح المحاولة الأولى حسب الوقت منذ آخر لقاء بالكلمة · ${d.recall.samples} محاولة تذكّر · الفجوة المضبوطة حاليًا ${d.recall.configuredGapDays} أيام`}
      actions={<Tabs value={view} onChange={setView} items={[{ value: 'all', label: 'الكل' }, { value: 'skills', label: 'لكل مهارة' }, { value: 'review', label: 'Weekly Review' }]} />}>
      {d.recall.samples === 0 ? <Empty title="لا محاولات تذكّر في الفترة" /> : (
        <ChartFrame labels={labels} series={series} unit="pct"
          note={<>عدد المحاولات في كل فترة: {counts.map((c) => `${c.label}: ${c.attempts}`).join(' · ')}. {view === 'skills' && 'الفترات بأقل من 3 محاولات لا تُرسم.'} المتعلمون الذين قُدّم جدولهم للاختبار مستبعدون.</>}>
          <LineChart labels={labels} series={series} unit="pct" height={250} area={view !== 'skills'} />
        </ChartFrame>
      )}
    </Card>
  )
}

function CohortCard({ d }: { d: Retention }) {
  const cell = (v: number | null) => (v === null ? 'transparent' : `var(--seq-${Math.min(4, Math.max(0, Math.ceil(v * 5) - 1))})`)
  const ink = (v: number | null) => (v !== null && v >= 0.6 ? '#fff' : 'var(--text)')
  const rows = d.cohorts.filter((c) => c.size > 0)
  return (
    <Card title="Cohorts أسبوعية" subtitle="نسبة من عادوا في كل أسبوع بعد أسبوع التسجيل">
      {rows.length === 0 ? <Empty title="لا تسجيلات في آخر 8 أسابيع" /> : (
        <div className="table-wrap">
          <table className="cohort">
            <thead><tr><th style={{ textAlign: 'start' }}>أسبوع التسجيل</th><th>العدد</th>{d.cohorts[0].weeks.map((_, i) => <th key={i}>أ{i + 1}</th>)}</tr></thead>
            <tbody>
              {rows.map((c) => (
                <tr key={c.cohort}>
                  <td className="label">{formatDate(c.cohort)}</td>
                  <td className="num">{c.size}</td>
                  {c.weeks.map((v, i) => <td key={i} style={{ background: cell(v), color: ink(v) }}>{v === null ? '' : `${Math.round(v * 100)}%`}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

