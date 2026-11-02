import { Info } from 'lucide-react'
import { api } from '../api'
import type { NotificationsPage as Data } from '../api/types'
import { BarChart, ChartFrame, LineChart, seriesColor } from '../components/charts/Charts'
import { UserTable } from '../components/UserTable'
import { Card, Empty, KpiTile, Loader } from '../components/ui'
import { filterKey, useFilters } from '../lib/filters'
import { formatDate, formatValue } from '../lib/format'
import { useAsync } from '../lib/useAsync'

const KIND_AR: Record<string, string> = {
  UNKNOWN: 'غير معروف',
}

/**
 * The reminder journey (brief page 8, ADR-132): planned → opened → lesson
 * started → lesson finished.
 *
 * Reminders are alarms the phone sets for itself (ADR-076), so there is no
 * delivery rate here and the page says why instead of drawing one. The
 * conversion figures are time links — "started a lesson within N minutes" —
 * and are labelled as such.
 */
export function NotificationsPage() {
  const { filters } = useFilters()
  const state = useAsync(() => api.notifications(filters), [filterKey(filters)])
  return (
    <>
      <div className="page-head">
        <div>
          <h1>الإشعارات</h1>
          <p>من التذكير المخطط إلى التمرين المكتمل. العودة بعد التذكير ارتباط زمني — لا دليل على أن التذكير هو السبب.</p>
        </div>
      </div>
      <Loader state={state}>{(d) => <Body d={d} />}</Loader>
    </>
  )
}

function Body({ d }: { d: Data }) {
  const top = Math.max(1, d.journey[0]?.count ?? 1)
  return (
    <>
      <div className="note row" style={{ alignItems: 'start' }}>
        <Info size={16} style={{ flexShrink: 0, marginTop: 3 }} />
        <ul style={{ margin: 0, paddingInlineStart: 18 }}>{d.notes.map((n, i) => <li key={i} style={{ fontSize: 13 }}>{n}</li>)}</ul>
      </div>
      <div className="kpis">{d.kpis.slice(0, 4).map((k) => <KpiTile key={k.key} kpi={k} />)}</div>
      <div className="kpis" style={{ gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' }}>{d.kpis.slice(4).map((k) => <KpiTile key={k.key} kpi={k} />)}</div>

      <div className="grid g-2">
        <Card title="رحلة التذكير" subtitle="كل مرحلة من المرحلة التي قبلها">
          <div className="stack">
            {d.journey.map((s, i) => {
              const prev = i > 0 ? d.journey[i - 1].count : null
              return (
                <div key={s.key}>
                  <div className="row between" style={{ fontSize: 13 }}>
                    <span>{s.label}</span>
                    <span className="num">{formatValue(s.count, 'int')}{prev ? <span className="muted"> · {formatValue(prev > 0 ? s.count / prev : null, 'pct')}</span> : null}</span>
                  </div>
                  <div className="stage-bar" style={{ height: 10 }}><span style={{ width: `${(s.count / top) * 100}%`, background: seriesColor(0), display: 'block', height: '100%', borderRadius: 6 }} /></div>
                </div>
              )
            })}
          </div>
        </Card>
        <Card title="حسب نوع التذكير" subtitle="النوع عند الفتح مستنتج من آخر موعد مخطط قبله" flush>
          {d.byKind.length === 0 ? <Empty title="لا تذكيرات في الفترة" /> : (
            <table className="table">
              <thead><tr><th>النوع</th><th className="n">مخطط</th><th className="n">فُتح</th><th className="n">→ تمرين</th><th className="n">التحويل</th></tr></thead>
              <tbody>{d.byKind.map((k) => (
                <tr key={k.kind}>
                  <td className="ltr" style={{ textAlign: 'start' }}>{KIND_AR[k.kind] ?? k.kind}</td>
                  <td className="n">{k.planned}</td><td className="n">{k.opened}</td><td className="n">{k.converted}</td>
                  <td className="n">{formatValue(k.opened > 0 ? k.converted / k.opened : null, 'pct')}</td>
                </tr>
              ))}</tbody>
            </table>
          )}
        </Card>
      </div>

      <div className="grid g-2">
        <Card title="الفتح والتحويل يوميًا">
          {d.trend.every((t) => t.opened === 0) ? <Empty title="لا فتح تذكير في الفترة" /> : (
            <ChartFrame labels={d.trend.map((t) => formatDate(t.date))} unit="int"
              series={[{ name: 'فُتح', values: d.trend.map((t) => t.opened) }, { name: 'بدأ تمرينًا', values: d.trend.map((t) => t.converted) }]}>
              <LineChart labels={d.trend.map((t) => formatDate(t.date))} unit="int"
                series={[{ name: 'فُتح', values: d.trend.map((t) => t.opened), color: seriesColor(0) }, { name: 'بدأ تمرينًا', values: d.trend.map((t) => t.converted), color: seriesColor(1) }]} />
            </ChartFrame>
          )}
        </Card>
        <Card title="ساعة فتح التذكير" subtitle="بتوقيت التقارير">
          {d.byHour.every((h) => h === 0) ? <Empty title="لا بيانات" /> : (
            <BarChart labels={d.byHour.map((_, h) => String(h).padStart(2, '0'))} unit="int" series={[{ name: 'مرات الفتح', values: d.byHour }]} />
          )}
        </Card>
      </div>

      <Card flush title="يفتحون التذكير ولا يتعلمون" subtitle={`${d.users.length} مستخدمًا — اضغط لفتح User 360`}>
        <UserTable rows={d.users} evidenceLabel="الدليل" compact empty="لا أحد في هذه الفترة" />
      </Card>
    </>
  )
}
