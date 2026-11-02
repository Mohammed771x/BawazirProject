import { ArrowRight, Mail, MessageCircle } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api'
import type { DayDetail, User360 } from '../api/types'
import { BarChart, ChartFrame, SKILL_COLOR, seriesColor } from '../components/charts/Charts'
import { Timeline } from '../components/Timeline'
import { Card, Drawer, Empty, Loader, Tabs } from '../components/ui'
import { initials } from '../components/UserTable'
import { FLAG_LABEL, formatDate, formatDateTime, formatValue, levelLabel, relative, skillLabel, STATE_LABEL } from '../lib/format'
import { useAsync } from '../lib/useAsync'

export function User360Page() {
  const { id = '' } = useParams()
  const state = useAsync(() => api.user(id), [id])
  return (
    <>
      <Link to="/users" className="row muted" style={{ fontSize: 13 }}><ArrowRight size={16} /> المستخدمون</Link>
      <Loader state={state}>{(d) => <Body data={d} id={id} />}</Loader>
    </>
  )
}

function Body({ data, id }: { data: User360; id: string }) {
  return (
    <>
      <Header data={data} />
      <div className="grid g-main">
        <LearningOverview data={data} />
        <ActivityCard data={data} id={id} />
      </div>
      <SkillPerformance data={data} />
      <WordPerformance data={data} />
      <div className="grid g-main">
        <TimelineCard id={id} />
        <div className="stack lg">
          <LevelsCard data={data} />
          <FeedbackCard data={data} />
        </div>
      </div>
    </>
  )
}

function Header({ data }: { data: User360 }) {
  const { header: h } = data
  const r = h.row
  const facts: [string, string][] = [
    ['تاريخ الانضمام', formatDate(r.joinedAt, true)],
    ['آخر نشاط', relative(r.lastActiveAt)],
    ['أيام النشاط', String(r.activeDays)],
    ['Sessions', String(r.sessions)],
    ['الـStreak الحالي', r.streak ? `${r.streak} يوم` : '—'],
    ['المستوى', levelLabel(r.level)],
  ]
  return (
    <div className="card">
      <div className="row between wrap" style={{ gap: 16, alignItems: 'flex-start' }}>
        <div className="row" style={{ gap: 14 }}>
          <span className="avatar lg">{initials(r.name)}</span>
          <div>
            <h1 style={{ fontSize: 20 }}>{r.name}</h1>
            <div className="row wrap muted" style={{ fontSize: 13, gap: 12 }}>
              <span className="ltr">{h.email}</span>
              {h.phone && <span className="ltr">{h.phone}</span>}
              {h.platform && <span>{h.platform} · {h.appVersion}</span>}
            </div>
            <div className="row wrap" style={{ gap: 6, marginTop: 8 }}>
              {h.interests.map((i) => <span key={i} className="badge">{i}</span>)}
              {data.attention.map((a) => <span key={a.key} className="badge warn" title={a.evidence}>{FLAG_LABEL[a.key] ?? a.label}</span>)}
            </div>
          </div>
        </div>
        <div className="row">
          <a className="btn" href={`mailto:${h.email}`} aria-disabled={h.email.includes('•')}><Mail /> Email</a>
          {h.whatsapp
            ? <a className="btn" href={h.whatsapp} target="_blank" rel="noreferrer"><MessageCircle /> WhatsApp</a>
            : <button className="btn" disabled title="لا يوجد رقم أو لا تملك صلاحية رؤيته"><MessageCircle /> WhatsApp</button>}
        </div>
      </div>
      <div className="row wrap" style={{ gap: 28, marginTop: 18, paddingTop: 16, borderTop: '1px solid var(--border)' }}>
        {facts.map(([l, v]) => (
          <div key={l}><div className="muted" style={{ fontSize: 12 }}>{l}</div><div style={{ fontWeight: 600 }} className="num">{v}</div></div>
        ))}
      </div>
      {data.attention.length > 0 && (
        <div className="stack" style={{ marginTop: 14 }}>
          {data.attention.map((a) => (
            <div key={a.key} className="alert info" style={{ background: 'var(--warn-soft)', color: 'var(--warn)' }}><b>{a.label}:</b> {a.evidence}</div>
          ))}
        </div>
      )}
    </div>
  )
}

function LearningOverview({ data }: { data: User360 }) {
  const [period, setPeriod] = useState('week')
  const p = data.periods.find((x) => x.key === period) ?? data.periods[0]
  const s = data.states
  const states: [string, number, string?][] = [
    ['كل الكلمات', s.total], ['قيد التعلم', s.learning], ['متقنة', s.mastered], ['Active', s.active],
    ['مؤرشفة', s.archived], ['مستحقة', s.due], ['متأخرة', s.overdue, s.overdue ? 'var(--bad)' : undefined],
  ]
  return (
    <Card title="Learning Overview" actions={<Tabs value={period} onChange={setPeriod} items={data.periods.map((x) => ({ value: x.key, label: x.label }))} />}>
      <div className="grid g-4" style={{ marginBottom: 16 }}>
        {[['أضاف', p.added], ['اجتاز مهارة', p.passed], ['فشل', p.failed], ['أتقن', p.mastered]].map(([l, v]) => (
          <div key={l as string} className="evidence"><div className="l">{l}</div><div className="v">{v}</div></div>
        ))}
      </div>
      <div className="section-title" style={{ marginBottom: 8 }}>حالة الكلمات الآن</div>
      <div className="row wrap" style={{ gap: 22 }}>
        {states.map(([l, v, color]) => (
          <div key={l}><div className="muted" style={{ fontSize: 12 }}>{l}</div><div style={{ fontSize: 19, fontWeight: 600, color }} className="num">{v}</div></div>
        ))}
      </div>
    </Card>
  )
}

function ActivityCard({ data, id }: { data: User360; id: string }) {
  const [day, setDay] = useState<string | null>(null)
  const max = Math.max(1, ...data.activity.map((a) => a.count))
  const level = (c: number) => (c === 0 ? 'var(--surface-3)' : `var(--seq-${Math.min(4, Math.ceil((c / max) * 4))})`)
  const active = data.activity.filter((a) => a.count > 0).length
  return (
    <Card title="النشاط — آخر 90 يومًا" subtitle={`${active} يومًا نشطًا — اضغط أي يوم لتفاصيله`}>
      <div className="activity" role="group" aria-label={`${active} يومًا نشطًا من 90`}>
        {data.activity.map((a) => (
          <span key={a.date} role="button" tabIndex={0} title={`${a.date}: ${a.count} أحداث`} aria-label={`${a.date}: ${a.count} أحداث`}
            style={{ background: level(a.count), cursor: 'pointer', outline: day === a.date ? '2px solid var(--brand)' : undefined }}
            onClick={() => setDay(a.date)} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setDay(a.date) } }} />
        ))}
      </div>
      <div className="row muted" style={{ fontSize: 11.5, marginTop: 10, gap: 4 }}>
        أقل {[0, 1, 2, 3, 4].map((i) => <span key={i} style={{ width: 10, height: 10, borderRadius: 3, background: i ? `var(--seq-${i})` : 'var(--surface-3)', display: 'inline-block' }} />)} أكثر
      </div>
      <Drawer open={!!day} onClose={() => setDay(null)} title={day ? `يوم ${formatDate(day, true)}` : ''}>
        {day && <DayView id={id} date={day} />}
      </Drawer>
    </Card>
  )
}

/**
 * One day of one learner (brief page 3): how often they came, what they
 * studied, how it went, what they added — and every event, as on the timeline.
 */
function DayView({ id, date }: { id: string; date: string }) {
  const state = useAsync(() => api.day(id, date), [id, date])
  return <Loader state={state}>{(d) => <DayBody d={d} />}</Loader>
}

function DayBody({ d }: { d: DayDetail }) {
  const s = d.summary
  const ms = (v: number | null) => (v === null ? '—' : formatValue(v, 'ms'))
  const tiles: [string, string][] = [
    ['فتح التطبيق', s.appOpens === null ? '—' : String(s.appOpens)],
    ['جلسات التطبيق', s.appSessions === null ? '—' : String(s.appSessions)],
    ['وقت التطبيق', ms(s.appMs)],
    ['وقت التعلم الفعلي', ms(s.learningMs)],
    ['تمارين', `${s.sessions}${s.practice ? ` + ${s.practice} تدريب` : ''}`],
    ['مكتملة / منسحب منها', `${s.completed} / ${s.abandoned}`],
    ['أسئلة (محاولات)', `${s.questions} (${s.attempts})`],
    ['دقة المحاولة الأولى', formatValue(s.firstAttemptAccuracy, 'pct')],
    ['اجتاز / فشل', `${s.passed} / ${s.failed}`],
    ['كلمات أُضيفت', String(s.wordsAdded)],
    ['دخلت Active', String(s.enteredActive)],
    ['مراجعات', String(s.reviews)],
  ]
  const nothing = s.sessions + s.practice + s.wordsAdded + s.reviews + d.events.length === 0
  return (
    <div className="stack lg">
      {!d.phoneReported && <div className="note" style={{ fontSize: 12.5 }}>لم يرسل التطبيق أحداثًا في هذا اليوم، فوقت التطبيق ومرات الفتح غير معروفة (ليست صفرًا).</div>}
      {nothing ? <Empty title="لا نشاط في هذا اليوم" /> : (
        <>
          <div className="evidence-grid">
            {tiles.map(([l, v]) => <div key={l} className="evidence"><div className="l">{l}</div><div className="v" style={{ fontSize: 18 }}>{v}</div></div>)}
          </div>
          {(s.translations + s.replays + s.hints + s.notificationsOpened) > 0 && (
            <div className="muted" style={{ fontSize: 12.5 }}>ترجمة {s.translations} · إعادة تشغيل {s.replays} · تلميحات {s.hints} · فتح من تذكير {s.notificationsOpened}</div>
          )}
          {d.skills.length > 0 && (
            <table className="table">
              <thead><tr><th>المهارة</th><th className="n">تمارين</th><th className="n">الوقت</th><th className="n">أسئلة</th><th className="n">أول محاولة</th><th className="n">اجتاز/فشل</th><th>المستوى</th></tr></thead>
              <tbody>{d.skills.map((k) => (
                <tr key={k.skill}>
                  <td><span style={{ color: SKILL_COLOR[k.skill] }}>●</span> {skillLabel(k.skill)}</td>
                  <td className="n">{k.sessions}{k.abandoned ? <span className="muted"> (−{k.abandoned})</span> : null}</td>
                  <td className="n">{ms(k.learningMs)}</td>
                  <td className="n">{k.questions}</td>
                  <td className="n">{formatValue(k.firstAttemptAccuracy, 'pct')}</td>
                  <td className="n">{k.passed}/{k.failed}</td>
                  <td className="ltr" style={{ textAlign: 'start' }}>{k.levels.map(levelLabel).join('، ')}</td>
                </tr>
              ))}</tbody>
            </table>
          )}
          {d.wordsAdded.length > 0 && (
            <div className="row wrap" style={{ gap: 6 }}>
              <span className="muted" style={{ fontSize: 12.5 }}>أضاف:</span>
              {d.wordsAdded.map((w) => <span key={w.id} className={`badge ltr ${w.deleted ? 'bad' : ''}`} title={w.deleted ? 'حذفها لاحقًا' : undefined}>{w.text}</span>)}
            </div>
          )}
          {d.events.length > 0 && <Timeline events={d.events} />}
        </>
      )}
    </div>
  )
}

function SkillPerformance({ data }: { data: User360 }) {
  const labels = data.skills.map((s) => skillLabel(s.summary.skill))
  const series = [
    { name: 'دقة المحاولة الأولى', values: data.skills.map((s) => s.summary.firstAttemptAccuracy), color: seriesColor(0) },
    { name: 'الدقة الكلية', values: data.skills.map((s) => s.summary.overallAccuracy), color: seriesColor(1) },
  ]
  return (
    <Card title="أداء المهارات" subtitle="منذ التسجيل" flush>
      <div className="grid g-2" style={{ padding: '0 20px 12px' }}>
        <ChartFrame labels={labels} series={series} unit="pct"><BarChart labels={labels} series={series} unit="pct" height={200} /></ChartFrame>
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>المهارة</th><th className="n">Sessions</th><th className="n">نجاح</th><th className="n">فشل</th><th className="n">Retry</th><th className="n">انسحاب</th><th className="n">الوقت</th><th>المستوى</th></tr></thead>
            <tbody>
              {data.skills.map(({ summary: s, selectedLevel, assessedLevel }) => (
                <tr key={s.skill}>
                  <td><span className="row"><span className="dot" style={{ background: SKILL_COLOR[s.skill] }} />{skillLabel(s.skill)}</span></td>
                  <td className="n">{s.sessions}</td><td className="n">{s.passed}</td><td className="n">{s.failed}</td>
                  <td className="n">{formatValue(s.retryRate, 'pct')}</td><td className="n">{formatValue(s.abandonmentRate, 'pct')}</td>
                  <td className="n">{formatValue(s.medianSessionMs, 'ms')}</td>
                  <td className="ltr" title="المختار / المقاس" style={{ fontSize: 12 }}>{levelLabel(selectedLevel)} / {levelLabel(assessedLevel)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </Card>
  )
}

type WordFilter = 'all' | 'failed' | 'repeated' | 'due' | 'overdue' | 'active'

function WordPerformance({ data }: { data: User360 }) {
  const [f, setF] = useState<WordFilter>('all')
  const words = useMemo(() => data.words.filter((w) => {
    switch (f) {
      case 'failed': return w.failures > 0
      case 'repeated': return w.skills.some((s) => s.attempts >= 3)
      case 'due': return w.due
      case 'overdue': return w.overdue
      case 'active': return w.state === 'ACTIVE' || w.state === 'ARCHIVED'
      default: return w.state !== 'DELETED'
    }
  }), [data.words, f])
  const STATUS_TITLE: Record<string, string> = { PASSED: 'نجح', FAILED: 'فشل', AVAILABLE: 'متاحة الآن', PENDING: 'لم تصل بعد' }
  return (
    <Card flush title="أداء الكلمات" subtitle="المشكلة قد تكون في مهارة معيّنة لا في الكلمة نفسها — كل خانة مهارة"
      actions={<Tabs value={f} onChange={setF} items={[
        { value: 'all', label: 'الكل' }, { value: 'failed', label: 'فشل فيها' }, { value: 'repeated', label: 'كررها كثيرًا' },
        { value: 'due', label: 'تحتاج مراجعة' }, { value: 'overdue', label: 'متأخرة' }, { value: 'active', label: 'Active' },
      ]} />}>
      {words.length === 0 ? <Empty title="لا كلمات هنا" /> : (
        <div className="table-wrap" style={{ maxHeight: 460, overflowY: 'auto' }}>
          <table className="table">
            <thead><tr><th>الكلمة</th><th>الحالة</th><th>R · L · Sp · W · Sl</th><th className="n">حالات فشل</th><th className="n">Exposure</th><th>أُضيفت</th></tr></thead>
            <tbody>
              {words.map((w) => (
                <tr key={w.id}>
                  <td><span className="ltr" style={{ fontWeight: 600 }}>{w.text}</span> <span className="muted">— {w.meaning}</span></td>
                  <td>
                    <span className={`badge ${w.state === 'ACTIVE' ? 'good' : w.state === 'DELETED' ? 'bad' : ''}`}>{STATE_LABEL[w.state] ?? w.state}</span>
                    {w.overdue && <span className="badge bad" style={{ marginInlineStart: 4 }}>متأخرة</span>}
                    {!w.overdue && w.due && <span className="badge brand" style={{ marginInlineStart: 4 }}>مستحقة</span>}
                  </td>
                  <td>
                    <div className="skill-pills ltr">
                      {w.skills.map((s) => (
                        <span key={s.skill} className={`skill-pill ${s.status ?? ''}`}
                          title={`${skillLabel(s.skill)}: ${STATUS_TITLE[s.status ?? ''] ?? '—'} · ${s.attempts} محاولات${s.failures ? ` · ${s.failures} فشل` : ''}`}>
                          {s.skill.slice(0, 2)}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="n">{w.failures || '—'}</td>
                  <td className="n">{w.exposures}</td>
                  <td className="muted">{formatDate(w.addedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

function TimelineCard({ id }: { id: string }) {
  const [verbose, setVerbose] = useState(false)
  const [filter, setFilter] = useState('all')
  const state = useAsync(() => api.timeline(id, verbose), [id, verbose])
  return (
    <Card title="Timeline" subtitle="كل ما حدث، الأحدث أولًا — اضغط أي حدث لتفاصيله"
      actions={<>
        <Tabs value={filter} onChange={setFilter} items={[
          { value: 'all', label: 'الكل' }, { value: 'learning', label: 'التعلم' }, { value: 'words', label: 'الكلمات' },
          { value: 'behavior', label: 'السلوك' }, { value: 'problems', label: 'المشكلات' },
        ]} />
        <label className="row" style={{ fontSize: 12.5, gap: 6 }}>
          <input type="checkbox" checked={verbose} onChange={(e) => setVerbose(e.target.checked)} /> تفصيلي
        </label>
      </>}>
      <div style={{ maxHeight: 720, overflowY: 'auto' }}>
        <Loader state={state}>{(d) => <Timeline events={d.events} filter={filter} />}</Loader>
      </div>
    </Card>
  )
}

function LevelsCard({ data }: { data: User360 }) {
  return (
    <Card title="تاريخ المستوى">
      {data.levels.length === 0 ? <Empty title="لا تغييرات" /> : (
        <div className="stack">
          {data.levels.slice(0, 12).map((l, i) => (
            <div key={i} className="row between" style={{ fontSize: 13 }}>
              <span className="row"><span className="dot" style={{ background: SKILL_COLOR[l.skill] }} />{skillLabel(l.skill)}
                <span className="ltr" style={{ fontWeight: 600 }}>{levelLabel(l.from)} → {levelLabel(l.to)}</span></span>
              <span className="muted" style={{ fontSize: 12 }}>{l.type === 'SYSTEM_VALIDATED_CHANGE' ? 'النظام' : l.type === 'PLACEMENT' ? 'اختبار المستوى' : 'يدوي'} · {formatDate(l.at)}</span>
            </div>
          ))}
        </div>
      )}
    </Card>
  )
}

function FeedbackCard({ data }: { data: User360 }) {
  return (
    <Card title="ملاحظاته">
      {data.feedback.length === 0 ? <Empty title="لم يكتب ملاحظات" /> : (
        <div className="stack">
          {data.feedback.map((f) => (
            <div key={f.id} className="note">
              <div className="row between" style={{ marginBottom: 4 }}>
                <span className="badge">{f.category ?? 'بدون تصنيف'}</span>
                <span className="muted" style={{ fontSize: 12 }}>{formatDateTime(f.createdAt)}</span>
              </div>
              {f.body}
            </div>
          ))}
        </div>
      )}
    </Card>
  )
}
