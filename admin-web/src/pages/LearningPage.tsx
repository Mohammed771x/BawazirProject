import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { Learning } from '../api/types'
import { BarChart, ChartFrame, HBars, LineChart, SKILL_COLOR, seriesColor } from '../components/charts/Charts'
import { Card, Delta, Empty, Loader, Tabs } from '../components/ui'
import { filterKey, useFilters } from '../lib/filters'
import { formatDate, formatValue, levelLabel, skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'
import { SkillTable } from './OverviewPage'

type Tab = 'skills' | 'levels' | 'review' | 'words'

export function LearningPage() {
  const { filters, params, setParams } = useFilters()
  const state = useAsync(() => api.learning(filters), [filterKey(filters)])
  const tab = (params.get('tab') as Tab) || 'skills'
  const setTab = (t: Tab) => { const n = new URLSearchParams(params); n.set('tab', t); setParams(n, { replace: true }) }
  return (
    <>
      <div className="page-head">
        <div>
          <h1>التعلم والمهارات</h1>
          <p>كيف يتعلم المستخدمون في كل مهارة، كيف يتعاملون مع المستوى، وهل تساعدهم المراجعة.</p>
        </div>
        <Tabs value={tab} onChange={setTab} items={[
          { value: 'skills', label: 'المهارات' }, { value: 'levels', label: 'المستوى' },
          { value: 'review', label: 'Weekly Review' }, { value: 'words', label: 'الكلمات الأصعب' },
        ]} />
      </div>
      <Loader state={state}>{(d) => (
        <>
          {tab === 'skills' && <Skills data={d} />}
          {tab === 'levels' && <Levels data={d} />}
          {tab === 'review' && <Review data={d} />}
          {tab === 'words' && <Words data={d} />}
        </>
      )}</Loader>
    </>
  )
}

function Skills({ data }: { data: Learning }) {
  const navigate = useNavigate()
  const current = data.skills.map((s) => s.current)
  return (
    <>
      <div className="grid g-5">
        {data.skills.map(({ current: c, previous: p }) => (
          <button key={c.skill} className="kpi clickable" style={{ textAlign: 'start' }} onClick={() => navigate(`/learning/${c.skill}`)}>
            <div className="kpi-label"><span className="dot" style={{ background: SKILL_COLOR[c.skill] }} />{skillLabel(c.skill)}</div>
            <div className="kpi-value">{formatValue(c.firstAttemptAccuracy, 'pct')}</div>
            <div className="kpi-foot"><Delta value={c.firstAttemptAccuracy} previous={p.firstAttemptAccuracy} format="pct" /> دقة المحاولة الأولى</div>
            <div className="kpi-foot">{c.sessions} Sessions · انسحاب {formatValue(c.abandonmentRate, 'pct')}</div>
          </button>
        ))}
      </div>
      <Card flush title="مقارنة المهارات" subtitle="اضغط مهارة لرؤية من فشل، في أي كلمة، وعند أي مستوى، ولماذا">
        <SkillTable skills={current} />
      </Card>
      <div className="grid g-2">
        <Card title="Pass / Fail لكل مهارة" subtitle="قرارات الكلمات خلال الفترة">
          {(() => {
            const labels = current.map((s) => skillLabel(s.skill))
            const series = [
              { name: 'نجاح', values: current.map((s) => s.passed), color: 'var(--series-3)' },
              { name: 'فشل', values: current.map((s) => s.failed), color: 'var(--series-2)' },
            ]
            return <ChartFrame labels={labels} series={series} unit="int"><BarChart labels={labels} series={series} stacked /></ChartFrame>
          })()}
        </Card>
        <Card title="Retry والانسحاب" subtitle="أين يتعثر المتعلم قبل أن ينجح أو يغادر">
          {(() => {
            const labels = current.map((s) => skillLabel(s.skill))
            const series = [
              { name: 'Retry', values: current.map((s) => s.retryRate), color: seriesColor(0) },
              { name: 'Abandonment', values: current.map((s) => s.abandonmentRate), color: seriesColor(1) },
            ]
            return <ChartFrame labels={labels} series={series} unit="pct"><BarChart labels={labels} series={series} unit="pct" /></ChartFrame>
          })()}
        </Card>
      </div>
    </>
  )
}

function Levels({ data }: { data: Learning }) {
  const l = data.levels
  const labels = l.bySkill.map((s) => skillLabel(s.skill))
  const series = [
    { name: 'رفع', values: l.bySkill.map((s) => s.up), color: 'var(--series-3)' },
    { name: 'خفض', values: l.bySkill.map((s) => s.down), color: 'var(--series-2)' },
  ]
  return (
    <>
      <div className="grid g-2">
        <Card title="رفع وخفض المستوى حسب المهارة" subtitle="هل يرفعون في Reading؟ هل يخفضون في Speaking؟">
          <ChartFrame labels={labels} series={series} unit="int"><BarChart labels={labels} series={series} /></ChartFrame>
        </Card>
        <Card title="من أي مستوى يحدث الخفض؟" subtitle="مستوى يكثر فيه الخفض غالبًا محتواه أصعب من اسمه">
          {l.byLevel.length === 0 ? <Empty title="لا تغييرات في الفترة" /> : (
            <HBars unit="int" items={l.byLevel.map((r) => ({ label: levelLabel(r.level), value: r.downgrades, color: 'var(--series-2)', sub: `${r.upgrades} رفع` }))} />
          )}
        </Card>
      </div>
      <Card flush title="From Level → To Level" subtitle="كل انتقال حدث في الفترة، مرتبًا بالتكرار (الفلتر «المهارة» في الأعلى يحصره)">
        {l.flows.length === 0 ? <Empty title="لا انتقالات" text="لم يغيّر أحد المستوى في هذه الفترة." /> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>المهارة</th><th>الانتقال</th><th>الاتجاه</th><th className="n">المرات</th></tr></thead>
              <tbody>
                {l.flows.map((f, i) => (
                  <tr key={i}>
                    <td><span className="row"><span className="dot" style={{ background: SKILL_COLOR[f.skill] }} />{skillLabel(f.skill)}</span></td>
                    <td className="ltr" style={{ fontWeight: 600, textAlign: 'right' }}>{levelLabel(f.from)} → {levelLabel(f.to)}</td>
                    <td><span className={`badge ${f.direction === 'up' ? 'good' : f.direction === 'down' ? 'bad' : ''}`}>{f.direction === 'up' ? 'رفع' : f.direction === 'down' ? 'خفض' : '—'}</span></td>
                    <td className="n">{f.count}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
      <Card flush title="يدوي مقابل النظام" subtitle="المستوى الذي يختاره المتعلم لا يحرك التقدم؛ المستوى المقاس وحده يفعل (R6)">
        <table className="table">
          <thead><tr><th>المهارة</th><th className="n">تغييرات يدوية</th><th className="n">تغييرات النظام</th></tr></thead>
          <tbody>{l.bySkill.map((s) => <tr key={s.skill}><td>{skillLabel(s.skill)}</td><td className="n">{s.manual}</td><td className="n">{s.system}</td></tr>)}</tbody>
        </table>
      </Card>
    </>
  )
}

export function Review({ data }: { data: Pick<Learning, 'weeklyReview'> }) {
  const w = data.weeklyReview
  const labels = w.trend.map((t) => formatDate(t.week))
  const series = [{ name: 'دقة المحاولة الأولى', values: w.trend.map((t) => t.firstAttempt), color: seriesColor(0) }]
  const tiles: [string, number | null, 'int' | 'pct' | 'ms'][] = [
    ['بدأوا', w.started, 'int'], ['أكملوا', w.completed, 'int'], ['كلمات روجعت', w.wordsReviewed, 'int'],
    ['Accuracy', w.accuracy, 'pct'], ['المحاولة الأولى', w.firstAttemptAccuracy, 'pct'], ['الوقت (وسيط)', w.medianMs, 'ms'], ['Drop-off', w.dropOff, 'pct'],
  ]
  return (
    <>
      <div className="row wrap card" style={{ gap: 28 }}>
        {tiles.map(([l, v, f]) => <div key={l}><div className="muted" style={{ fontSize: 12 }}>{l}</div><div style={{ fontSize: 20, fontWeight: 600 }} className="num">{formatValue(v, f)}</div></div>)}
      </div>
      <div className="grid g-main">
        <Card title="هل يتحسن الأداء أسبوعًا بعد أسبوع؟">
          {w.trend.length < 2 ? <Empty title="أقل من أسبوعين من البيانات" /> : <ChartFrame labels={labels} series={series} unit="pct"><LineChart labels={labels} series={series} unit="pct" /></ChartFrame>}
        </Card>
        <Card title="أثر المراجعة على نفس الكلمة" subtitle="المحاولة الأولى في أول مراجعة مقابل التالية — قياس فقط (R9)">
          {w.afterEffect.words === 0 ? <Empty title="لا كلمات روجعت مرتين بعد" /> : (
            <div className="stack lg">
              <HBars unit="pct" max={1} items={[
                { label: 'أول مراجعة', value: w.afterEffect.firstTime, color: 'var(--seq-2)' },
                { label: 'المراجعة التالية', value: w.afterEffect.nextTime, color: 'var(--series-1)' },
              ]} />
              <span className="muted" style={{ fontSize: 12.5 }}>{w.afterEffect.words} كلمة روجعت مرتين أو أكثر.</span>
            </div>
          )}
        </Card>
      </div>
      <Card flush title="أكثر الكلمات فشلًا في المراجعة">
        {w.mostFailed.length === 0 ? <Empty title="لا أخطاء في الفترة" /> : (
          <table className="table">
            <thead><tr><th>الكلمة</th><th className="n">أخطاء أول محاولة</th><th className="n">المحاولات</th><th className="n">المستخدمون</th></tr></thead>
            <tbody>{w.mostFailed.map((m) => <tr key={m.text}><td><b className="ltr">{m.text}</b> <span className="muted">— {m.meaning}</span></td><td className="n">{m.failures}</td><td className="n">{m.attempts}</td><td className="n">{m.users}</td></tr>)}</tbody>
          </table>
        )}
      </Card>
    </>
  )
}

function Words({ data }: { data: Learning }) {
  return (
    <Card flush title="أكثر الكلمات فشلًا" subtitle="عبر كل المتعلمين في النطاق — ومع كل كلمة، في أي مهارة تفشل">
      {data.mostFailedWords.length === 0 ? <Empty title="لا حالات فشل في الفترة" /> : (
        <table className="table">
          <thead><tr><th>الكلمة</th><th>المهارات</th><th className="n">حالات الفشل</th><th className="n">المستخدمون</th></tr></thead>
          <tbody>
            {data.mostFailedWords.map((w) => (
              <tr key={w.text}>
                <td><b className="ltr">{w.text}</b> <span className="muted">— {w.meaning}</span></td>
                <td><div className="row wrap" style={{ gap: 4 }}>{w.skills.map((s) => s.skill && (
                  <span key={s.skill} className="badge"><span className="dot" style={{ background: SKILL_COLOR[s.skill], width: 6, height: 6 }} />{skillLabel(s.skill)} {s.count}</span>
                ))}</div></td>
                <td className="n">{w.failures}</td><td className="n">{w.users}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  )
}
