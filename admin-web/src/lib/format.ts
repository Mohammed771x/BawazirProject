import type { Format, Skill } from '../api/types'

// Western digits inside an Arabic interface: figures are read and compared in
// columns, and every learner-facing number in WordOS already uses them.
const LOCALE = 'ar-u-nu-latn'
const intFmt = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 0 })
const numFmt = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 1 })
const compactFmt = new Intl.NumberFormat(LOCALE, { notation: 'compact', maximumFractionDigits: 1 })

export function formatValue(v: number | null | undefined, format: Format, compact = false): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '—'
  switch (format) {
    case 'pct':
      return `${numFmt.format(v * 100)}%`
    case 'ms':
      return duration(v)
    case 'min':
      return v >= 120 ? `${numFmt.format(v / 60)} ساعة` : `${intFmt.format(v)} دقيقة`
    case 'num':
      return numFmt.format(v)
    default:
      return compact && Math.abs(v) >= 10000 ? compactFmt.format(v) : intFmt.format(v)
  }
}

export function duration(ms: number): string {
  const s = ms / 1000
  if (s < 60) return `${numFmt.format(s)} ث`
  const m = s / 60
  if (m < 60) return `${numFmt.format(m)} د`
  const h = m / 60
  if (h < 48) return `${numFmt.format(h)} س`
  return `${numFmt.format(h / 24)} يوم`
}

/** Signed change against the previous period, as a fraction; null when not meaningful. */
export function change(value: number | null, previous: number | null, format: Format): number | null {
  if (value === null || previous === null) return null
  if (format === 'pct') return value - previous // percentage points
  if (previous === 0) return null
  return (value - previous) / previous
}

export function formatChange(delta: number, format: Format): string {
  const sign = delta > 0 ? '+' : delta < 0 ? '−' : ''
  const abs = Math.abs(delta)
  return format === 'pct' ? `${sign}${numFmt.format(abs * 100)} نقطة` : `${sign}${numFmt.format(abs * 100)}%`
}

const dateFmt = new Intl.DateTimeFormat(LOCALE, { day: 'numeric', month: 'short' })
const dateYearFmt = new Intl.DateTimeFormat(LOCALE, { day: 'numeric', month: 'short', year: 'numeric' })
const timeFmt = new Intl.DateTimeFormat(LOCALE, { hour: '2-digit', minute: '2-digit' })

export const formatDate = (iso: string | null | undefined, year = false) =>
  iso ? (year ? dateYearFmt : dateFmt).format(new Date(iso)) : '—'
export const formatDateTime = (iso: string) => `${dateYearFmt.format(new Date(iso))} · ${timeFmt.format(new Date(iso))}`
export const formatTime = (iso: string) => timeFmt.format(new Date(iso))

export function relative(iso: string | null | undefined): string {
  if (!iso) return 'لم ينشط بعد'
  const minutes = (Date.now() - new Date(iso).getTime()) / 60000
  if (minutes < 2) return 'الآن'
  if (minutes < 60) return `قبل ${Math.round(minutes)} دقيقة`
  const hours = minutes / 60
  if (hours < 24) return `قبل ${Math.round(hours)} ساعة`
  const days = hours / 24
  if (days < 2) return 'أمس'
  if (days < 30) return `قبل ${Math.round(days)} يومًا`
  return formatDate(iso, true)
}

export const SKILL_LABEL: Record<Skill, string> = {
  READING: 'Reading',
  LISTENING: 'Listening',
  SPEAKING: 'Speaking',
  WRITING: 'Writing',
  SPELLING: 'Spelling',
}

export const SKILL_AR: Record<Skill, string> = {
  READING: 'القراءة',
  LISTENING: 'الاستماع',
  SPEAKING: 'المحادثة',
  WRITING: 'الكتابة',
  SPELLING: 'التهجئة',
}

export const skillLabel = (s: string | null | undefined) => (s && s in SKILL_LABEL ? SKILL_LABEL[s as Skill] : s ?? '—')

export const levelLabel = (l: string | null | undefined) => (l ? l.replace('_PLUS', '+') : '—')

export const STATE_LABEL: Record<string, string> = {
  LEARNING: 'قيد التعلم', MATURE: 'متقنة', ACTIVE: 'Active', ARCHIVED: 'مؤرشفة', DELETED: 'محذوفة',
}

export const CATEGORY_LABEL: Record<string, string> = {
  READING: 'Reading', LISTENING: 'Listening', SPEAKING: 'Speaking', WRITING: 'Writing', SPELLING: 'Spelling',
  WEEKLY_REVIEW: 'Weekly Review', ADD_WORD: 'Add Word', OTHER: 'أخرى', UNCATEGORISED: 'بدون تصنيف',
}

export const FLAG_LABEL: Record<string, string> = {
  recently_inactive: 'توقف مؤخرًا',
  high_abandonment: 'انسحاب مرتفع',
  repeated_failures: 'فشل متكرر',
  long_sessions_low_progress: 'وقت طويل وتقدم قليل',
  very_short_sessions: 'Sessions قصيرة جدًا',
  hoarder: 'يضيف ولا يتقن',
  speaking_failure: 'فشل في Speaking',
  notification_no_learning: 'تنبيه بلا تعلم',
  high_overdue: 'متأخرات كثيرة',
  ai_content_issue: 'مشكلة AI/محتوى',
}
