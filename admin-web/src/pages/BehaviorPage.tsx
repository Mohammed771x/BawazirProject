import { ArrowRight, ChevronLeft, Smartphone } from 'lucide-react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api } from '../api'
import type { Behavior, FrictionDetail, FrictionItem } from '../api/types'
import { BarChart, ChartFrame, HBars, SKILL_COLOR, seriesColor } from '../components/charts/Charts'
import { Card, CATEGORY_NAME, CATEGORY_TONE, Delta, Empty, Help, Loader, SeverityBadge, Tabs } from '../components/ui'
import { UserTable } from '../components/UserTable'
import { filterKey, useFilters } from '../lib/filters'
import { formatValue, levelLabel, skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'

type Tab = 'friction' | 'time' | 'content' | 'screens'

export function BehaviorPage() {
  const { filters, params, setParams } = useFilters()
  const state = useAsync(() => api.behavior(filters), [filterKey(filters)])
  const tab = (params.get('tab') as Tab) || 'friction'
  const setTab = (t: Tab) => { const n = new URLSearchParams(params); n.set('tab', t); setParams(n, { replace: true }) }
  return (
    <>
      <div className="page-head">
        <div>
          <h1>سلوك المستخدم وUX</h1>
          <p>أين يحتك المستخدم بالتجربة، كيف يقضي وقته، وكيف يتعامل مع المحتوى.</p>
        </div>
        <Tabs value={tab} onChange={setTab} items={[
          { value: 'friction', label: 'نقاط الاحتكاك' }, { value: 'time', label: 'الوقت' },
          { value: 'content', label: 'التفاعل مع المحتوى' }, { value: 'screens', label: 'الشاشات والأخطاء' },
        ]} />
      </div>
      <Loader state={state}>{(d) => (
        <>
          {!d.tracked && (
            <div className="alert info row" style={{ gap: 10 }}><Smartphone size={18} />
              أحداث الشاشة (ترجمة، إعادة تشغيل، تلميحات، خروج، وقت التطبيق) تصل من إصدار التطبيق الذي يرسلها. ما يظهر الآن محسوب من بيانات الخادم فقط.
            </div>
          )}
          {tab === 'friction' && <Friction items={d.friction} />}
          {tab === 'time' && <Time data={d} />}
          {tab === 'content' && <Content data={d} />}
          {tab === 'screens' && <Screens data={d} />}
        </>
      )}</Loader>
    </>
  )
}

function Friction({ items }: { items: FrictionItem[] }) {
  const navigate = useNavigate()
  const { params } = useFilters()
  return (
    <Card flush title="أكثر نقاط الاحتكاك شيوعًا" subtitle="مرتبة بالخطورة ثم بعدد المستخدمين المتأثرين — اضغط أي نقطة لرؤية من تأثر، وعند أي محتوى ومستوى، وهل أكملوا">
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>نقطة الاحتكاك</th><th>النوع</th><th className="n">القيمة</th><th className="n">التغير</th><th className="n">متأثرون</th><th>الخطورة</th><th /></tr></thead>
          <tbody>
            {items.map((f) => (
              <tr key={f.key} className="clickable" onClick={() => navigate(`/behavior/friction/${f.key}?${params.toString()}`)}>
                <td>
                  <div className="row" style={{ gap: 6 }}>
                    {f.skill && <span className="dot" style={{ background: SKILL_COLOR[f.skill] }} />}
                    <span style={{ fontWeight: 500 }}>{f.title}</span><Help text={f.description} />
                  </div>
                </td>
                <td><span className={`badge ${CATEGORY_TONE[f.category]}`}>{CATEGORY_NAME[f.category]}</span></td>
                <td className="n">{formatValue(f.value, f.format)}</td>
                <td className="n"><Delta value={f.value} previous={f.baseline} format={f.format} goodWhenUp={false} /></td>
                <td className="n">{f.affectedUsers}</td>
                <td><SeverityBadge severity={f.severity} /></td>
                <td><ChevronLeft size={16} className="muted" /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  )
}

function Time({ data }: { data: Behavior }) {
  const t = data.time
  const sessLabels = t.sessionDistribution.map((b) => b.label)
  const sessSeries = [{ name: 'Sessions', values: t.sessionDistribution.map((b) => b.count), color: seriesColor(0) }]
  const ansLabels = t.answerDistribution.map((b) => b.label)
  const ansSeries = [{ name: 'إجابات', values: t.answerDistribution.map((b) => b.count), color: seriesColor(0) }]
  const learning = t.metrics.find((m) => m.key === 'learning_time')?.value ?? null
  const app = t.metrics.find((m) => m.key === 'app_time')?.value ?? null
  return (
    <>
      <div className="evidence-grid">
        {t.metrics.map((m) => (
          <div key={m.key} className="evidence" style={{ background: 'var(--surface)' }}>
            <div className="l row" style={{ gap: 4 }}>{m.label}<Help text={m.hint} /></div>
            <div className="v">{formatValue(m.value, m.format)}</div>
          </div>
        ))}
      </div>
      <Card title="وقت التطبيق مقابل وقت التعلم الفعلي" subtitle="الفرق هو وقت التصفح والانتظار خارج التمارين">
        {app === null ? <Empty title="وقت التطبيق غير متاح بعد" text="يُقاس من الهاتف؛ يبدأ مع الإصدار الذي يرسل أحداث فتح وإغلاق التطبيق." /> : (
          <HBars unit="ms" max={Math.max(app, learning ?? 0)} items={[
            { label: 'وقت التطبيق', value: app, color: 'var(--seq-2)' },
            { label: 'وقت التعلم الفعلي', value: learning, color: 'var(--series-1)' },
          ]} />
        )}
      </Card>
      <div className="grid g-2">
        <Card title="توزيع مدة الـSessions" subtitle="التوزيع لا المتوسط — Sessions المكتملة خلال ساعة">
          <ChartFrame labels={sessLabels} series={sessSeries} unit="int"><BarChart labels={sessLabels} series={sessSeries} /></ChartFrame>
        </Card>
        <Card title="الوقت لكل إجابة" subtitle="من ظهور السؤال إلى إرسال الإجابة">
          {t.answerDistribution.every((b) => b.count === 0) ? <Empty text="يبدأ مع الإصدار الذي يرسل زمن الإجابة." /> :
            <ChartFrame labels={ansLabels} series={ansSeries} unit="int"><BarChart labels={ansLabels} series={ansSeries} /></ChartFrame>}
        </Card>
      </div>
      <div className="grid g-2">
        <Card flush title="زمن الإجابة لكل مهارة">
          <table className="table"><thead><tr><th>المهارة</th><th className="n">الوسيط</th><th className="n">P90</th><th className="n">إجابات</th></tr></thead>
            <tbody>{t.answerBySkill.map((s) => <tr key={s.skill}><td><span className="row"><span className="dot" style={{ background: SKILL_COLOR[s.skill] }} />{skillLabel(s.skill)}</span></td><td className="n">{formatValue(s.median, 'ms')}</td><td className="n">{formatValue(s.p90, 'ms')}</td><td className="n">{s.count}</td></tr>)}</tbody></table>
        </Card>
        <Card flush title="زمن استجابة الـAI" subtitle="مقاس في الخادم لكل طلب، قبل أي محتوى احتياطي">
          {t.aiByOperation.length === 0 ? <Empty title="لا طلبات مقاسة في الفترة" /> : (
            <table className="table"><thead><tr><th>العملية</th><th className="n">طلبات</th><th className="n">أخطاء</th><th className="n">الوسيط</th><th className="n">P95</th></tr></thead>
              <tbody>{t.aiByOperation.map((o) => <tr key={o.operation}><td className="ltr" style={{ textAlign: 'right' }}>{o.operation}</td><td className="n">{o.calls}</td><td className="n" style={{ color: o.errors ? 'var(--bad)' : undefined }}>{o.errors}</td><td className="n">{formatValue(o.median, 'ms')}</td><td className="n">{formatValue(o.p95, 'ms')}</td></tr>)}</tbody></table>
          )}
        </Card>
      </div>
    </>
  )
}

function Content({ data }: { data: Behavior }) {
  return (
    <>
      <div className="grid g-3">
        {data.content.map((c) => (
          <Card key={c.skill} title={<span className="row"><span className="dot" style={{ background: SKILL_COLOR[c.skill] }} />{skillLabel(c.skill)}</span>}
            actions={<Link className="btn ghost sm" to={`/learning/${c.skill}`}>التفاصيل <ChevronLeft size={14} /></Link>}>
            <div className="stack" style={{ gap: 6 }}>
              {c.metrics.map((m) => (
                <div key={m.key} className="row between" style={{ fontSize: 13 }}>
                  <span className="text-2 row" style={{ gap: 4 }}>{m.label}<Help text={m.hint} /></span>
                  <b className="num">{formatValue(m.value, m.format)}</b>
                </div>
              ))}
            </div>
          </Card>
        ))}
        <Card title="أكثر الكلمات ترجمة" subtitle="Reading">
          {data.mostTranslated.length === 0 ? <Empty text="لا أحداث ترجمة بعد." /> : <HBars unit="int" items={data.mostTranslated.map((m) => ({ label: m.word, value: m.count }))} />}
        </Card>
      </div>
    </>
  )
}

function Screens({ data }: { data: Behavior }) {
  return (
    <div className="grid g-2">
      <Card flush title="الشاشات" subtitle="كم مرة فُتحت، كم بقي عليها المستخدم، وكم مرة غادرها فورًا">
        {data.screens.length === 0 ? <Empty text="لا أحداث شاشة بعد." /> : (
          <table className="table"><thead><tr><th>الشاشة</th><th className="n">مرات</th><th className="n">مستخدمون</th><th className="n">الوسيط</th><th className="n">رجوع فوري</th></tr></thead>
            <tbody>{data.screens.map((s) => <tr key={s.screen}><td className="ltr" style={{ textAlign: 'right' }}>{s.screen}</td><td className="n">{s.views}</td><td className="n">{s.users}</td><td className="n">{formatValue(s.medianMs, 'ms')}</td><td className="n">{s.quickExits}</td></tr>)}</tbody></table>
        )}
      </Card>
      <Card flush title="أخطاء الاتصال كما رآها الهاتف">
        {data.errors.length === 0 ? <Empty title="لا أخطاء مسجلة" /> : (
          <table className="table"><thead><tr><th>الرمز</th><th className="n">مرات</th><th className="n">مستخدمون</th></tr></thead>
            <tbody>{data.errors.map((e) => <tr key={e.code}><td className="ltr" style={{ textAlign: 'right' }}>{e.code}</td><td className="n">{e.count}</td><td className="n">{e.users}</td></tr>)}</tbody></table>
        )}
      </Card>
    </div>
  )
}

export function FrictionPage() {
  const { key = '' } = useParams()
  const { filters } = useFilters()
  const state = useAsync(() => api.friction(key, filters), [key, filterKey(filters)])
  return (
    <>
      <Link to="/behavior" className="row muted" style={{ fontSize: 13 }}><ArrowRight size={16} /> سلوك المستخدم وUX</Link>
      <Loader state={state}>{(d) => <FrictionBody d={d} />}</Loader>
    </>
  )
}

function FrictionBody({ d }: { d: FrictionDetail }) {
  const total = d.outcome.completed + d.outcome.open + d.outcome.abandoned
  const dayLabels = d.byDay.map((x) => x.date.slice(5))
  const daySeries = [{ name: 'Sessions متأثرة', values: d.byDay.map((x) => x.sessions), color: seriesColor(0) }]
  return (
    <>
      <div className="page-head">
        <div>
          <h1>{d.item.title}</h1>
          <p>{d.item.description}</p>
        </div>
        <div className="row"><SeverityBadge severity={d.item.severity} /><span className={`badge ${CATEGORY_TONE[d.item.category]}`}>{CATEGORY_NAME[d.item.category]}</span></div>
      </div>
      <div className="kpis">
        <div className="kpi"><div className="kpi-label">القيمة</div><div className="kpi-value">{formatValue(d.item.value, d.item.format)}</div><div className="kpi-foot"><Delta value={d.item.value} previous={d.item.baseline} format={d.item.format} goodWhenUp={false} /></div></div>
        <div className="kpi"><div className="kpi-label">مستخدمون متأثرون</div><div className="kpi-value">{d.item.affectedUsers}</div></div>
        <div className="kpi"><div className="kpi-label">دقة المتأثرين</div><div className="kpi-value">{formatValue(d.performance.affectedAccuracy, 'pct')}</div><div className="kpi-foot">مقابل {formatValue(d.performance.othersAccuracy, 'pct')} لبقية الـSessions</div></div>
        <div className="kpi"><div className="kpi-label">أكملوا التمرين</div><div className="kpi-value">{formatValue(total ? d.outcome.completed / total : null, 'pct')}</div><div className="kpi-foot">{d.outcome.abandoned} انسحبوا · {d.outcome.open} مفتوحة</div></div>
      </div>
      <div className="grid g-3">
        <Card title="المستوى">{d.byLevel.length === 0 ? <Empty /> : <HBars unit="int" items={d.byLevel.map((x) => ({ label: levelLabel(x.level), value: x.sessions }))} />}</Card>
        <Card title="المهارة">{d.bySkill.length === 0 ? <Empty /> : <HBars unit="int" items={d.bySkill.map((x) => ({ label: skillLabel(x.skill), value: x.sessions, color: SKILL_COLOR[x.skill] }))} />}</Card>
        <Card title="المحتوى" subtitle="النصوص التي تكرر فيها الاحتكاك">{d.content.length === 0 ? <Empty /> : <HBars unit="int" items={d.content.map((x) => ({ label: x.title, value: x.sessions, sub: levelLabel(x.level) }))} />}</Card>
      </div>
      <Card title="الوقت">{d.byDay.length === 0 ? <Empty /> : <ChartFrame labels={dayLabels} series={daySeries} unit="int"><BarChart labels={dayLabels} series={daySeries} /></ChartFrame>}</Card>
      <Card flush title="المستخدمون المتأثرون"><UserTable rows={d.users} evidenceLabel="" compact /></Card>
    </>
  )
}
