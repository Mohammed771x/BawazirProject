import { FlaskConical, Pencil, Plus, Snowflake } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api } from '../api'
import type { ChangeDetail, ChangeInput, MetricTarget } from '../api/types'
import { EvaluationView, MetricPicker, SegmentSelect } from '../components/Evaluation'
import { Card, Empty, ErrorState, Loader, Tabs } from '../components/ui'
import { formatDate, formatDateTime } from '../lib/format'
import { KIND_LABEL, VERDICTS, today, verdictLabel } from '../lib/product'
import { useAsync } from '../lib/useAsync'

/**
 * Experiments and the change log (brief §12–§13, ADR-132).
 *
 * One list, because an experiment is a change with a hypothesis and a group
 * to compare against — and the change that explains a dip must be findable
 * from wherever someone is looking at the dip. Only changes that could move
 * what learners do belong here, not every commit.
 */
export function ChangesPage() {
  const { id } = useParams()
  if (id === 'new') return <ChangeForm />
  return id ? <ChangeView id={id} /> : <ChangeList />
}

function ChangeList() {
  const navigate = useNavigate()
  const [tab, setTab] = useState<'all' | 'experiments' | 'changes'>('all')
  const [kind, setKind] = useState('')
  const state = useAsync(() => api.changes({ kind: kind || undefined, experiments: tab === 'all' ? undefined : tab === 'experiments' }), [tab, kind])
  return (
    <>
      <div className="page-head">
        <div>
          <h1>التجارب والتغييرات</h1>
          <p>التغييرات التي قد تؤثر في سلوك المستخدمين أو تعلمهم، وما حدث قبلها وبعدها. التجربة تغيير له فرضية ومجموعة مقارنة.</p>
        </div>
        <Link to="/changes/new" className="btn primary"><Plus /> تسجيل تغيير أو تجربة</Link>
      </div>
      <Card flush title="السجل" actions={
        <div className="row wrap" style={{ gap: 6 }}>
          <Tabs value={tab} onChange={setTab} items={[{ value: 'all', label: 'الكل' }, { value: 'experiments', label: 'التجارب' }, { value: 'changes', label: 'التغييرات' }]} />
          <select className="select" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="النوع">
            <option value="">كل الأنواع</option>
            {Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}
          </select>
        </div>
      }>
        <Loader state={state}>{(d) => d.items.length === 0 ? <Empty title="لا شيء مسجل" text="سجّل أي تغيير له أثر محتمل على التعلم أو التجربة ليُقاس قبله وبعده." /> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>التغيير</th><th>النوع</th><th>التنفيذ</th><th>الإصدار</th><th>الفئة</th><th>المقاييس</th><th>النتيجة</th></tr></thead>
              <tbody>{d.items.map((c) => {
                const v = verdictLabel(c.verdict)
                return (
                  <tr key={c.id} className="clickable" onClick={() => navigate(`/changes/${c.id}`)}>
                    <td><Link to={`/changes/${c.id}`} style={{ fontWeight: 500 }}>{c.title}</Link>{c.isExperiment && <> <span className="badge brand"><FlaskConical /> تجربة</span></>}</td>
                    <td>{KIND_LABEL[c.kind] ?? c.kind}</td>
                    <td>{formatDate(c.shippedOn, true)}</td>
                    <td className="ltr">{c.version ?? '—'}</td>
                    <td style={{ fontSize: 12.5 }}>{c.segment ?? 'الكل'}{c.comparisonSegment && <span className="muted"> مقابل {c.comparisonSegment}</span>}</td>
                    <td>{c.metrics.length}</td>
                    <td>{v ? <span className={`badge ${v[2]}`}>{v[1]}</span> : <span className="muted">قيد القياس</span>}</td>
                  </tr>
                )
              })}</tbody>
            </table>
          </div>
        )}</Loader>
      </Card>
    </>
  )
}

function ChangeView({ id }: { id: string }) {
  const state = useAsync(() => api.change(id), [id])
  const [editing, setEditing] = useState(false)
  if (editing && state.data) return <ChangeForm existing={state.data} onDone={() => { setEditing(false); state.reload() }} />
  return (
    <>
      <Link to="/changes" className="muted" style={{ fontSize: 13 }}>→ التجارب والتغييرات</Link>
      <Loader state={state}>{(d) => <ChangeBody d={d} reload={state.reload} onEdit={() => setEditing(true)} />}</Loader>
    </>
  )
}

function ChangeBody({ d, reload, onEdit }: { d: ChangeDetail; reload: () => void; onEdit: () => void }) {
  const c = d.change
  const [result, setResult] = useState(c.result ?? '')
  const [sideEffects, setSideEffects] = useState(c.sideEffects ?? '')
  const [recommendation, setRecommendation] = useState(c.recommendation ?? '')
  const [verdict, setVerdict] = useState(c.verdict ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<Error | null>(null)
  const [view, setView] = useState<'live' | 'frozen'>(d.frozen ? 'frozen' : 'live')

  const conclude = async (freezeEvaluation: boolean) => {
    setBusy(true); setError(null)
    try {
      await api.concludeChange(c.id, { result, sideEffects, recommendation, verdict: verdict || null, freezeEvaluation })
      reload()
    } catch (e) { setError(e as Error) } finally { setBusy(false) }
  }

  return (
    <div className="stack lg">
      <div className="page-head">
        <div>
          <div className="row wrap" style={{ gap: 6, marginBottom: 6 }}>
            <span className="badge">{KIND_LABEL[c.kind] ?? c.kind}</span>
            {c.isExperiment && <span className="badge brand"><FlaskConical /> تجربة</span>}
            {c.version && <span className="badge ltr">{c.version}</span>}
          </div>
          <h1>{c.title}</h1>
          <p className="muted">نُفّذ {formatDate(c.shippedOn, true)} · سجّله {c.author} · {formatDateTime(c.createdAt)}</p>
        </div>
        <button className="btn" onClick={onEdit}><Pencil /> تعديل</button>
      </div>

      <div className="grid g-2">
        <Card title="ما الذي تغير؟">
          <div className="stack">
            <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{c.description}</p>
            {c.problem && <><div className="muted" style={{ fontSize: 12 }}>المشكلة</div><p style={{ margin: 0 }}>{c.problem}</p></>}
            {d.decision && <div className="note">القرار المرتبط: <Link to={`/decisions/${d.decision.id}`}>{d.decision.title}</Link></div>}
          </div>
        </Card>
        <Card title="الفرضية والفئات">
          <div className="stack">
            <div className="layer-tag hypo" style={{ margin: 0 }}>فرضية</div>
            <p style={{ margin: 0 }}>{c.hypothesis ?? <span className="muted">—</span>}</p>
            <div className="muted" style={{ fontSize: 12 }}>الفئة المستهدفة</div><div>{c.segmentLabel ?? 'كل المتعلمين'}</div>
            <div className="muted" style={{ fontSize: 12 }}>مجموعة المقارنة</div><div>{c.comparisonLabel ?? <span className="muted">لا توجد — النتيجة ارتباط زمني فقط</span>}</div>
            <div className="muted" style={{ fontSize: 12 }}>نافذة القياس</div><div>{c.windowDays} يومًا قبل التنفيذ و{c.windowDays} بعده</div>
          </div>
        </Card>
      </div>

      <div>
        <div className="row between wrap">
          <div className="section-title">قبل وبعد</div>
          {d.frozen && <Tabs value={view} onChange={setView} items={[{ value: 'frozen', label: 'المجمّد عند الاستنتاج' }, { value: 'live', label: 'الحي الآن' }]} />}
        </div>
        <EvaluationView e={view === 'frozen' && d.frozen ? d.frozen : d.evaluation} />
      </div>

      <Card title="النتيجة والتوصية" subtitle="ما يكتبه الفريق — منفصل عن القياس أعلاه">
        <div className="stack">
          <div className="field"><label>النتيجة</label><textarea className="textarea" rows={3} value={result} onChange={(e) => setResult(e.target.value)} maxLength={4000} /></div>
          <div className="field"><label>الآثار الجانبية</label><textarea className="textarea" rows={2} value={sideEffects} onChange={(e) => setSideEffects(e.target.value)} maxLength={2000} /></div>
          <div className="field"><label>التوصية التالية</label><textarea className="textarea" rows={2} value={recommendation} onChange={(e) => setRecommendation(e.target.value)} maxLength={2000} /></div>
          <div className="row wrap" style={{ gap: 8 }}>
            <select className="select" value={verdict} onChange={(e) => setVerdict(e.target.value)} aria-label="الحكم">
              <option value="">بلا حكم بعد</option>
              {VERDICTS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
            </select>
            <button className="btn" disabled={busy} onClick={() => conclude(false)}>حفظ</button>
            <button className="btn primary" disabled={busy || !verdict} onClick={() => conclude(true)}><Snowflake /> حفظ وتجميد القياس</button>
          </div>
          {error && <ErrorState error={error} />}
        </div>
      </Card>

      {d.history.length > 0 && (
        <Card title="سجل التغييرات الإدارية" flush>
          <table className="table"><tbody>{d.history.map((h, i) => (
            <tr key={i}><td className="ltr" style={{ fontSize: 12 }}>{h.action}</td><td className="text-2" style={{ fontSize: 12.5 }}>{h.detail}</td><td>{h.actor}</td><td className="muted" style={{ fontSize: 12 }}>{formatDateTime(h.at)}</td></tr>
          ))}</tbody></table>
        </Card>
      )}
    </div>
  )
}

function ChangeForm({ existing, onDone }: { existing?: ChangeDetail; onDone?: () => void }) {
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const dictionary = useAsync(() => api.definitions(), [])
  const decisions = useAsync(() => api.decisions({}), [])
  const seed = useMemo<ChangeInput>(() => existing ? { ...existing.change } : {
    title: '', kind: 'other', isExperiment: params.get('experiment') === '1', description: '', shippedOn: today(),
    decisionId: params.get('decision'), metrics: [], windowDays: 14,
  }, [existing, params])
  const [f, setF] = useState<ChangeInput>(seed)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<Error | null>(null)
  const set = (patch: Partial<ChangeInput>) => setF((x) => ({ ...x, ...patch }))
  const valid = f.title.trim().length >= 3 && f.description.trim().length >= 3 && !!f.shippedOn

  const save = async () => {
    setBusy(true); setError(null)
    try {
      if (existing) { await api.editChange(existing.change.id, f); onDone?.() }
      else { const { id } = await api.recordChange(f); navigate(`/changes/${id}`, { replace: true }) }
    } catch (e) { setError(e as Error) } finally { setBusy(false) }
  }

  return (
    <div className="stack lg">
      <Link to="/changes" className="muted" style={{ fontSize: 13 }}>→ التجارب والتغييرات</Link>
      <div className="page-head"><div><h1>{existing ? 'تعديل' : 'تسجيل تغيير أو تجربة'}</h1>
        <p>سجّل ما له أثر محتمل على المتعلمين فقط — ليس كل تعديل برمجي.</p></div></div>
      <div className="grid g-2" style={{ alignItems: 'start' }}>
        <Card title="التغيير">
          <div className="stack">
            <label className="row" style={{ gap: 8 }}><input type="checkbox" checked={f.isExperiment} onChange={(e) => set({ isExperiment: e.target.checked })} /> هذه تجربة (لها فرضية ومجموعة مقارنة)</label>
            <div className="field"><label>العنوان</label><input className="input" value={f.title} onChange={(e) => set({ title: e.target.value })} maxLength={200} /></div>
            <div className="field"><label>النوع</label>
              <select className="select" value={f.kind} onChange={(e) => set({ kind: e.target.value })}>{Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></div>
            <div className="field"><label>ما الذي تغير؟</label><textarea className="textarea" rows={3} value={f.description} onChange={(e) => set({ description: e.target.value })} maxLength={4000} /></div>
            <div className="field"><label>المشكلة المراد حلها</label><textarea className="textarea" rows={2} value={f.problem ?? ''} onChange={(e) => set({ problem: e.target.value })} maxLength={4000} /></div>
            <div className="field"><label>الفرضية</label><textarea className="textarea" rows={2} value={f.hypothesis ?? ''} onChange={(e) => set({ hypothesis: e.target.value })} maxLength={4000} /></div>
            <div className="row wrap" style={{ gap: 12 }}>
              <div className="field"><label>تاريخ التنفيذ</label><input className="input ltr" type="date" value={f.shippedOn} onChange={(e) => set({ shippedOn: e.target.value })} /></div>
              <div className="field"><label>الإصدار (تطبيق أو محتوى)</label><input className="input ltr" value={f.version ?? ''} onChange={(e) => set({ version: e.target.value })} maxLength={64} /></div>
            </div>
          </div>
        </Card>
        <Card title="القياس">
          <div className="stack">
            <div className="field"><label>الفئة المستهدفة</label><SegmentSelect value={f.segment} onChange={(v) => set({ segment: v })} label="الفئة" /></div>
            <div className="field"><label>مجموعة المقارنة</label><SegmentSelect value={f.comparisonSegment} onChange={(v) => set({ comparisonSegment: v })} label="المقارنة" allowEmpty="بلا مجموعة مقارنة" /></div>
            <div className="field"><label>أيام القياس على كل جانب</label><input className="input ltr" style={{ width: 100 }} type="number" min={1} max={180} value={f.windowDays ?? 14} onChange={(e) => set({ windowDays: Number(e.target.value) })} /></div>
            <div className="field"><label>القرار المرتبط</label>
              <select className="select" value={f.decisionId ?? ''} onChange={(e) => set({ decisionId: e.target.value || null })}>
                <option value="">بلا قرار</option>
                {(decisions.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.title}</option>)}
              </select></div>
            <div className="field"><label>مؤشرات التقييم</label>
              {dictionary.data ? <MetricPicker value={f.metrics ?? []} onChange={(m: MetricTarget[]) => set({ metrics: m })} catalogue={dictionary.data.metrics} /> : <span className="muted">جارٍ تحميل القاموس…</span>}</div>
          </div>
        </Card>
      </div>
      {error && <div className="card"><ErrorState error={error} /></div>}
      <div className="row" style={{ gap: 8 }}>
        <button className="btn primary" disabled={busy || !valid} onClick={save}>حفظ</button>
        {existing && <button className="btn" onClick={onDone}>إلغاء</button>}
      </div>
    </div>
  )
}
