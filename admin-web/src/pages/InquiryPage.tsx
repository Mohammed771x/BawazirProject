import { Bot, CalendarClock, CornerDownLeft, FlaskConical, Gavel, History, ListChecks, MessagesSquare, RefreshCw, Sparkles } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api } from '../api'
import type { Investigation } from '../api/types'
import { SpecChart } from '../components/charts/Charts'
import { Card, Empty, ErrorState, Loader, Skeleton } from '../components/ui'
import { UserTable } from '../components/UserTable'
import { useFilters } from '../lib/filters'
import { formatDate, formatDateTime, formatValue } from '../lib/format'
import { useAsync } from '../lib/useAsync'
import { Delta } from '../components/ui'
import { SECTIONS, STATUS_LABEL, sectionLabel, today, verdictLabel } from '../lib/product'

const EXAMPLES: [string, string][] = [
  ['listening', 'لماذا انخفضت نسبة إكمال Listening هذا الشهر؟'],
  ['skills', 'ما أكثر Skill يعاني منها المستخدمون الجدد؟'],
  ['words', 'لماذا المستخدمون الذين يضيفون أكثر من 10 كلمات لا يصلون إلى Mastery؟'],
  ['reading', 'هل B2 أصعب من اللازم في Reading؟'],
  ['words', 'ما أكثر الكلمات التي يفشل فيها المستخدمون؟'],
  ['reading', 'هل Translation Usage مرتبط بانخفاض Reading Accuracy؟'],
  ['speaking', 'لماذا ينسحب المستخدمون من Speaking؟'],
]

/**
 * The reporting day an instant belongs to. Period bounds are local midnights;
 * twelve hours either side lands inside the right day for any offset the
 * server could be configured with, without the page knowing which one it is.
 */
const dayOf = (iso: string, shift: 1 | -1) => new Date(Date.parse(iso) + shift * 12 * 36e5).toISOString().slice(0, 10)

export function InquiryPage() {
  const { id } = useParams()
  return id ? <SavedInvestigation id={id} /> : <Ask />
}

function Ask() {
  const navigate = useNavigate()
  const { filters } = useFilters()
  const [section, setSection] = useState<string | null>(null)
  const [question, setQuestion] = useState('')
  const [days, setDays] = useState(filters.days ?? 30)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<Error | null>(null)
  const [result, setResult] = useState<Investigation | null>(null)
  const history = useAsync(() => api.inquiries(), [result?.id])

  const submit = async () => {
    if (!section || question.trim().length < 3) return
    setBusy(true); setError(null); setResult(null)
    try {
      const r = await api.ask(section, question.trim(), { days })
      setResult(r)
      // Saved under this id: the address is now a link to exactly this answer.
      navigate(`/inquiry/${r.id}`, { replace: true })
    } catch (e) {
      setError(e as Error)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>تحليل واستفسار</h1>
          <p>اسأل سؤال منتج عن بيانات WordOS. النظام يحسب الأدلة بنفسه، ثم يفصل بين البيانات والتفسير والفرضيات — والقرار لك.</p>
        </div>
      </div>
      <div className="grid g-main" style={{ alignItems: 'start' }}>
        <div className="stack lg">
          <Card title="1. اختر القسم" subtitle="القسم يحدد أي أدلة تُجمع أولًا — أو اختر النقاش لمناقشة فكرة دون استعلام">
            <div className="section-picker">
              <button className={section === 'discussion' ? 'active' : ''} onClick={() => setSection('discussion')}><MessagesSquare size={14} /> نقاش دون استعلام</button>
              {SECTIONS.map(([k, l]) => <button key={k} className={section === k ? 'active' : ''} onClick={() => setSection(k)}>{l}</button>)}
            </div>
          </Card>
          <Card title="2. اكتب الاستفسار">
            <div className="stack">
              <textarea className="textarea" rows={4} placeholder="اكتب ما تريد معرفته…" value={question} onChange={(e) => setQuestion(e.target.value)} maxLength={1000}
                onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) void submit() }} />
              <div className="row between wrap">
                <label className="row" style={{ fontSize: 13, gap: 6 }}>الفترة
                  <select className="select" value={days} onChange={(e) => setDays(Number(e.target.value))}>
                    {[7, 30, 90, 365].map((d) => <option key={d} value={d}>آخر {d} يومًا</option>)}
                  </select>
                </label>
                <button className="btn primary" disabled={busy || !section || question.trim().length < 3} onClick={submit}>
                  <Sparkles /> {busy ? 'جارٍ التحقيق…' : 'ابدأ التحقيق'}
                </button>
              </div>
              {!section && question && <span className="muted" style={{ fontSize: 12.5 }}>اختر القسم أولًا.</span>}
              <div className="section-title" style={{ margin: '8px 0 0' }}>أمثلة</div>
              <div className="row wrap" style={{ gap: 6 }}>
                {EXAMPLES.map(([s, q]) => <button key={q} className="example" onClick={() => { setSection(s); setQuestion(q) }}>{q}</button>)}
              </div>
            </div>
          </Card>
        </div>
        <Card title={<span className="row"><History size={16} />التحقيقات السابقة</span>} flush>
          <Loader state={history} skeleton={<div style={{ padding: 20 }}><Skeleton h={60} /></div>}>{(h) => h.items.length === 0
            ? <Empty title="لا تحقيقات بعد" text="كل تحقيق يُحفظ كما ظهر، ويمكن إعادة فتحه لاحقًا." />
            : (
              <div>
                {h.items.map((i) => (
                  <Link key={i.id} to={`/inquiry/${i.id}`} style={{ display: 'block', padding: '12px 20px', borderTop: '1px solid var(--border)', paddingInlineStart: i.parentId ? 36 : 20 }}>
                    <div style={{ fontWeight: 500, fontSize: 13.5 }}>
                      {i.parentId && <CornerDownLeft size={12} className="muted" />} {i.question}
                      {i.followUpOn && <> <span className={`badge ${i.followUpOn <= today() ? 'warn' : ''}`}><CalendarClock /> {i.followUpOn <= today() ? 'حان موعد المتابعة' : `متابعة ${formatDate(i.followUpOn)}`}</span></>}
                    </div>
                    <div className="muted" style={{ fontSize: 12 }}>{sectionLabel(i.section)} · {formatDate(i.createdAt, true)} · {i.author ?? '—'}</div>
                    <div className="text-2" style={{ fontSize: 12.5, marginTop: 2, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{i.summary}</div>
                  </Link>
                ))}
              </div>
            )}</Loader>
        </Card>
      </div>
      {busy && <div className="card"><div className="stack"><Skeleton h={18} w="40%" /><Skeleton h={60} /><Skeleton h={200} /></div><p className="muted" style={{ marginBottom: 0 }}>نحسب الأدلة من قاعدة البيانات، ثم نطلب قراءتها…</p></div>}
      {error && <div className="card"><ErrorState error={error} onRetry={submit} /></div>}
    </>
  )
}

function SavedInvestigation({ id }: { id: string }) {
  const state = useAsync(() => api.inquiry(id), [id])
  return (
    <>
      <Link to="/ai" className="muted" style={{ fontSize: 13 }}>→ WordOS AI</Link>
      <Loader state={state}>{(inv) => (
        <>
          {inv.parentId && <Link to={`/inquiry/${inv.parentId}`} className="note row" style={{ fontSize: 13 }}><CornerDownLeft size={14} /> متابعة لتحقيق سابق — افتح السؤال الأصلي</Link>}
          <InvestigationView inv={inv} />
          <ThreadTools inv={inv} />
        </>
      )}</Loader>
    </>
  )
}

/**
 * What can be done with a saved investigation (brief §9.5, §10, ADR-132):
 * carry the conversation on, set a date to look again, ask it again now, or
 * turn it into a proposed decision. None of these watches anything by itself
 * — a follow-up date is a reminder on this page, not a process.
 */
function ThreadTools({ inv }: { inv: Investigation }) {
  const navigate = useNavigate()
  const [question, setQuestion] = useState('')
  const [followUp, setFollowUp] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<Error | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const area = inv.section === 'discussion' ? undefined : inv.section
  const related = useAsync(() => (area ? api.decisions({ area }) : Promise.resolve({ items: [], counts: { proposed: 0, active: 0, due: 0, closed: 0 } })), [area])

  const run = async (kind: string, fn: () => Promise<Investigation | void>) => {
    setBusy(kind); setError(null)
    try {
      const r = await fn()
      if (r) navigate(`/inquiry/${r.id}`)
    } catch (e) { setError(e as Error) } finally { setBusy(null) }
  }

  const toDecision = () => {
    const p = new URLSearchParams({
      inquiry: inv.id,
      area: inv.section === 'discussion' || inv.section === 'chat' ? 'general' : inv.section,
      problem: inv.question,
      evidence: [inv.summary, ...inv.evidence.slice(0, 6).map((e) => `${e.label}: ${formatValue(e.value, e.format)}`)].join('\n'),
      hypothesis: inv.hypotheses[0] ?? '',
    })
    if (inv.mode !== 'discussion') { p.set('from', dayOf(inv.periodFrom, 1)); p.set('to', dayOf(inv.periodTo, -1)) }
    if (inv.filter?.segment) p.set('segment', inv.filter.segment)
    navigate(`/decisions/new?${p}`)
  }

  const fromChat = inv.section === 'chat'

  return (
    <div className="stack lg">
      {fromChat ? (
        <Card title="ما التالي؟" subtitle="حُفظ هذا التحقيق من محادثة WordOS AI">
          <div className="stack">
            <div className="row wrap" style={{ gap: 8 }}>
              <Link className="btn primary" to="/ai"><Sparkles /> تابع في WordOS AI</Link>
              <button className="btn" onClick={toDecision}><Gavel /> حوّله إلى اقتراح قرار</button>
            </div>
            <div className="row wrap" style={{ gap: 8 }}>
              <span className="muted" style={{ fontSize: 13 }}>راقب وعُد إليه في:</span>
              <input className="input ltr" type="date" min={today()} value={followUp} onChange={(e) => setFollowUp(e.target.value)} aria-label="موعد المتابعة" />
              <button className="btn sm" disabled={!followUp || !!busy} onClick={() => run('follow', async () => { await api.setFollowUp(inv.id, followUp); setSaved(followUp) })}>
                <CalendarClock /> حفظ موعد المتابعة
              </button>
              {saved && <span className="badge good">سيظهر في قائمة التحقيقات عند {formatDate(saved)}</span>}
            </div>
            {error && <ErrorState error={error} />}
          </div>
        </Card>
      ) : (
      <Card title="تابع النقاش" subtitle="السؤال التالي يرث القسم والفلاتر من هذا التحقيق — اكتب «والفترة السابقة؟» دون إعادة الاختيار">
        <div className="stack">
          <textarea className="textarea" rows={2} value={question} maxLength={1000} placeholder="سؤال المتابعة…" onChange={(e) => setQuestion(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && question.trim().length >= 3) void run('ask', () => api.ask(null, question.trim(), null, inv.id)) }} />
          <div className="row wrap" style={{ gap: 8 }}>
            <button className="btn primary" disabled={!!busy || question.trim().length < 3} onClick={() => run('ask', () => api.ask(null, question.trim(), null, inv.id))}>
              <Sparkles /> {busy === 'ask' ? 'جارٍ التحقيق…' : 'اسأل'}
            </button>
            <button className="btn" disabled={!!busy} onClick={() => run('rerun', () => api.rerun(inv.id))} title="السؤال نفسه على الأيام منذ طُرح">
              <RefreshCw /> {busy === 'rerun' ? 'جارٍ…' : 'أعد التحقيق على الأيام منذ طرحه'}
            </button>
            <button className="btn" onClick={toDecision}><Gavel /> حوّله إلى اقتراح قرار</button>
          </div>
          <div className="row wrap" style={{ gap: 8 }}>
            <span className="muted" style={{ fontSize: 13 }}>راقب وعُد إليه في:</span>
            <input className="input ltr" type="date" min={today()} value={followUp} onChange={(e) => setFollowUp(e.target.value)} aria-label="موعد المتابعة" />
            <button className="btn sm" disabled={!followUp || !!busy} onClick={() => run('follow', async () => { await api.setFollowUp(inv.id, followUp); setSaved(followUp) })}>
              <CalendarClock /> حفظ موعد المتابعة
            </button>
            {saved && <span className="badge good">سيظهر في قائمة التحقيقات عند {formatDate(saved)}</span>}
          </div>
          <span className="muted" style={{ fontSize: 12 }}>النظام لا يراقب البيانات باستمرار: موعد المتابعة تذكير في هذه الصفحة، وعند حلوله أعد التحقيق لترى ما تغير.</span>
          {error && <ErrorState error={error} />}
        </div>
      </Card>
      )}
      {area && !fromChat && (
        <Card flush title="قرارات سابقة في هذا المجال" subtitle="راجعها قبل اقتراح قرار جديد — الـAI يراها أيضًا عند التحليل">
          <Loader state={related} skeleton={<div style={{ padding: 20 }}><Skeleton h={40} /></div>}>{(r) => r.items.length === 0 ? <Empty title="لا قرارات سابقة هنا" /> : (
            <table className="table"><tbody>{r.items.map((d) => {
              const v = verdictLabel(d.verdict)
              const [sl, st] = STATUS_LABEL[d.status] ?? [d.status, '']
              return (
                <tr key={d.id}><td><Link to={`/decisions/${d.id}`}>{d.title}</Link></td><td><span className={`badge ${st}`}>{sl}</span></td>
                  <td>{v ? <span className={`badge ${v[2]}`}>{v[1]}</span> : <span className="muted">—</span>}</td><td className="muted">{formatDate(d.updatedAt, true)}</td></tr>
              )
            })}</tbody></table>
          )}</Loader>
        </Card>
      )}
    </div>
  )
}

/**
 * The investigation, in the order the brief asks for — and with data,
 * interpretation and hypothesis visibly apart, so a guess is never read as a
 * measurement.
 */
export function InvestigationView({ inv }: { inv: Investigation }) {
  return (
    <div className="stack lg">
      <div className="layer">
        <div className="row between wrap" style={{ marginBottom: 6 }}>
          <span className="muted" style={{ fontSize: 12.5 }}>{inv.sectionLabel} · {formatDateTime(inv.createdAt)}{inv.author && ` · ${inv.author}`} · الفترة {formatDate(inv.periodFrom)} — {formatDate(inv.periodTo)}</span>
          <span className="row" style={{ gap: 6 }}>
            {inv.mode === 'discussion' && <span className="badge warn"><MessagesSquare /> نقاش — لم تُقرأ بيانات</span>}
            <span className="badge">{inv.interpretedBy === 'ai' ? <><Bot /> فسّره الـAI من الأدلة</> : <><ListChecks /> تفسير بالقواعد — الـAI غير متاح</>}</span>
          </span>
        </div>
        <h2 style={{ fontSize: 17, marginBottom: 10 }}>{inv.question}</h2>
        <div className="layer-tag interp">النتيجة المختصرة</div>
        <div className="summary-box">{inv.summary}</div>
        <p className="muted" style={{ fontSize: 12.5, marginBottom: 0 }}>{inv.dataNote}</p>
      </div>

      <div className="layer">
        <div className="layer-tag data">البيانات — الأدلة</div>
        {inv.evidence.length === 0 ? <Empty title="لا أدلة رقمية" /> : (
          <div className="evidence-grid">
            {inv.evidence.map((e, i) => (
              <div key={i} className="evidence">
                <div className="l">{e.label}</div>
                <div className="v">{formatValue(e.value, e.format)}</div>
                <div className="row" style={{ gap: 6 }}>
                  {e.previous !== undefined && e.previous !== null && <><Delta value={e.value} previous={e.previous} format={e.format} /><span className="c">السابق {formatValue(e.previous, e.format)}</span></>}
                </div>
                {e.context && <div className="c">{e.context}</div>}
              </div>
            ))}
          </div>
        )}
      </div>

      {inv.charts.length > 0 && (
        <div className="grid g-2">
          {inv.charts.map((c) => (
            <Card key={c.id} title={c.title} subtitle={<span className="layer-tag data" style={{ margin: 0 }}>بيانات</span>}>
              <SpecChart spec={c} />
            </Card>
          ))}
        </div>
      )}

      <div className="grid g-2">
        <div className="layer">
          <div className="layer-tag data">أين تحدث المشكلة؟</div>
          {inv.where.length === 0 ? <Empty title="لا تركّز واضح" /> : (
            <table className="table"><tbody>{inv.where.map((w, i) => (
              <tr key={i}><td className="muted" style={{ width: 90 }}>{w.dimension}</td><td style={{ fontWeight: 600 }}>{w.value}</td><td className="text-2" style={{ fontSize: 12.5 }}>{w.detail}</td></tr>
            ))}</tbody></table>
          )}
        </div>
        <div className="layer">
          <div className="layer-tag data">مقارنة</div>
          {!inv.comparison ? <Empty title="لا مقارنة لهذا السؤال" /> : (
            <>
              <div style={{ fontWeight: 600, marginBottom: 6 }}>{inv.comparison.title}</div>
              <table className="table">
                <thead><tr><th></th><th className="n">{inv.comparison.aLabel}</th><th className="n">{inv.comparison.bLabel}</th></tr></thead>
                <tbody>{inv.comparison.rows.map((r) => (
                  <tr key={r.label}><td>{r.label}</td><td className="n">{formatValue(r.a, r.format)}</td><td className="n">{formatValue(r.b, r.format)}</td></tr>
                ))}</tbody>
              </table>
            </>
          )}
        </div>
      </div>

      <div className="grid g-2">
        <div className="layer">
          <div className="layer-tag interp">التفسير — ما تقوله البيانات</div>
          {inv.interpretation.length === 0 ? <span className="muted">لا تفسير إضافي.</span> : <ul>{inv.interpretation.map((x, i) => <li key={i}>{x}</li>)}</ul>}
        </div>
        <div className="layer">
          <div className="layer-tag hypo"><FlaskConical size={13} /> فرضيات — ليست حقائق</div>
          {inv.hypotheses.length === 0 ? <span className="muted">لا فرضيات.</span> : <ul>{inv.hypotheses.map((x, i) => <li key={i}>{x}</li>)}</ul>}
        </div>
      </div>

      <div className="layer">
        <div className="layer-tag hypo">ما الذي يستحق التحقيق؟</div>
        {inv.investigate.length === 0 ? <span className="muted">—</span> : (
          <div className="grid g-2">
            {inv.investigate.map((l, i) => (
              <div key={i} className="note"><b>{l.title}</b><div className="text-2" style={{ fontSize: 13 }}>{l.why}</div></div>
            ))}
          </div>
        )}
        <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>اقتراحات للفحص لا قرارات — القرار للـProduct Manager.</p>
      </div>

      <Card flush title={`المستخدمون المتأثرون · ${inv.affectedLabel}`} subtitle={`${inv.affectedUsers.length} مستخدمًا — اضغط لفتح User 360`}>
        <UserTable rows={inv.affectedUsers} evidenceLabel="" compact empty="لا مستخدمين مرتبطين بهذا السؤال" />
      </Card>
    </div>
  )
}
