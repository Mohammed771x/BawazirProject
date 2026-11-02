/**
 * Vocabulary shared by the investigator, the decision log and the change log
 * (ADR-132). Keys are the backend's; only the labels live here.
 */

/** The investigator's sections — also the areas a decision belongs to. */
export const SECTIONS: [string, string][] = [
  ['product_health', 'Product Health'], ['users', 'المستخدمون'], ['engagement', 'Engagement'], ['activation', 'Activation'],
  ['words', 'الكلمات'], ['skills', 'Skills'], ['reading', 'Reading'], ['listening', 'Listening'], ['speaking', 'Speaking'],
  ['writing', 'Writing'], ['spelling', 'Spelling'], ['level', 'Level'], ['retention', 'Retention'],
  ['weekly_review', 'Weekly Review'], ['ux', 'UX'], ['feedback', 'Feedback'], ['notifications', 'Notifications'], ['ai', 'AI'],
]

export const AREAS: [string, string][] = [['general', 'عام'], ...SECTIONS]

export const sectionLabel = (key: string) =>
  key === 'discussion' ? 'نقاش' : AREAS.find(([k]) => k === key)?.[1] ?? key

export const STATUS_LABEL: Record<string, [string, string]> = {
  PROPOSED: ['اقتراح', ''],
  APPROVED: ['معتمد', 'brand'],
  IMPLEMENTED: ['منفّذ', 'brand'],
  CLOSED: ['مغلق', 'good'],
  REJECTED: ['مرفوض', 'bad'],
}

export const VERDICTS: [string, string, string][] = [
  ['SUCCEEDED', 'نجح', 'good'],
  ['PARTIAL', 'نجح جزئيًا', 'warn'],
  ['NOEFFECT', 'دون أثر', ''],
  ['WORSE', 'أسوأ', 'bad'],
  ['INCONCLUSIVE', 'غير حاسم', ''],
]
export const verdictLabel = (v: string | null | undefined) => VERDICTS.find(([k]) => k === v)

export const OUTCOME_LABEL: Record<string, [string, string]> = {
  improved: ['تحسّن', 'good'],
  worsened: ['تراجع', 'bad'],
  flat: ['دون تغير يُذكر', ''],
  insufficient: ['عينة غير كافية', 'warn'],
  unknown: ['لا بيانات', ''],
}

export const KIND_LABEL: Record<string, string> = {
  content: 'المحتوى', translation: 'الترجمة', speaking: 'Speaking', notifications: 'الإشعارات',
  scheduling: 'الجدولة', ai_feedback: 'AI Feedback', performance: 'الأداء', ux: 'UX', other: 'أخرى',
}

export const REVIEW_LABEL: Record<string, [string, string]> = {
  due: ['حان التقييم', 'warn'],
  measured: ['قِيس — بانتظار الحكم', 'warn'],
  scheduled: ['مجدول', ''],
  unscheduled: ['بلا موعد', ''],
  done: ['منتهٍ', 'good'],
}

/**
 * The admin's own calendar date. Not `toISOString()`, which is the UTC date —
 * three hours after midnight in Riyadh that is still yesterday.
 */
const localDay = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

export const today = () => localDay(new Date())
export const plusDays = (n: number, from = new Date()) => {
  const d = new Date(from)
  d.setDate(d.getDate() + n)
  return localDay(d)
}
