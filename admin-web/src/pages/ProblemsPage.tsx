import { ChevronLeft } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { Learning, Overview } from '../api/types'
import { Donut, HBars, SKILL_COLOR } from '../components/charts/Charts'
import { Card, CATEGORY_NAME, CATEGORY_TONE, Empty, Loader, SeverityBadge } from '../components/ui'
import { filterKey, useFilters } from '../lib/filters'
import { skillLabel } from '../lib/format'
import { useAsync } from '../lib/useAsync'

const CATEGORY_COLOR: Record<string, string> = {
  learning: 'var(--series-1)', ux: 'var(--s-writing)', content: 'var(--series-2)', ai: 'var(--text-3)', unknown: 'var(--surface-3)',
}

const CATEGORY_HELP: Record<string, string> = {
  learning: 'المتعلم وصل إلى السؤال وأجاب وأخطأ: معنى، استخدام، Grammar، حروف.',
  ux: 'التجربة نفسها أوقفته: انسحب، لم يُجب، لم يتكلم.',
  content: 'المحتوى أصعب من المستوى: خفّض المستوى، أعاد الصوت كثيرًا، فشل رغم التلميحات.',
  ai: 'الـAI لم يُجب أو استُخدم تقييم احتياطي.',
  unknown: 'فشل سُجّل قبل بدء تتبع أسبابه.',
}

/**
 * Failure analysis (admin brief §12): not "Speaking failed" but why — and
 * whether "why" is a learning, UX, content or AI problem.
 */
export function ProblemsPage() {
  const { filters } = useFilters()
  const learning = useAsync(() => api.learning(filters), [filterKey(filters)])
  const overview = useAsync(() => api.overview(filters), [filterKey(filters)])
  return (
    <>
      <div className="page-head">
        <div>
          <h1>المشكلات والتحليل</h1>
          <p>لماذا يحدث الفشل، وهل هو مشكلة تعلم أم UX أم محتوى أم AI — ثم أين تبدأ التحقيق.</p>
        </div>
      </div>
      <Loader state={learning}>{(l) => <Failures data={l} />}</Loader>
      <Loader state={overview} skeleton={<div />}>{(o) => <SignalsTable data={o} />}</Loader>
    </>
  )
}

function Failures({ data }: { data: Learning }) {
  const navigate = useNavigate()
  const [skill, setSkill] = useState<string | null>(null)
  const total = data.categories.reduce((s, c) => s + c.count, 0)
  const shown = data.failures.filter((f) => !skill || f.skill === skill)
  return (
    <>
      <div className="grid g-main">
        <Card title="طبيعة الفشل" subtitle={`${total} حالة فشل في الفترة، كل منها مصنفة مرة واحدة`}>
          {total === 0 ? <Empty title="لا حالات فشل في الفترة" /> : (
            <div className="grid g-2" style={{ alignItems: 'center' }}>
              <Donut items={data.categories.map((c) => ({ label: c.label, value: c.count, color: CATEGORY_COLOR[c.category] }))} />
              <div className="stack">
                {data.categories.map((c) => (
                  <div key={c.category} className="note">
                    <div className="row between"><b>{c.label}</b><span className="num">{c.count}</span></div>
                    <div className="muted" style={{ fontSize: 12 }}>{CATEGORY_HELP[c.category]}</div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </Card>
        <Card title="الفشل لكل مهارة" subtitle="اضغط مهارة لحصر الأسباب أدناه">
          <HBars unit="int" items={data.failures.map((f) => ({
            label: skillLabel(f.skill), value: f.total, color: SKILL_COLOR[f.skill], onClick: () => setSkill(skill === f.skill ? null : f.skill),
          }))} />
          {skill && <button className="btn ghost sm" style={{ marginTop: 10 }} onClick={() => setSkill(null)}>كل المهارات</button>}
        </Card>
      </div>
      <div className="grid g-2">
        {shown.map((f) => (
          <Card key={f.skill} title={<span className="row"><span className="dot" style={{ background: SKILL_COLOR[f.skill] }} />أسباب الفشل في {skillLabel(f.skill)}</span>}
            subtitle={`${f.total} حالة`}
            actions={<Link className="btn ghost sm" to={`/learning/${f.skill}`}>من وأين ومتى <ChevronLeft size={14} /></Link>}>
            {f.reasons.length === 0 ? <Empty title="لا حالات" /> : (
              <div className="stack" style={{ gap: 10 }}>
                <HBars unit="int" items={f.reasons.map((r) => ({ label: r.label, value: r.count, color: CATEGORY_COLOR[r.category], onClick: () => navigate(`/learning/${f.skill}`) }))} />
                <div className="row wrap" style={{ gap: 6 }}>
                  {[...new Set(f.reasons.map((r) => r.category))].map((c) => <span key={c} className={`badge ${CATEGORY_TONE[c]}`}><span className="dot" style={{ background: CATEGORY_COLOR[c], width: 6, height: 6 }} />{CATEGORY_NAME[c]}</span>)}
                </div>
              </div>
            )}
          </Card>
        ))}
      </div>
    </>
  )
}

function SignalsTable({ data }: { data: Overview }) {
  return (
    <Card flush title="إشارات تستحق التحقيق" subtitle="ما تجاوز عتبته في الفترة — نقطة بداية، لا قرار">
      {data.signals.length === 0 ? <Empty title="لا إشارات" /> : (
        <table className="table">
          <thead><tr><th>الإشارة</th><th>النوع</th><th>الخطورة</th><th /></tr></thead>
          <tbody>
            {data.signals.map((s, i) => (
              <tr key={i}>
                <td><div style={{ fontWeight: 500 }}>{s.title}</div><div className="muted" style={{ fontSize: 12 }}>{s.detail}</div></td>
                <td><span className="badge">{CATEGORY_NAME[s.area] ?? s.area}</span></td>
                <td><SeverityBadge severity={s.severity} /></td>
                <td>{s.link && <Link className="btn sm" to={s.link}>افتح</Link>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  )
}
