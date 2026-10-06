import { Check, MessageSquarePlus, RotateCcw } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import type { FeedbackItem } from '../api/types'
import { HBars } from '../components/charts/Charts'
import { Card, Empty, Loader, Tabs } from '../components/ui'
import { CATEGORY_LABEL, formatDateTime } from '../lib/format'
import { useAsync } from '../lib/useAsync'

export function FeedbackPage() {
  const [status, setStatus] = useState('NEW')
  const [category, setCategory] = useState('')
  const state = useAsync(() => api.feedback(status === 'ALL' ? undefined : status, category || undefined), [status, category])
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Feedback</h1>
          <p>ما كتبه المستخدمون من داخل التطبيق — منظمًا بالتصنيف، مع المشكلات المتكررة وملاحظات الفريق الداخلية.</p>
        </div>
        <Tabs value={status} onChange={setStatus} items={[{ value: 'NEW', label: 'جديدة' }, { value: 'HANDLED', label: 'تمت معالجتها' }, { value: 'ALL', label: 'الكل' }]} />
      </div>
      <Loader state={state}>{(d) => (
        <>
          <div className="kpis">
            <div className="kpi"><div className="kpi-label">جديدة</div><div className="kpi-value">{d.summary.unread}</div></div>
            <div className="kpi"><div className="kpi-label">آخر 7 أيام</div><div className="kpi-value">{d.summary.lastWeek}</div></div>
            <div className="kpi"><div className="kpi-label">الأكثر بلاغًا</div><div className="kpi-value" style={{ fontSize: 20 }}>{CATEGORY_LABEL[d.summary.byCategory[0]?.category] ?? '—'}</div></div>
            <div className="kpi"><div className="kpi-label">الإجمالي</div><div className="kpi-value">{d.summary.total}</div></div>
          </div>
          <div className="grid g-main" style={{ alignItems: 'start' }}>
            <div className="stack lg">
              {d.items.length === 0 ? <div className="card"><Empty title="لا ملاحظات هنا" /></div>
                : d.items.map((f) => <FeedbackCard key={f.id} item={f} onChange={state.reload} />)}
            </div>
            <div className="stack lg">
              <Card title="حسب التصنيف" subtitle="اضغط تصنيفًا لحصر القائمة">
                <HBars unit="int" items={d.summary.byCategory.map((c) => ({
                  label: CATEGORY_LABEL[c.category] ?? c.category, value: c.count, sub: `${c.unread} جديدة`,
                  color: category === c.category ? 'var(--series-2)' : 'var(--series-1)',
                  onClick: () => setCategory(category === c.category ? '' : c.category),
                }))} />
                {category && <button className="btn ghost sm" style={{ marginTop: 8 }} onClick={() => setCategory('')}>كل التصنيفات</button>}
              </Card>
              <Card title="مشكلات متكررة" subtitle="كلمات تتكرر في أكثر من ملاحظة — بلا تفسير آلي للنص">
                {d.repeated.length === 0 ? <Empty title="لا تكرار بعد" /> : (
                  <div className="row wrap" style={{ gap: 6 }}>
                    {d.repeated.map((r) => <span key={r.term} className="badge brand">{r.term} · {r.messages}</span>)}
                  </div>
                )}
              </Card>
            </div>
          </div>
        </>
      )}</Loader>
    </>
  )
}

function FeedbackCard({ item: f, onChange }: { item: FeedbackItem; onChange: () => void }) {
  const [note, setNote] = useState('')
  const [writing, setWriting] = useState(false)
  const [busy, setBusy] = useState(false)
  const run = async (fn: () => Promise<void>) => {
    setBusy(true)
    try { await fn(); onChange() } finally { setBusy(false) }
  }
  return (
    <article className={`fb ${f.status === 'NEW' ? 'new' : ''}`}>
      <div className="row between wrap">
        <div className="row wrap">
          {f.user ? <Link to={`/users/${f.user.id}`} style={{ fontWeight: 600 }}>{f.user.name}</Link> : <b>—</b>}
          <span className="badge">{CATEGORY_LABEL[f.category ?? 'UNCATEGORISED']}</span>
          {f.status === 'NEW' ? <span className="badge brand">جديدة</span> : <span className="badge good">تمت معالجتها</span>}
        </div>
        <span className="muted" style={{ fontSize: 12 }}>{formatDateTime(f.createdAt)}{f.appVersion && ` · ${f.platform ?? ''} ${f.appVersion}`}</span>
      </div>
      <div className="fb-body">{f.body}</div>
      {f.notes.map((n) => (
        <div key={n.id} className="note"><span className="muted" style={{ fontSize: 12 }}>ملاحظة داخلية · {n.author} · {formatDateTime(n.createdAt)}</span><div>{n.body}</div></div>
      ))}
      {writing && (
        <div className="stack">
          <textarea className="textarea" rows={2} placeholder="مثال: Investigating Reading difficulty at B2." value={note} onChange={(e) => setNote(e.target.value)} autoFocus />
          <div className="row">
            <button className="btn primary sm" disabled={busy || !note.trim()} onClick={() => run(async () => { await api.addNote(f.id, note.trim()); setNote(''); setWriting(false) })}>حفظ الملاحظة</button>
            <button className="btn ghost sm" onClick={() => setWriting(false)}>إلغاء</button>
          </div>
        </div>
      )}
      <div className="row">
        {!writing && <button className="btn sm" onClick={() => setWriting(true)}><MessageSquarePlus /> ملاحظة داخلية</button>}
        {f.status === 'NEW'
          ? <button className="btn sm" disabled={busy} onClick={() => run(() => api.setFeedbackHandled(f.id, true))}><Check /> تمت المعالجة</button>
          : <button className="btn ghost sm" disabled={busy} onClick={() => run(() => api.setFeedbackHandled(f.id, false))}><RotateCcw /> إعادة فتح</button>}
        {f.user?.phone && <a className="btn ghost sm" href={`https://wa.me/${f.user.phone.replace('+', '')}`} target="_blank" rel="noreferrer">WhatsApp</a>}
      </div>
    </article>
  )
}
