import {
  Archive, ArchiveRestore, Bot, Check, Copy, Database, FileSearch, Gavel, MessageSquarePlus, Pencil, RefreshCw,
  Search, Send, Trash2, X,
} from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api } from '../api'
import type { AiAttachment, AiConversation, AiMessage } from '../api/types'
import { SpecChart } from '../components/charts/Charts'
import { Markdown } from '../components/Markdown'
import { UserTable } from '../components/UserTable'
import { Empty, ErrorState, Tabs } from '../components/ui'
import { formatDate, formatDateTime } from '../lib/format'
import { today } from '../lib/product'
import { useAsync } from '../lib/useAsync'

const SUGGESTIONS = [
  'ما وضع WordOS خلال آخر 7 أيام؟',
  'كم عدد المستخدمين الذين دخلوا اليوم؟',
  'لماذا نسبة إكمال Listening منخفضة؟',
  'أريد مقارنة أداء B1 وB2 في Reading',
  'أفكر في إضافة تلميحات جديدة إلى Spelling. ما رأيك؟',
  'هل نجحت القرارات التي اتخذناها مؤخرًا؟',
]

const TOOL_LABEL: Record<string, string> = {
  health: 'صحة المنتج', metrics: 'مقاييس القاموس', compare: 'مقارنة مجموعتين', skill: 'تحليل مهارة', time: 'الوقت',
  words: 'الكلمات', users: 'المستخدمون', retention: 'الاحتفاظ والتذكر', notifications: 'الإشعارات', behavior: 'السلوك وUX',
  content_versions: 'نسخ المحتوى', technical: 'الأداء التقني', decisions: 'القرارات',
}

/**
 * WordOS AI (ADR-133): a conversation, not a form. The admin writes; the
 * backend lets the model decide whether to read any data, runs only the
 * tools it names, and keeps the conversation. Every answer can show where
 * its numbers came from, and can become an investigation or a proposal —
 * never a decision by itself.
 */
export function AiPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [listTick, setListTick] = useState(0)
  return (
    <div className="chat-shell">
      <ChatSidebar activeId={id} tick={listTick} onChanged={() => setListTick((t) => t + 1)} onDeleted={() => navigate('/ai')} />
      <Conversation key={id ?? 'new'} id={id} onCreated={(cid) => { setListTick((t) => t + 1); navigate(`/ai/${cid}`, { replace: true }) }}
        onAnswered={() => setListTick((t) => t + 1)} />
    </div>
  )
}

// ── Sidebar ────────────────────────────────────────────────────────────────

function groupOf(iso: string) {
  const d = iso.slice(0, 10)
  const t = today()
  const days = (Date.parse(t) - Date.parse(d)) / 864e5
  if (days < 1) return 'اليوم'
  if (days < 2) return 'أمس'
  if (days < 7) return 'آخر 7 أيام'
  if (days < 30) return 'آخر 30 يومًا'
  return 'أقدم'
}

function ChatSidebar({ activeId, tick, onChanged, onDeleted }: { activeId?: string; tick: number; onChanged: () => void; onDeleted: () => void }) {
  const [tab, setTab] = useState<'chats' | 'archived' | 'saved'>('chats')
  const [q, setQ] = useState('')
  const [query, setQuery] = useState('')
  const list = useAsync(() => (tab === 'saved' ? Promise.resolve({ items: [] as AiConversation[] }) : api.aiConversations(query || undefined, tab === 'archived')), [tab, query, tick])
  const saved = useAsync(() => (tab === 'saved' ? api.inquiries() : Promise.resolve({ items: [] })), [tab, tick])
  const [editing, setEditing] = useState<string | null>(null)
  const [title, setTitle] = useState('')

  useEffect(() => {
    const t = setTimeout(() => setQuery(q.trim()), 300)
    return () => clearTimeout(t)
  }, [q])

  const groups = useMemo(() => {
    const out: [string, AiConversation[]][] = []
    for (const c of list.data?.items ?? []) {
      const g = groupOf(c.updatedAt)
      const last = out[out.length - 1]
      if (last && last[0] === g) last[1].push(c)
      else out.push([g, [c]])
    }
    return out
  }, [list.data])

  const rename = async (id: string) => {
    if (title.trim()) await api.aiPatch(id, { title: title.trim() })
    setEditing(null)
    onChanged()
  }
  const archive = async (c: AiConversation) => { await api.aiPatch(c.id, { archived: !c.archived }); onChanged(); if (c.id === activeId) onDeleted() }
  const remove = async (c: AiConversation) => {
    if (!window.confirm(`حذف المحادثة «${c.title}» نهائيًا؟ التحقيقات المحفوظة منها تبقى.`)) return
    await api.aiDelete(c.id)
    onChanged()
    if (c.id === activeId) onDeleted()
  }

  return (
    <aside className="chat-list" aria-label="المحادثات">
      <Link to="/ai" className="btn primary" style={{ justifyContent: 'center' }}><MessageSquarePlus /> محادثة جديدة</Link>
      <label className="chat-search">
        <Search size={15} />
        <input placeholder="ابحث في المحادثات…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="ابحث في المحادثات" />
      </label>
      <Tabs value={tab} onChange={setTab} items={[{ value: 'chats', label: 'المحادثات' }, { value: 'archived', label: 'المؤرشفة' }, { value: 'saved', label: 'التحقيقات' }]} />
      <div className="chat-list-body">
        {tab === 'saved' ? (
          (saved.data?.items ?? []).length === 0 ? <p className="muted" style={{ fontSize: 12.5, padding: 8 }}>لا تحقيقات محفوظة. احفظ أي إجابة كتحقيق من تحتها.</p> :
            saved.data!.items.map((i) => (
              <Link key={i.id} to={`/inquiry/${i.id}`} className="chat-item">
                <span className="chat-title">{i.question}</span>
                <span className="muted" style={{ fontSize: 11.5 }}>
                  {formatDate(i.createdAt, true)}
                  {i.followUpOn && <> · <span className={`badge ${i.followUpOn <= today() ? 'warn' : ''}`}>متابعة {formatDate(i.followUpOn)}</span></>}
                </span>
              </Link>
            ))
        ) : list.error ? <ErrorState error={list.error} onRetry={list.reload} /> : groups.length === 0 && !list.loading ? (
          <p className="muted" style={{ fontSize: 12.5, padding: 8 }}>{query ? 'لا نتائج.' : tab === 'archived' ? 'لا محادثات مؤرشفة.' : 'لا محادثات بعد.'}</p>
        ) : groups.map(([g, items]) => (
          <div key={g}>
            <div className="chat-group">{g}</div>
            {items.map((c) => editing === c.id ? (
              <form key={c.id} className="row" style={{ gap: 4, padding: '4px 6px' }} onSubmit={(e) => { e.preventDefault(); void rename(c.id) }}>
                <input className="input" autoFocus value={title} onChange={(e) => setTitle(e.target.value)} maxLength={120} aria-label="اسم المحادثة" style={{ height: 30 }} />
                <button className="btn ghost sm" type="submit" aria-label="حفظ"><Check /></button>
                <button className="btn ghost sm" type="button" onClick={() => setEditing(null)} aria-label="إلغاء"><X /></button>
              </form>
            ) : (
              <div key={c.id} className={`chat-item ${c.id === activeId ? 'active' : ''}`}>
                <Link to={`/ai/${c.id}`} className="chat-title" title={c.title}>{c.title}</Link>
                <span className="chat-actions">
                  <button className="btn ghost sm" onClick={() => { setEditing(c.id); setTitle(c.title) }} aria-label="إعادة التسمية" title="إعادة التسمية"><Pencil /></button>
                  <button className="btn ghost sm" onClick={() => void archive(c)} aria-label={c.archived ? 'إلغاء الأرشفة' : 'أرشفة'} title={c.archived ? 'إلغاء الأرشفة' : 'أرشفة'}>{c.archived ? <ArchiveRestore /> : <Archive />}</button>
                  <button className="btn ghost sm" onClick={() => void remove(c)} aria-label="حذف" title="حذف"><Trash2 /></button>
                </span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </aside>
  )
}

// ── Conversation ───────────────────────────────────────────────────────────

function Conversation({ id, onCreated, onAnswered }: { id?: string; onCreated: (id: string) => void; onAnswered: () => void }) {
  const loaded = useAsync(() => (id ? api.aiConversation(id) : Promise.resolve(null)), [id])
  const [messages, setMessages] = useState<AiMessage[]>([])
  const [title, setTitle] = useState<string>('')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  // The free model waits out its per-minute limit, so a data question can
  // take most of a minute (ADR-134); after a while the wait says so.
  const [slow, setSlow] = useState(false)
  useEffect(() => {
    setSlow(false)
    if (!busy) return
    const t = window.setTimeout(() => setSlow(true), 15_000)
    return () => window.clearTimeout(t)
  }, [busy])
  const [error, setError] = useState<Error | null>(null)
  const end = useRef<HTMLDivElement>(null)
  const box = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    if (loaded.data) { setMessages(loaded.data.messages); setTitle(loaded.data.conversation.title) }
  }, [loaded.data])
  useEffect(() => { end.current?.scrollIntoView({ block: 'end' }) }, [messages.length, busy])

  const send = async (value?: string) => {
    const message = (value ?? text).trim()
    if (!message || busy) return
    setBusy(true); setError(null); setText('')
    const pending: AiMessage = { id: `pending-${Date.now()}`, role: 'user', content: message, failed: false, createdAt: new Date().toISOString(), inquiryId: null, sources: [], attachments: [] }
    setMessages((m) => [...m, pending])
    try {
      const r = await api.aiSend(message, id)
      setMessages((m) => [...m.filter((x) => x.id !== pending.id), r.user, r.reply])
      setTitle(r.conversation.title)
      if (!id) onCreated(r.conversation.id)
      else onAnswered()
    } catch (e) {
      setError(e as Error)
      setMessages((m) => m.filter((x) => x.id !== pending.id))
      setText(message)
    } finally {
      setBusy(false)
      box.current?.focus()
    }
  }

  const regenerate = async () => {
    if (!id || busy) return
    setBusy(true); setError(null)
    try {
      const r = await api.aiRegenerate(id)
      setMessages((m) => {
        const lastUser = m.map((x) => x.role).lastIndexOf('user')
        const kept = m.filter((x, i) => i <= lastUser || x.inquiryId)
        return [...kept, r.reply]
      })
    } catch (e) { setError(e as Error) } finally { setBusy(false) }
  }

  const lastAssistant = [...messages].reverse().find((m) => m.role === 'assistant')?.id

  return (
    <section className="chat-main">
      <header className="chat-head">
        <Bot size={18} />
        <h1>{id ? title || '…' : 'WordOS AI'}</h1>
      </header>
      <div className="chat-scroll">
        {id && loaded.error && <ErrorState error={loaded.error} onRetry={loaded.reload} />}
        {!id && messages.length === 0 && (
          <div className="chat-welcome">
            <div className="brand-mark" style={{ width: 44, height: 44, fontSize: 20 }}>W</div>
            <h2>بماذا أساعدك في WordOS اليوم؟</h2>
            <p className="muted">اسأل عن أي شيء: أرقام، أسباب، مقارنات، مستخدمون، قرارات سابقة — أو ناقش فكرة. أقرأ البيانات فقط عندما يحتاجها السؤال.</p>
            <div className="chat-suggestions">
              {SUGGESTIONS.map((s) => <button key={s} className="example" onClick={() => void send(s)}>{s}</button>)}
            </div>
          </div>
        )}
        {messages.map((m) => (
          <MessageView key={m.id} m={m} question={questionBefore(messages, m)} canRegenerate={m.id === lastAssistant && !busy}
            onRegenerate={regenerate} onSaved={(inquiryId) => setMessages((all) => all.map((x) => (x.id === m.id ? { ...x, inquiryId } : x)))} />
        ))}
        {busy && (
          <div className="msg ai">
            <div className="msg-avatar"><Bot size={16} /></div>
            <div className="msg-body"><span className="typing"><i /><i /><i /></span> <span className="muted" style={{ fontSize: 12.5 }}>{slow ? 'ما زال يعمل — النموذج المجاني قد يحتاج حتى دقيقة للأسئلة التي تستعلم عن البيانات…' : 'يفكّر، وقد يستعلم عن البيانات…'}</span></div>
          </div>
        )}
        {error && <ErrorState error={error} />}
        <div ref={end} />
      </div>
      <form className="composer" onSubmit={(e) => { e.preventDefault(); void send() }}>
        <textarea ref={box} rows={1} value={text} maxLength={4000} placeholder="اكتب رسالتك… (Enter للإرسال، Shift+Enter لسطر جديد)" aria-label="رسالتك"
          onChange={(e) => { setText(e.target.value); e.target.style.height = 'auto'; e.target.style.height = `${Math.min(220, e.target.scrollHeight)}px` }}
          onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send() } }} />
        <button className="btn primary" type="submit" disabled={busy || !text.trim()} aria-label="إرسال"><Send /></button>
      </form>
    </section>
  )
}

function questionBefore(all: AiMessage[], m: AiMessage) {
  const i = all.indexOf(m)
  for (let j = i - 1; j >= 0; j--) if (all[j].role === 'user') return all[j].content
  return ''
}

function MessageView({ m, question, canRegenerate, onRegenerate, onSaved }: {
  m: AiMessage; question: string; canRegenerate: boolean; onRegenerate: () => void; onSaved: (inquiryId: string) => void
}) {
  const navigate = useNavigate()
  const [showSources, setShowSources] = useState(false)
  const [copied, setCopied] = useState(false)
  const [saving, setSaving] = useState(false)

  if (m.role === 'user') return <div className="msg user"><div className="msg-body">{m.content}</div></div>

  const copy = async () => {
    try { await navigator.clipboard.writeText(m.content); setCopied(true); setTimeout(() => setCopied(false), 1500) } catch { /* clipboard blocked */ }
  }
  const save = async () => {
    if (m.inquiryId) return m.inquiryId
    setSaving(true)
    try { const r = await api.aiSaveInvestigation(m.id); onSaved(r.id); return r.id } finally { setSaving(false) }
  }
  const toDecision = async () => {
    const inquiry = await save()
    const p = new URLSearchParams({
      inquiry, area: 'general', problem: question.slice(0, 1000),
      evidence: m.content.slice(0, 1800),
    })
    navigate(`/decisions/new?${p}`)
  }

  return (
    <div className={`msg ai ${m.failed ? 'failed' : ''}`}>
      <div className="msg-avatar"><Bot size={16} /></div>
      <div className="msg-body">
        {m.failed ? <p className="muted" style={{ margin: 0 }}>{m.content}</p> : <Markdown text={m.content} />}
        {m.attachments.map((a) => <Attachment key={a.id} a={a} />)}
        <div className="msg-tools">
          <button className="btn ghost sm" onClick={() => void copy()} title="نسخ">{copied ? <Check /> : <Copy />}{copied ? 'نُسخ' : 'نسخ'}</button>
          {canRegenerate && <button className="btn ghost sm" onClick={onRegenerate} title="إعادة توليد"><RefreshCw /> إعادة توليد</button>}
          {m.sources.length > 0
            ? <button className="btn ghost sm" onClick={() => setShowSources((s) => !s)} aria-expanded={showSources}><Database /> مصادر التحليل ({m.sources.length})</button>
            : !m.failed && <span className="muted" style={{ fontSize: 11.5 }}>دون استعلام</span>}
          {!m.failed && (m.inquiryId
            ? <Link className="btn ghost sm" to={`/inquiry/${m.inquiryId}`}><FileSearch /> محفوظ كتحقيق</Link>
            : <button className="btn ghost sm" disabled={saving} onClick={() => void save()}><FileSearch /> حفظ كتحقيق</button>)}
          {!m.failed && <button className="btn ghost sm" onClick={() => void toDecision()}><Gavel /> اقتراح قرار</button>}
          <span className="muted" style={{ fontSize: 11, marginInlineStart: 'auto' }}>{formatDateTime(m.createdAt)}</span>
        </div>
        {showSources && (
          <div className="sources">
            {m.sources.map((s, i) => (
              <div key={i} className="source">
                <div className="row wrap" style={{ gap: 6 }}>
                  <b>{TOOL_LABEL[s.tool] ?? s.tool}</b>
                  {s.cached && <span className="badge">نتيجة سابقة أُعيد استخدامها</span>}
                  {s.error && <span className="badge bad">خطأ</span>}
                  {s.why && <span className="muted">— {s.why}</span>}
                </div>
                {s.meta && <div className="muted" style={{ fontSize: 12 }}>الفترة {formatDate(s.meta.from)} — {formatDate(s.meta.to)} · {s.meta.learners} متعلمًا في النطاق · العينة: {s.meta.sample} · حُسبت {formatDateTime(s.at)}</div>}
                {s.error && <div style={{ fontSize: 12, color: 'var(--bad)' }}>{s.error}</div>}
                <code className="ltr" style={{ fontSize: 11 }}>{s.args}</code>
              </div>
            ))}
            <p className="muted" style={{ fontSize: 11.5, margin: 0 }}>كل رقم في الإجابة محسوب في الخادم بهذه الأدوات وتعريفات قاموس المقاييس. الـAI لا يرى أسماء المستخدمين ولا يعدّل أي بيانات.</p>
          </div>
        )}
      </div>
    </div>
  )
}

function Attachment({ a }: { a: AiAttachment }) {
  if (a.kind === 'chart' && a.chart) return <div className="attach"><div className="attach-title">{a.title}</div><SpecChart spec={a.chart} /></div>
  if (a.kind === 'users' && a.users) return (
    <div className="attach flush"><div className="attach-title">{a.title} · {a.users.length}</div>
      <UserTable rows={a.users} evidenceLabel="السبب" compact empty="لا أحد" /></div>
  )
  if (a.kind === 'table' && a.columns && a.rows) return (
    <div className="attach flush">
      <div className="attach-title">{a.title}</div>
      {a.rows.length === 0 ? <Empty title="لا صفوف" /> : (
        <div className="table-wrap"><table className="table">
          <thead><tr>{a.columns.map((c) => <th key={c}>{c}</th>)}</tr></thead>
          <tbody>{a.rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j}>{c}</td>)}</tr>)}</tbody>
        </table></div>
      )}
    </div>
  )
  return null
}
