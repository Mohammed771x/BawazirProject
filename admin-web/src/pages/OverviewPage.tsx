import { AlertTriangle, CheckCircle2, ChevronLeft, Lightbulb, ShieldAlert, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { FunnelStage, Lifecycle, Overview, Signal, SkillSummary } from '../api/types'
import { BarChart, ChartFrame, LineChart, SKILL_COLOR, seriesColor } from '../components/charts/Charts'
import { Card, CATEGORY_NAME, Drawer, Empty, Help, KpiTile, Loader, Tabs } from '../components/ui'
import { UserTable } from '../components/UserTable'
import { filterKey, useFilters } from '../lib/filters'
import { formatDate, formatValue, skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'

export function OverviewPage() {
  const { filters } = useFilters()
  const state = useAsync(() => api.overview(filters), [filterKey(filters)])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>نظرة عامة</h1>
          <p>هل المنتج يعمل بشكل جيد؟ — أهم مؤشرات الصحة، وما يستحق الانتباه الآن.</p>
        </div>
      </div>
      <Loader state={state}>{(d) => <OverviewBody data={d} />}</Loader>
    </>
  )
}

function OverviewBody({ data }: { data: Overview }) {
  return (
    <>
      <Verdict data={data} />
      <div className="kpis">{data.health.map((k) => <KpiTile key={k.key} kpi={k} />)}</div>
      <div className="grid g-main">
        <Engagement data={data} />
        <Signals signals={data.signals} />
      </div>
      <FunnelCard stages={data.funnel} />
      <LifecycleCard lifecycle={data.lifecycle} />
      <SkillsCard skills={data.skills} />
    </>
  )
}

/**
 * The one-line answer to "is the product working?", read from the server's
 * own signals — the page summarises them, it does not judge anything itself.
 */
function Verdict({ data }: { data: Overview }) {
  const high = data.signals.filter((s) => s.severity === 'high').length
  const medium = data.signals.filter((s) => s.severity === 'medium').length
  const tone = high > 0 ? 'bad' : medium > 0 ? 'warn' : 'good'
  const Icon = tone === 'good' ? CheckCircle2 : tone === 'warn' ? TriangleAlert : ShieldAlert
  const text = tone === 'good'
    ? 'لا توجد إشارات تستدعي التدخل في هذه الفترة.'
    : `${high > 0 ? `${high} إشارات مرتفعة` : ''}${high && medium ? ' و' : ''}${medium > 0 ? `${medium} متوسطة` : ''} تستحق المراجعة.`
  return (
    <div className="card">
      <div className="row between wrap" style={{ gap: 16 }}>
        <div className={`verdict ${tone}`}>
          <div className="icon"><Icon size={22} /></div>
          <div>
            <div style={{ fontWeight: 600, fontSize: 15 }}>{tone === 'good' ? 'المنتج يعمل كما هو متوقع' : 'هناك ما يستحق الانتباه'}</div>
            <div className="muted" style={{ fontSize: 13 }}>{text}</div>
          </div>
        </div>
        <div className="muted" style={{ fontSize: 12.5 }}>
          {formatDate(data.period.from, true)} — {formatDate(new Date(new Date(data.period.to).getTime() - 1).toISOString(), true)} · {data.period.learners} متعلمًا في النطاق
        </div>
      </div>
    </div>
  )
}

type EngagementMetric = 'activeUsers' | 'sessions' | 'learningMinutes' | 'newUsers'

function Engagement({ data }: { data: Overview }) {
  const [metric, setMetric] = useState<EngagementMetric>('activeUsers')
  const names: Record<EngagementMetric, string> = {
    activeUsers: 'المستخدمون النشطون', sessions: 'Sessions', learningMinutes: 'دقائق التعلم', newUsers: 'مستخدمون جدد',
  }
  const labels = data.engagement.map((p) => p.date.slice(5))
  const series = [{ name: names[metric], values: data.engagement.map((p) => p[metric]), color: seriesColor(0) }]
  const total = data.engagement.reduce((s, p) => s + p[metric], 0)
  return (
    <Card title="Engagement" subtitle={`${names[metric]} يوميًا · المجموع ${formatValue(total, 'int')}`}
      actions={<Tabs value={metric} onChange={setMetric} items={(Object.keys(names) as EngagementMetric[]).map((k) => ({ value: k, label: names[k] }))} />}>
      {data.engagement.length <= 1
        ? <BarChart labels={labels} series={series} />
        : <ChartFrame labels={labels} series={series} unit="int"><LineChart labels={labels} series={series} height={240} /></ChartFrame>}
    </Card>
  )
}

const SIGNAL_ICON = { high: AlertTriangle, medium: TriangleAlert, low: Lightbulb }

function Signals({ signals }: { signals: Signal[] }) {
  return (
    <Card title="ما الذي يستحق الانتباه؟" subtitle="إشارات محسوبة بقواعد واضحة — تفتح كل منها التحليل الخاص بها">
      {signals.length === 0
        ? <Empty title="لا إشارات" text="لم تتجاوز أي مؤشر عتبته في هذه الفترة." icon={<CheckCircle2 />} />
        : (
          <div className="stack">
            {signals.map((s, i) => {
              const Icon = SIGNAL_ICON[s.severity]
              const body = (
                <div className={`signal ${s.severity}`}>
                  <div className="icon"><Icon /></div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="row between"><h4>{s.title}</h4><span className="badge">{CATEGORY_NAME[s.area] ?? s.area}</span></div>
                    <p>{s.detail}</p>
                  </div>
                </div>
              )
              return s.link ? <Link key={i} to={s.link}>{body}</Link> : <div key={i}>{body}</div>
            })}
          </div>
        )}
    </Card>
  )
}

function FunnelCard({ stages }: { stages: FunnelStage[] }) {
  const [selected, setSelected] = useState<FunnelStage | null>(null)
  const top = stages[0]?.count ?? 0
  const worst = stages.filter((s) => s.dropOff !== null).sort((a, b) => (b.dropOff ?? 0) - (a.dropOff ?? 0))[0]
  return (
    <Card title="Activation Funnel" subtitle="المستخدمون الذين سجلوا خلال الفترة — اضغط أي مرحلة لرؤية من توقفوا عندها"
      actions={worst && worst.dropOff ? <span className="badge bad">أكبر تسرب: {worst.label} · {formatValue(worst.dropOff, 'pct')}</span> : undefined}>
      {top === 0 ? <Empty title="لم يسجل أحد في هذه الفترة" text="وسّع الفترة لرؤية الـFunnel." /> : (
        <div className="funnel">
          {stages.map((s) => (
            <div key={s.key} className={`funnel-row ${selected?.key === s.key ? 'selected' : ''}`} onClick={() => setSelected(s)} role="button" tabIndex={0}>
              <div><div style={{ fontWeight: 500, fontSize: 13 }}>{s.label}</div><div className="muted" style={{ fontSize: 11.5 }}>{s.hint}</div></div>
              <div className="funnel-bar-wrap">
                <div className="funnel-bar" style={{ width: `${Math.max(2, (s.count / top) * 100)}%` }}>{s.count > 0 && s.count}</div>
              </div>
              <div className="funnel-meta">
                <b className="num">{formatValue(s.share, 'pct')}</b>
                {s.dropOff !== null && s.dropOff > 0 && <span className="funnel-drop num">−{formatValue(s.dropOff, 'pct')}</span>}
              </div>
            </div>
          ))}
        </div>
      )}
      <FunnelDrawer stage={selected} onClose={() => setSelected(null)} />
    </Card>
  )
}

function FunnelDrawer({ stage, onClose }: { stage: FunnelStage | null; onClose: () => void }) {
  const state = useAsync(() => (stage ? api.users({ segment: `funnel:${stage.key}`, days: 3650 }) : Promise.resolve(null)), [stage?.key])
  return (
    <Drawer open={!!stage} onClose={onClose} title={`توقفوا عند: ${stage?.label ?? ''}`}>
      <p className="muted" style={{ margin: 0 }}>وصلوا إلى «{stage?.label}» ولم يصلوا إلى المرحلة التالية — من كل المتعلمين، لا من الفترة فقط.</p>
      <div className="card flush">
        <Loader state={state}>{(d) => <UserTable rows={d?.rows ?? []} compact />}</Loader>
      </div>
    </Drawer>
  )
}

function LifecycleCard({ lifecycle: l }: { lifecycle: Lifecycle }) {
  const stats: [string, number, string][] = [
    ['أضيفت', l.added, 'كلمات أضيفت خلال الفترة'], ['دُرست', l.studied, 'كلمات أُجيب عنها في تمرين خلال الفترة'],
    ['قيد التعلم', l.learning, 'الآن'], ['متقنة', l.mature, 'اجتازت المهارات الخمس'], ['Active', l.active, 'الآن'],
    ['مستحقة', l.due, 'تنتظر المتعلم الآن'], ['متأخرة', l.overdue, 'مستحقة منذ أكثر من العتبة'],
  ]
  const series = [
    { name: 'أضيفت', values: l.trend.map((t) => t.added), color: seriesColor(0) },
    { name: 'اجتازت مهارة', values: l.trend.map((t) => t.passed), color: seriesColor(1) },
    { name: 'أصبحت Active', values: l.trend.map((t) => t.activated), color: seriesColor(2) },
  ]
  const labels = l.trend.map((t) => t.date.slice(5))
  return (
    <Card title="Learning Overview" subtitle="حركة الكلمات عبر دورة حياة WordOS">
      <div className="row wrap" style={{ gap: 22, marginBottom: 18 }}>
        {stats.map(([label, v, hint]) => (
          <div key={label}>
            <div className="muted" style={{ fontSize: 12, display: 'flex', gap: 4 }}>{label}<Help text={hint} /></div>
            <div style={{ fontSize: 20, fontWeight: 600 }} className="num">{formatValue(v, 'int', true)}</div>
          </div>
        ))}
      </div>
      <div className="pipeline" style={{ marginBottom: 18 }}>
        {l.byStage.map((s, i) => {
          const total = s.waiting + s.due + s.overdue
          return (
            <Link to={`/learning/${s.skill}`} key={s.skill} className="stage">
              <div className="stage-head"><span className="dot" style={{ background: SKILL_COLOR[s.skill] }} />{i + 1}. {skillLabel(s.skill)}<span className="muted num" style={{ marginInlineStart: 'auto', fontWeight: 500 }}>{total}</span></div>
              <div className="stage-bar" title="بانتظار الفجوة · مستحقة · متأخرة">
                {total > 0 && <>
                  <span style={{ width: `${(s.waiting / total) * 100}%`, background: 'var(--seq-1)' }} />
                  <span style={{ width: `${(s.due / total) * 100}%`, background: 'var(--seq-3)' }} />
                  <span style={{ width: `${(s.overdue / total) * 100}%`, background: 'var(--bad)' }} />
                </>}
              </div>
              <div className="stage-nums">
                <span><b>{s.waiting}</b>بانتظار</span><span><b>{s.due}</b>مستحقة</span><span><b style={{ color: s.overdue ? 'var(--bad)' : undefined }}>{s.overdue}</b>متأخرة</span>
              </div>
            </Link>
          )
        })}
      </div>
      <ChartFrame labels={labels} series={series} unit="int">
        <LineChart labels={labels} series={series} height={200} area={false} />
      </ChartFrame>
    </Card>
  )
}

function SkillsCard({ skills }: { skills: SkillSummary[] }) {
  const navigate = useNavigate()
  const labels = skills.map((s) => skillLabel(s.skill))
  const series = [
    { name: 'دقة المحاولة الأولى', values: skills.map((s) => s.firstAttemptAccuracy), color: seriesColor(0) },
    { name: 'Pass Rate', values: skills.map((s) => s.passRate), color: seriesColor(1) },
    { name: 'الانسحاب', values: skills.map((s) => s.abandonmentRate), color: seriesColor(2) },
  ]
  return (
    <Card title="Skill Analytics" subtitle="المهارات الخمس جنبًا إلى جنب — اضغط مهارة لرؤية تفاصيلها" flush>
      <div style={{ padding: '0 20px 8px' }}>
        <ChartFrame labels={labels} series={series} unit="pct">
          <BarChart labels={labels} series={series} unit="pct" onSelect={(i) => navigate(`/learning/${skills[i].skill}`)} />
        </ChartFrame>
      </div>
      <SkillTable skills={skills} />
    </Card>
  )
}

export function SkillTable({ skills }: { skills: SkillSummary[] }) {
  const navigate = useNavigate()
  const cols: [keyof SkillSummary, string, 'int' | 'pct' | 'ms'][] = [
    ['sessions', 'Sessions', 'int'], ['words', 'الكلمات', 'int'], ['questions', 'الأسئلة', 'int'],
    ['firstAttemptAccuracy', 'المحاولة الأولى', 'pct'], ['overallAccuracy', 'الدقة الكلية', 'pct'],
    ['passRate', 'Pass', 'pct'], ['failRate', 'Fail', 'pct'], ['retryRate', 'Retry', 'pct'],
    ['abandonmentRate', 'Abandonment', 'pct'], ['medianSessionMs', 'وسيط الوقت', 'ms'],
  ]
  return (
    <div className="table-wrap">
      <table className="table">
        <thead><tr><th>المهارة</th>{cols.map(([, l]) => <th key={l} className="n">{l}</th>)}<th /></tr></thead>
        <tbody>
          {skills.map((s) => (
            <tr key={s.skill} className="clickable" onClick={() => navigate(`/learning/${s.skill}`)}>
              <td><span className="row"><span className="dot" style={{ background: SKILL_COLOR[s.skill] }} />{skillLabel(s.skill)}</span></td>
              {cols.map(([k, l, f]) => <td key={l} className="n">{formatValue(s[k] as number | null, f)}</td>)}
              <td><ChevronLeft size={16} className="muted" /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
