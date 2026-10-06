import { ArrowRight } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api'
import type { Kpi, SkillDetail } from '../api/types'
import { BarChart, ChartFrame, HBars, LineChart, SKILL_COLOR } from '../components/charts/Charts'
import { Card, CATEGORY_NAME, CATEGORY_TONE, Empty, Help, KpiTile, Loader } from '../components/ui'
import { UserTable } from '../components/UserTable'
import { filterKey, useFilters } from '../lib/filters'
import { formatValue, levelLabel, SKILL_AR, skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'

/**
 * One skill, metric → segment → user → event (admin brief §30).
 */
export function SkillDetailPage() {
  const { skill = 'READING' } = useParams()
  const { filters } = useFilters()
  const state = useAsync(() => api.skill(skill, filters), [skill, filterKey(filters)])
  return (
    <>
      <Link to="/learning" className="row muted" style={{ fontSize: 13 }}><ArrowRight size={16} /> التعلم والمهارات</Link>
      <div className="page-head">
        <div>
          <h1 className="row"><span className="dot" style={{ background: SKILL_COLOR[skill as keyof typeof SKILL_COLOR], width: 12, height: 12 }} />{skillLabel(skill)} <span className="muted" style={{ fontWeight: 400, fontSize: 16 }}>· {SKILL_AR[skill as keyof typeof SKILL_AR]}</span></h1>
          <p>من فشل؟ في أي كلمة؟ عند أي مستوى؟ لماذا؟ متى؟ وماذا حدث بعدها؟</p>
        </div>
      </div>
      <Loader state={state}>{(d) => <Body d={d} />}</Loader>
    </>
  )
}

function Body({ d }: { d: SkillDetail }) {
  const s = d.summary
  const p = d.previous
  const kpis: Kpi[] = [
    { key: 'sessions', label: 'Sessions', value: s.sessions, previous: p.sessions, format: 'int' },
    { key: 'fa', label: 'دقة المحاولة الأولى', value: s.firstAttemptAccuracy, previous: p.firstAttemptAccuracy, format: 'pct' },
    { key: 'pass', label: 'Pass Rate', value: s.passRate, previous: p.passRate, format: 'pct', hint: 'الكلمات التي اجتازت المهارة من كل الكلمات التي حُسم أمرها' },
    { key: 'abandonment', label: 'الانسحاب', value: s.abandonmentRate, previous: p.abandonmentRate, format: 'pct' },
  ]
  const dailyLabels = d.daily.map((x) => x.date.slice(5))
  const dailySeries = [
    { name: 'نجاح', values: d.daily.map((x) => x.passed), color: 'var(--series-3)' },
    { name: 'فشل', values: d.daily.map((x) => x.failed), color: 'var(--series-2)' },
  ]
  const levelLabels = d.byLevel.map((x) => levelLabel(x.level))
  const levelSeries = [{ name: 'دقة المحاولة الأولى', values: d.byLevel.map((x) => x.firstAttemptAccuracy), color: SKILL_COLOR[d.skill] }]

  return (
    <>
      <div className="kpis">{kpis.map((k) => <KpiTile key={k.key} kpi={k} goodWhenUp={k.key !== 'abandonment'} />)}</div>
      <Card title="Content Interaction" subtitle="كيف تعامل المتعلمون مع المحتوى في هذه المهارة">
        <div className="evidence-grid">
          {d.content.map((m) => (
            <div key={m.key} className="evidence">
              <div className="l row" style={{ gap: 4 }}>{m.label}<Help text={m.hint} /></div>
              <div className="v">{formatValue(m.value, m.format)}</div>
            </div>
          ))}
          {[['الأسئلة', s.questions, 'int'], ['المحاولات', s.attempts, 'int'], ['Retry', s.retryRate, 'pct'], ['وسيط مدة الـSession', s.medianSessionMs, 'ms']].map(([l, v, f]) => (
            <div key={l as string} className="evidence"><div className="l">{l}</div><div className="v">{formatValue(v as number | null, f as 'int')}</div></div>
          ))}
        </div>
      </Card>
      <div className="grid g-2">
        <Card title="لماذا فشلوا؟" subtitle={`${d.failures.total} حالة — كل حالة تحت سبب واحد`}>
          {d.failures.reasons.length === 0 ? <Empty title="لا حالات فشل في الفترة" /> : (
            <>
              <HBars unit="int" items={d.failures.reasons.map((r) => ({ label: r.label, value: r.count, color: r.category === 'ai' ? 'var(--text-3)' : r.category === 'ux' ? 'var(--s-writing)' : r.category === 'content' ? 'var(--series-2)' : 'var(--series-1)' }))} />
              <div className="row wrap" style={{ gap: 6, marginTop: 12 }}>
                {Object.entries(d.failures.reasons.reduce<Record<string, number>>((acc, r) => ({ ...acc, [r.category]: (acc[r.category] ?? 0) + r.count }), {}))
                  .map(([c, n]) => <span key={c} className={`badge ${CATEGORY_TONE[c]}`}>{CATEGORY_NAME[c]}: {n}</span>)}
              </div>
            </>
          )}
        </Card>
        <Card title="عند أي مستوى؟" subtitle="دقة المحاولة الأولى حسب مستوى المحتوى المستخدم">
          {d.byLevel.length === 0 ? <Empty /> : (
            <ChartFrame labels={levelLabels} series={levelSeries} unit="pct" note={d.byLevel.map((x) => `${levelLabel(x.level)}: ${x.sessions} Sessions`).join(' · ')}>
              <BarChart labels={levelLabels} series={levelSeries} unit="pct" />
            </ChartFrame>
          )}
        </Card>
      </div>
      <Card title="متى؟" subtitle="النجاح والفشل يومًا بيوم">
        <ChartFrame labels={dailyLabels} series={dailySeries} unit="int"><LineChart labels={dailyLabels} series={dailySeries} area={false} /></ChartFrame>
      </Card>
      <div className="grid g-2">
        <Card title="كم محاولة احتاجت الكلمات؟">
          {d.attempts.length === 0 ? <Empty /> : <HBars unit="int" items={d.attempts.map((a) => ({ label: `${a.attempts} محاولة`, value: a.words, color: 'var(--seq-3)' }))} />}
        </Card>
        <Card title="ماذا حدث بعد الفشل؟" subtitle={`${d.next.total} كلمة فشلت في الفترة`}>
          {d.next.total === 0 ? <Empty /> : (
            <HBars unit="int" max={d.next.total} items={[
              { label: 'نجحت لاحقًا', value: d.next.recovered, color: 'var(--series-3)' },
              { label: 'ما زالت تنتظر', value: d.next.waiting, color: 'var(--seq-2)' },
              { label: 'حذفها المتعلم', value: d.next.deleted, color: 'var(--series-2)' },
            ]} />
          )}
        </Card>
      </div>
      <Card flush title="من فشل؟" subtitle="المتعلمون الذين يحملون فشل هذه المهارة — اضغط لفتح User 360">
        <UserTable rows={d.byUser} evidenceLabel="في هذه المهارة" compact empty="لم يفشل أحد في الفترة" />
      </Card>
      <div className="grid g-2">
        <Card flush title="في أي كلمة؟">
          {d.words.length === 0 ? <Empty /> : (
            <table className="table"><thead><tr><th>الكلمة</th><th className="n">فشل</th><th className="n">مستخدمون</th></tr></thead>
              <tbody>{d.words.map((w) => <tr key={w.text}><td><b className="ltr">{w.text}</b> <span className="muted">— {w.meaning}</span></td><td className="n">{w.failures}</td><td className="n">{w.users}</td></tr>)}</tbody></table>
          )}
        </Card>
        {d.mostTranslated ? (
          <Card title="أكثر الكلمات ترجمة" subtitle="من أحداث فتح الترجمة في النصوص">
            {d.mostTranslated.length === 0 ? <Empty text="تبدأ مع إصدار التطبيق الذي يرسل أحداث الترجمة." /> : <HBars unit="int" items={d.mostTranslated.map((m) => ({ label: m.word, value: m.count }))} />}
          </Card>
        ) : (
          <Card flush title="تغييرات المستوى في هذه المهارة">
            {d.levelFlows.length === 0 ? <Empty title="لا تغييرات" /> : (
              <table className="table"><tbody>{d.levelFlows.map((f, i) => <tr key={i}><td className="ltr" style={{ textAlign: 'right' }}>{levelLabel(f.from)} → {levelLabel(f.to)}</td><td><span className={`badge ${f.direction === 'up' ? 'good' : 'bad'}`}>{f.direction === 'up' ? 'رفع' : 'خفض'}</span></td><td className="n">{f.count}</td></tr>)}</tbody></table>
            )}
          </Card>
        )}
      </div>
    </>
  )
}
