import { CheckCircle2, ClipboardCheck, Gavel, Pencil, Plus, Search, X } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api } from '../api'
import type { DecisionDetail, DecisionInput, MetricTarget } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { EvaluationView, MetricPicker, SegmentSelect } from '../components/Evaluation'
import { Card, Empty, ErrorState, Loader } from '../components/ui'
import { formatDate, formatDateTime } from '../lib/format'
import { AREAS, KIND_LABEL, REVIEW_LABEL, STATUS_LABEL, VERDICTS, plusDays, sectionLabel, today, verdictLabel } from '../lib/product'
import { useAsync } from '../lib/useAsync'

/**
 * The decision log (brief §11–§12, ADR-132).
 *
 * A proposal and a decision look alike on paper, so the page never lets them
 * be confused: a proposal says so in its badge, and only the Owner can turn
 * one into a decision. What happened afterwards is in two layers — the
 * measurement the backend computed, and the verdict a person wrote.
 */
export function DecisionsPage() {
  const { id } = useParams()
  if (id === 'new') return <DecisionForm />
  return id ? <DecisionView id={id} /> : <DecisionList />
}

function StatusBadge({ status }: { status: string }) {
  const [label, tone] = STATUS_LABEL[status] ?? [status, '']
  return <span className={`badge ${tone}`}>{label}</span>
}

function VerdictBadge({ verdict }: { verdict: string | null }) {
  const v = verdictLabel(verdict)
  return v ? <span className={`badge ${v[2]}`}>{v[1]}</span> : null
}

function DecisionList() {
  const navigate = useNavigate()
  const [status, setStatus] = useState<string>('')
  const [area, setArea] = useState('')
  const [q, setQ] = useState('')
  const [query, setQuery] = useState('')
  const state = useAsync(() => api.decisions({ status: status || undefined, area: area || undefined, q: query || undefined }), [status, area, query])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>سجل القرارات</h1>
          <p>كل قرار منتج: المشكلة، الأدلة، الفرضية، ما اتُّفق عليه، وما حدث بعده. الاقتراح لا يصبح قرارًا إلا باعتماد المالك.</p>
        </div>
        <Link to="/decisions/new" className="btn primary"><Plus /> اقتراح قرار</Link>
      </div>
      <Loader state={state}>{(d) => (
        <>
          <div className="kpis">
            {([['PROPOSED', 'اقتراحات بانتظار الاعتماد', d.counts.proposed], ['active', 'قرارات نشطة', d.counts.active], ['due', 'حان تقييمها', d.counts.due], ['CLOSED', 'مكتملة', d.counts.closed]] as const).map(([k, l, n]) => (
              <div key={k} className={`kpi clickable ${k === 'due' && n > 0 ? 'warn' : ''}`} role="button" tabIndex={0} aria-pressed={status === k}
                style={status === k ? { borderColor: 'var(--brand)' } : undefined}
                onClick={() => setStatus(status === k ? '' : k)} onKeyDown={(e) => { if (e.key === 'Enter') setStatus(status === k ? '' : k) }}>
                <div className="kpi-label">{l}</div><div className="kpi-value">{n}</div>
              </div>
            ))}
          </div>
          <Card flush actions={
            <div className="row wrap" style={{ gap: 6 }}>
              <form className="row" onSubmit={(e) => { e.preventDefault(); setQuery(q) }}>
                <input className="input" placeholder="ابحث في القرارات…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="بحث" />
                <button className="btn sm" type="submit" aria-label="بحث"><Search /></button>
              </form>
              <select className="select" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="الحالة">
                <option value="">كل الحالات</option>
                <option value="active">النشطة (معتمدة أو منفّذة)</option>
                <option value="due">حان تقييمها</option>
                {Object.entries(STATUS_LABEL).map(([k, [l]]) => <option key={k} value={k}>{l}</option>)}
              </select>
              <select className="select" value={area} onChange={(e) => setArea(e.target.value)} aria-label="المجال">
                <option value="">كل المجالات</option>
                {AREAS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
              </select>
            </div>
          } title="القرارات" subtitle={`${d.items.length} قرارًا`}>
            {d.items.length === 0 ? <Empty title="لا قرارات" text="ابدأ من تحقيق في «تحليل واستفسار» ثم «حوّله إلى قرار»، أو اقترح قرارًا مباشرة." /> : (
              <div className="table-wrap">
                <table className="table">
                  <thead><tr><th>القرار</th><th>المجال</th><th>الحالة</th><th>المسؤول</th><th>التنفيذ</th><th>التقييم</th><th>النتيجة</th></tr></thead>
                  <tbody>
                    {d.items.map((r) => {
                      const [rl, rt] = REVIEW_LABEL[r.reviewState] ?? ['', '']
                      return (
                        <tr key={r.id} className="clickable" onClick={() => navigate(`/decisions/${r.id}`)}>
                          <td><Link to={`/decisions/${r.id}`} style={{ fontWeight: 500 }}>{r.title}</Link><div className="muted" style={{ fontSize: 11.5 }}>{r.author} · {formatDate(r.createdAt, true)}</div></td>
                          <td>{sectionLabel(r.area)}</td>
                          <td><StatusBadge status={r.status} /></td>
                          <td>{r.assignee ?? <span className="muted">—</span>}</td>
                          <td>{r.implementedOn ? formatDate(r.implementedOn) : <span className="muted">—</span>}</td>
                          <td>{r.reviewOn ? <>{formatDate(r.reviewOn)} <span className={`badge ${rt}`}>{rl}</span></> : <span className="muted">—</span>}</td>
                          <td><VerdictBadge verdict={r.verdict} /></td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
        </>
      )}</Loader>
    </>
  )
}

function DecisionView({ id }: { id: string }) {
  const state = useAsync(() => api.decision(id), [id])
  const [editing, setEditing] = useState(false)
  if (editing && state.data) return <DecisionForm existing={state.data} onDone={() => { setEditing(false); state.reload() }} />
  return (
    <>
      <Link to="/decisions" className="muted" style={{ fontSize: 13 }}>→ سجل القرارات</Link>
      <Loader state={state}>{(d) => <DecisionBody d={d} reload={state.reload} onEdit={() => setEditing(true)} />}</Loader>
    </>
  )
}

function DecisionBody({ d, reload, onEdit }: { d: DecisionDetail; reload: () => void; onEdit: () => void }) {
  const { meta } = useAuth()
  const owner = meta?.me.role === 'OWNER'
  const x = d.decision
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<Error | null>(null)
  const [verdict, setVerdict] = useState('')
  const [note, setNote] = useState(x.actualOutcome ?? '')
  const [shipped, setShipped] = useState(today())
  const [reviewOn, setReviewOn] = useState(x.reviewOn ?? '')

  const act = async (action: string, extra: Record<string, unknown> = {}) => {
    setBusy(true); setError(null)
    try { await api.transition(x.id, { action, ...extra }); reload() } catch (e) { setError(e as Error) } finally { setBusy(false) }
  }
  const editable = (x.status === 'PROPOSED' || owner) && x.status !== 'CLOSED' && x.status !== 'REJECTED'

  return (
    <div className="stack lg">
      <div className="page-head">
        <div>
          <div className="row wrap" style={{ gap: 6, marginBottom: 6 }}>
            <StatusBadge status={x.status} /><VerdictBadge verdict={x.verdict} />
            <span className="badge">{sectionLabel(x.area)}</span>
            {x.reviewState === 'due' && <span className="badge warn">حان موعد التقييم</span>}
            {x.reviewState === 'measured' && <span className="badge warn">قِيس تلقائيًا — بانتظار الحكم</span>}
          </div>
          <h1>{x.title}</h1>
          <p className="muted">اقترحه {x.author} · {formatDateTime(x.createdAt)}{x.approvedBy && <> · اعتمده {x.approvedBy} {x.approvedAt && formatDate(x.approvedAt, true)}</>}</p>
        </div>
        {editable && <button className="btn" onClick={onEdit}><Pencil /> تعديل</button>}
      </div>
      {error && <div className="card"><ErrorState error={error} /></div>}

      {x.status === 'PROPOSED' && (
        <div className="layer">
          <div className="layer-tag hypo">اقتراح — ليس قرارًا معتمدًا بعد</div>
          {owner ? (
            <div className="row wrap" style={{ gap: 8 }}>
              <button className="btn primary" disabled={busy} onClick={() => act('approve')}><CheckCircle2 /> اعتماد القرار</button>
              <button className="btn" disabled={busy} onClick={() => act('reject', { note: note || null })}><X /> رفض</button>
            </div>
          ) : <span className="muted">الاعتماد للمالك فقط.</span>}
        </div>
      )}

      <div className="grid g-2">
        <Card title="المشكلة والأدلة">
          <div className="stack">
            <Field label="المشكلة" text={x.problem} />
            <Field label="الأدلة" text={x.evidence} />
            {d.inquiry && (
              <div className="note">
                <div className="muted" style={{ fontSize: 12 }}>التحقيق الذي أدى إليه · {formatDate(d.inquiry.createdAt, true)}</div>
                <Link to={`/inquiry/${d.inquiry.id}`} style={{ fontWeight: 500 }}>{d.inquiry.question}</Link>
                <div className="text-2" style={{ fontSize: 12.5 }}>{d.inquiry.summary}</div>
              </div>
            )}
            {x.baselineFrom && <Field label="الفترة التي حُللت" text={`${formatDate(x.baselineFrom)} — ${formatDate(x.baselineTo)}`} />}
            <Field label="الفئة المتأثرة" text={x.segmentLabel ?? 'كل المتعلمين'} />
          </div>
        </Card>
        <Card title="الفرضية والقرار">
          <div className="stack">
            <div className="layer-tag hypo" style={{ margin: 0 }}>فرضية</div>
            <p style={{ margin: 0 }}>{x.hypothesis ?? <span className="muted">—</span>}</p>
            <Field label="الإجراء المتفق عليه" text={x.action} />
            <Field label="المسؤول عن التنفيذ" text={x.assignee} />
            <Field label="النتيجة المتوقعة" text={x.expectedOutcome} />
          </div>
        </Card>
      </div>

      <Card title={<span className="row"><ClipboardCheck size={16} />التنفيذ والتقييم</span>}
        subtitle={x.implementedOn ? `نُفّذ ${formatDate(x.implementedOn, true)}` : 'لم يُسجَّل التنفيذ بعد'}>
        <div className="row wrap" style={{ gap: 12, alignItems: 'end' }}>
          {(x.status === 'APPROVED' || x.status === 'IMPLEMENTED') && (
            <div className="field"><label>تاريخ التنفيذ الفعلي</label>
              <div className="row"><input className="input ltr" type="date" value={shipped} onChange={(e) => setShipped(e.target.value)} />
                <button className="btn" disabled={busy} onClick={() => act('implement', { on: shipped })}>تسجيل التنفيذ</button></div>
            </div>
          )}
          {x.status !== 'CLOSED' && x.status !== 'REJECTED' && (
            <div className="field"><label>موعد التقييم</label>
              <div className="row"><input className="input ltr" type="date" value={reviewOn} onChange={(e) => setReviewOn(e.target.value)} />
                <button className="btn" disabled={busy} onClick={() => act('reschedule', { reviewOn: reviewOn || null })}>حفظ الموعد</button></div>
            </div>
          )}
        </div>
      </Card>

      {d.review && x.status !== 'CLOSED' && (
        <div>
          <div className="section-title">القياس عند موعد التقييم</div>
          <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
            أخذه النظام تلقائيًا بعد مرور {x.reviewOn && formatDate(x.reviewOn, true)}{d.reviewedAt && ` (${formatDateTime(d.reviewedAt)})`} على الفترة كاملة، وحفظه كما هو. قياس لا حكم — القرار يبقى مفتوحًا حتى يغلقه المالك.
          </p>
          <EvaluationView e={d.review} />
        </div>
      )}
      <div>
        <div className="section-title">{d.review && x.status !== 'CLOSED' ? 'القياس الحي الآن' : 'النتائج'}</div>
        <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>{d.evaluationNote}</p>
        {d.evaluation && <EvaluationView e={d.evaluation} />}
      </div>

      {(x.status === 'IMPLEMENTED' || x.status === 'APPROVED' || x.status === 'CLOSED') && (
        <Card title={<span className="row"><Gavel size={16} />التقييم النهائي</span>} subtitle="حكم يكتبه شخص — منفصل عن القياس أعلاه">
          {x.status === 'CLOSED' ? (
            <div className="stack"><VerdictBadge verdict={x.verdict} /><p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{x.actualOutcome ?? '—'}</p></div>
          ) : (
            <div className="stack">
              <textarea className="textarea" rows={3} placeholder="النتيجة الفعلية: ماذا حدث، وما الذي يمكن استنتاجه بثقة؟" value={note} onChange={(e) => setNote(e.target.value)} maxLength={4000} />
              <div className="row wrap" style={{ gap: 8 }}>
                <button className="btn" disabled={busy || !note.trim()} onClick={() => act('outcome', { note })}>حفظ نتيجة مرحلية</button>
                {owner && (
                  <>
                    <select className="select" value={verdict} onChange={(e) => setVerdict(e.target.value)} aria-label="الحكم">
                      <option value="">الحكم…</option>
                      {VERDICTS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                    </select>
                    <button className="btn primary" disabled={busy || !verdict} onClick={() => act('close', { verdict, note: note || null })}>إغلاق القرار وتجميد القياس</button>
                  </>
                )}
              </div>
            </div>
          )}
        </Card>
      )}

      <div className="grid g-2">
        <Card title="التغييرات المرتبطة" flush actions={<Link className="btn sm" to={`/changes/new?decision=${x.id}`}><Plus /> سجّل تغييرًا</Link>}>
          {d.changes.length === 0 ? <Empty title="لا تغييرات مسجلة لهذا القرار" /> : (
            <table className="table"><tbody>{d.changes.map((c) => (
              <tr key={c.id}><td><Link to={`/changes/${c.id}`}>{c.title}</Link></td><td><span className="badge">{KIND_LABEL[c.kind] ?? c.kind}</span>{c.isExperiment && <span className="badge brand">تجربة</span>}</td><td>{formatDate(c.shippedOn)}</td></tr>
            ))}</tbody></table>
          )}
        </Card>
        <Card title="سجل التغييرات الإدارية" flush>
          {d.history.length === 0 ? <Empty title="لا تغييرات" /> : (
            <table className="table"><tbody>{d.history.map((h, i) => (
              <tr key={i}><td className="ltr" style={{ fontSize: 12 }}>{h.action}</td><td className="text-2" style={{ fontSize: 12.5 }}>{h.detail}</td><td>{h.actor}</td><td className="muted" style={{ fontSize: 12 }}>{formatDateTime(h.at)}</td></tr>
            ))}</tbody></table>
          )}
        </Card>
      </div>
    </div>
  )
}

function Field({ label, text }: { label: string; text: string | null | undefined }) {
  return (
    <div>
      <div className="muted" style={{ fontSize: 12 }}>{label}</div>
      <div style={{ whiteSpace: 'pre-wrap' }}>{text || <span className="muted">—</span>}</div>
    </div>
  )
}

/**
 * Propose, or edit, a decision. Opened from an investigation it arrives
 * pre-filled with the question, the reading and the period analysed.
 */
function DecisionForm({ existing, onDone }: { existing?: DecisionDetail; onDone?: () => void }) {
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const dictionary = useAsync(() => api.definitions(), [])
  const seed = useMemo<DecisionInput>(() => existing ? { ...existing.decision } : {
    title: '', area: params.get('area') ?? 'general', problem: params.get('problem') ?? '', action: '',
    evidence: params.get('evidence') ?? '', hypothesis: params.get('hypothesis') ?? '', inquiryId: params.get('inquiry'),
    baselineFrom: params.get('from'), baselineTo: params.get('to'), segment: params.get('segment'),
    metrics: [], reviewOn: plusDays(14),
  }, [existing, params])
  const [f, setF] = useState<DecisionInput>(seed)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<Error | null>(null)
  const set = (patch: Partial<DecisionInput>) => setF((x) => ({ ...x, ...patch }))
  const valid = f.title.trim().length >= 3 && f.problem.trim().length >= 3 && f.action.trim().length >= 3

  const save = async () => {
    setBusy(true); setError(null)
    try {
      if (existing) { await api.editDecision(existing.decision.id, f); onDone?.() }
      else { const { id } = await api.proposeDecision(f); navigate(`/decisions/${id}`, { replace: true }) }
    } catch (e) { setError(e as Error) } finally { setBusy(false) }
  }

  return (
    <div className="stack lg">
      <Link to="/decisions" className="muted" style={{ fontSize: 13 }}>→ سجل القرارات</Link>
      <div className="page-head"><div>
        <h1>{existing ? 'تعديل القرار' : 'اقتراح قرار'}</h1>
        <p>{existing ? 'كل تعديل يُسجَّل في سجل التغييرات.' : 'يُحفظ كاقتراح. الاعتماد للمالك.'}</p>
      </div></div>
      {f.inquiryId && <div className="note">مرتبط بالتحقيق <Link to={`/inquiry/${f.inquiryId}`}>{f.inquiryId.slice(0, 8)}</Link> — الأدلة والفترة مأخوذة منه.</div>}
      <div className="grid g-2" style={{ alignItems: 'start' }}>
        <Card title="المشكلة">
          <div className="stack">
            <div className="field"><label>عنوان القرار</label><input className="input" value={f.title} onChange={(e) => set({ title: e.target.value })} maxLength={200} /></div>
            <div className="field"><label>المجال</label>
              <select className="select" value={f.area} onChange={(e) => set({ area: e.target.value })}>{AREAS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></div>
            <div className="field"><label>المشكلة الأصلية</label><textarea className="textarea" rows={3} value={f.problem} onChange={(e) => set({ problem: e.target.value })} maxLength={4000} /></div>
            <div className="field"><label>الأدلة</label><textarea className="textarea" rows={4} value={f.evidence ?? ''} onChange={(e) => set({ evidence: e.target.value })} maxLength={4000} /></div>
            <div className="field"><label>الفترة التي حُللت (خط الأساس)</label>
              <div className="row"><input className="input ltr" type="date" value={f.baselineFrom ?? ''} onChange={(e) => set({ baselineFrom: e.target.value || null })} />
                <span className="muted">—</span><input className="input ltr" type="date" value={f.baselineTo ?? ''} onChange={(e) => set({ baselineTo: e.target.value || null })} /></div>
              <span className="muted" style={{ fontSize: 12 }}>تُركت فارغة؟ يُقاس خط الأساس على الأسبوعين قبل التنفيذ.</span></div>
            <div className="field"><label>الفئة المتأثرة</label><SegmentSelect value={f.segment} onChange={(v) => set({ segment: v })} label="الفئة" /></div>
          </div>
        </Card>
        <Card title="القرار">
          <div className="stack">
            <div className="field"><label>التفسير والفرضية</label><textarea className="textarea" rows={3} value={f.hypothesis ?? ''} onChange={(e) => set({ hypothesis: e.target.value })} maxLength={4000} /></div>
            <div className="field"><label>الإجراء المتفق عليه</label><textarea className="textarea" rows={3} value={f.action} onChange={(e) => set({ action: e.target.value })} maxLength={4000} /></div>
            <div className="field"><label>المسؤول عن التنفيذ</label><input className="input" value={f.assignee ?? ''} onChange={(e) => set({ assignee: e.target.value })} maxLength={100} /></div>
            <div className="field"><label>النتيجة المتوقعة</label><textarea className="textarea" rows={2} value={f.expectedOutcome ?? ''} onChange={(e) => set({ expectedOutcome: e.target.value })} maxLength={2000} /></div>
            <div className="field"><label>المقاييس المستهدفة</label>
              {dictionary.data ? <MetricPicker value={f.metrics ?? []} onChange={(m: MetricTarget[]) => set({ metrics: m })} catalogue={dictionary.data.metrics} /> : <span className="muted">جارٍ تحميل القاموس…</span>}</div>
            <div className="field"><label>موعد مراجعة النتائج</label><input className="input ltr" type="date" value={f.reviewOn ?? ''} onChange={(e) => set({ reviewOn: e.target.value || null })} /></div>
          </div>
        </Card>
      </div>
      {error && <div className="card"><ErrorState error={error} /></div>}
      <div className="row" style={{ gap: 8 }}>
        <button className="btn primary" disabled={busy || !valid} onClick={save}>{existing ? 'حفظ التعديل' : 'حفظ الاقتراح'}</button>
        {existing && <button className="btn" onClick={onDone}>إلغاء</button>}
        {!valid && <span className="muted" style={{ fontSize: 12.5 }}>العنوان والمشكلة والإجراء مطلوبة.</span>}
      </div>
    </div>
  )
}
