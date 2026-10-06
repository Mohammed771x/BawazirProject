import { Fragment, useMemo, useState } from 'react'
import type { TimelineEvent } from '../api/types'
import { formatDate, formatTime, skillLabel } from '../lib/format'
import { SKILL_COLOR } from './charts/Charts'
import { Empty } from './ui'

interface Group { event: TimelineEvent; count: number }

/**
 * A learner's history, newest first, grouped by day (admin brief §22).
 *
 * Consecutive identical events collapse into one row with a count —
 * "أعاد تشغيل الصوت ×3" — because three rows saying the same thing bury the
 * one that matters next to them.
 */
export function Timeline({ events, filter }: { events: TimelineEvent[]; filter?: string }) {
  const [open, setOpen] = useState<string | null>(null)

  const days = useMemo(() => {
    const visible = events.filter((e) => !filter || filter === 'all'
      || (filter === 'learning' && ['answer', 'writing', 'speaking_eval', 'skill_passed', 'skill_failed', 'session_started', 'session_completed', 'session_abandoned', 'review_answer'].includes(e.kind))
      || (filter === 'words' && e.kind.startsWith('word_'))
      || (filter === 'behavior' && ['translation', 'audio_play', 'audio_replay', 'hint', 'exercise_exit', 'feedback_viewed', 'app_opened', 'notification', 'api_error', 'screen'].includes(e.kind))
      || (filter === 'problems' && e.tone === 'bad'))

    const groups: Group[] = []
    for (const e of visible) {
      const last = groups[groups.length - 1]
      if (last && last.event.kind === e.kind && last.event.title === e.title && last.event.sessionId === e.sessionId
        && Math.abs(new Date(last.event.at).getTime() - new Date(e.at).getTime()) < 30 * 60_000) {
        last.count++
      } else groups.push({ event: e, count: 1 })
    }

    const byDay = new Map<string, Group[]>()
    for (const g of groups) {
      const key = g.event.at.slice(0, 10)
      byDay.set(key, [...(byDay.get(key) ?? []), g])
    }
    return [...byDay.entries()]
  }, [events, filter])

  if (days.length === 0) return <Empty title="لا توجد أحداث" text="لا شيء يطابق هذا التصنيف." />

  return (
    <div className="timeline">
      {days.map(([day, groups]) => (
        <div key={day}>
          <div className="tl-day">{formatDate(day, true)}</div>
          {groups.map(({ event: e, count }) => {
            const isOpen = open === e.id
            const details = Object.entries(e.detail)
            return (
              <div className="tl-item" key={e.id}>
                <div className="tl-time">{formatTime(e.at)}</div>
                <div className="tl-rail"><span className={`tl-node ${e.tone}`} /></div>
                <div className={`tl-card ${isOpen ? 'open' : ''}`} onClick={() => setOpen(isOpen ? null : e.id)}
                  role="button" tabIndex={0} aria-expanded={isOpen}>
                  <div className="tl-title">
                    <span>{e.title}</span>
                    {count > 1 && <span className="badge">×{count}</span>}
                    {e.skill && (
                      <span className="badge" style={{ gap: 5 }}>
                        <span className="dot" style={{ background: SKILL_COLOR[e.skill], width: 6, height: 6 }} />{skillLabel(e.skill)}
                      </span>
                    )}
                  </div>
                  {isOpen && (details.length > 0 || e.sessionId) && (
                    <dl className="tl-detail">
                      {details.map(([k, v]) => (<Fragment key={k}><dt>{k}</dt><dd>{v}</dd></Fragment>))}
                      {e.sessionId && (<><dt>Session</dt><dd className="ltr muted" style={{ fontSize: 11 }}>{e.sessionId}</dd></>)}
                    </dl>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      ))}
    </div>
  )
}
