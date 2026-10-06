import type { AdminApi } from './client'
import type {
  AttentionGroups, Behavior, CohortSide, FeedbackItem, FeedbackPage, Filters, FrictionDetail,
  FrictionItem, InquirySummary, Investigation, Kpi, Learning, Meta, Overview, Retention, Session,
  Skill, SkillDetail, SkillSummary, TimelineEvent, User360, UserRow,
} from './types'

/**
 * A disposable stand-in for the admin API, for building the UI with no backend.
 *
 * Deterministic (seeded), so a screenshot taken today matches one taken
 * tomorrow. Shapes are the real contract's; the numbers are invented and the
 * page says so in its banner. Never imported by anything except `api/index.ts`.
 */

const SKILLS: Skill[] = ['READING', 'LISTENING', 'SPEAKING', 'WRITING', 'SPELLING']
const NAMES = ['أحمد سالم', 'سارة علي', 'محمد حسن', 'نورة خالد', 'يوسف عمر', 'ريم فهد', 'خالد ناصر', 'هند سعيد',
  'عبدالله مراد', 'لينا جمال', 'فيصل منصور', 'مها راشد', 'طارق زيد', 'دانة سامي', 'بدر وليد', 'جود ماجد',
  'سلمان إبراهيم', 'غادة يحيى', 'تركي أنس', 'رهف صالح', 'ماجد عادل', 'شهد كريم', 'حمد ياسر', 'أسماء طلال']
const WORDS = [['perspective', 'منظور'], ['achieve', 'يحقق'], ['reliable', 'موثوق'], ['negotiate', 'يتفاوض'],
  ['curious', 'فضولي'], ['emerge', 'يظهر'], ['significant', 'مهم'], ['abandon', 'يتخلى'], ['thorough', 'شامل'],
  ['estimate', 'يقدّر'], ['launch', 'يطلق'], ['generous', 'كريم'], ['decline', 'يرفض'], ['vivid', 'حيّ']]

function rng(seed: number) {
  let s = seed >>> 0
  return () => {
    s = (s * 1664525 + 1013904223) >>> 0
    return s / 2 ** 32
  }
}
const r = rng(42)
const int = (min: number, max: number) => Math.floor(min + r() * (max - min + 1))
const pick = <T,>(xs: T[]) => xs[Math.floor(r() * xs.length)]
const day = (offset: number) => {
  const d = new Date()
  d.setDate(d.getDate() - offset)
  return d.toISOString().slice(0, 10)
}
const iso = (offsetDays: number, hours = 0) => new Date(Date.now() - offsetDays * 864e5 - hours * 36e5).toISOString()
const wait = <T,>(value: T, ms = 250) => new Promise<T>((ok) => setTimeout(() => ok(structuredClone(value)), ms))

const FLAGS = ['recently_inactive', 'high_abandonment', 'repeated_failures', 'speaking_failure', 'high_overdue', 'hoarder']

const USERS: UserRow[] = NAMES.map((name, i) => {
  const joined = int(1, 80)
  const last = Math.min(joined, int(0, 12))
  const words = int(2, 40)
  return {
    id: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`,
    name,
    email: `user${i + 1}@example.com`,
    phone: `+96650000${String(1000 + i)}`,
    joinedAt: iso(joined),
    lastActiveAt: iso(last, int(0, 20)),
    activeDays: int(1, Math.max(1, joined)),
    sessions: int(1, 60),
    words,
    mastered: int(0, Math.floor(words / 2)),
    level: pick(['A2', 'B1', 'B1_PLUS', 'B2', 'C1']),
    status: last >= 3 ? 'inactive' : joined < 3 ? 'new' : 'active',
    streak: last === 0 ? int(1, 9) : 0,
    flags: r() < 0.35 ? [pick(FLAGS)] : [],
  }
})

function summary(skill: Skill, shift = 0): SkillSummary {
  const base: Record<Skill, number> = { READING: 0.78, LISTENING: 0.66, SPEAKING: 0.58, WRITING: 0.7, SPELLING: 0.74 }
  const sessions = int(30, 140)
  const abandoned = Math.floor(sessions * (skill === 'SPEAKING' ? 0.31 : skill === 'LISTENING' ? 0.22 : 0.12) * (1 + shift))
  const passed = int(40, 200)
  const failed = Math.floor(passed * (1 - base[skill]) * (1 + shift))
  const questions = sessions * 6
  return {
    skill, sessions, completed: sessions - abandoned - int(0, 4), abandoned, practice: int(0, 12), words: int(30, 120),
    questions, attempts: Math.floor(questions * 1.3), passed, failed,
    firstAttemptAccuracy: base[skill] - shift * 0.1, overallAccuracy: base[skill] - 0.08 - shift * 0.05,
    passRate: passed / (passed + failed), failRate: failed / (passed + failed), retryRate: 0.18 + r() * 0.2,
    abandonmentRate: abandoned / sessions, avgSessionMs: int(120, 600) * 1000, medianSessionMs: int(90, 420) * 1000,
    medianAnswerMs: int(6, 40) * 1000,
  }
}

const SKILL_SUMMARIES = SKILLS.map((s) => summary(s))
const PREV_SUMMARIES = SKILLS.map((s) => summary(s, -0.15))

function kpi(key: string, label: string, value: number, previous: number, format: Kpi['format'], hint?: string): Kpi {
  return { key, label, value, previous, format, hint }
}

function period(f: Filters) {
  const days = f.days ?? 30
  return {
    from: iso(days), to: iso(0), prevFrom: iso(days * 2), prevTo: iso(days), days, learners: USERS.length,
  }
}

const REASONS: Record<Skill, [string, string, string][]> = {
  READING: [['misunderstood_context', 'فهم خاطئ للسياق', 'learning'], ['vocabulary_meaning', 'مشكلة في معنى الكلمة', 'learning'], ['too_difficult', 'المحتوى صعب — خُفّض المستوى', 'content'], ['abandoned', 'انسحب قبل النهاية', 'ux']],
  LISTENING: [['could_not_hear', 'صعوبة في السماع — إعادة تشغيل متكررة', 'content'], ['misunderstood_context', 'فهم خاطئ للسياق', 'learning'], ['abandoned', 'انسحب قبل النهاية', 'ux'], ['no_answer', 'لم يُجب', 'ux']],
  SPEAKING: [['did_not_use_word', 'لم يستخدم الكلمة المطلوبة', 'learning'], ['wrong_meaning', 'معنى خاطئ', 'learning'], ['grammar', 'Grammar', 'learning'], ['did_not_answer', 'لم يتكلم', 'ux'], ['abandoned', 'انسحب قبل النهاية', 'ux']],
  WRITING: [['wrong_usage', 'استخدام خاطئ / Collocation', 'learning'], ['missing_target_word', 'الكلمة غير موجودة في الجملة', 'learning'], ['grammar', 'Grammar', 'learning'], ['ai_fallback', 'تقييم احتياطي — الـAI لم يجب', 'ai']],
  SPELLING: [['wrong_letters', 'حروف خاطئة', 'learning'], ['failed_with_hints', 'فشل رغم التلميحات', 'content'], ['abandoned', 'انسحب قبل النهاية', 'ux']],
}

const FAILURES = SKILLS.map((skill) => {
  const reasons = REASONS[skill].map(([key, label, category]) => ({ key, label, category, count: int(3, 40) }))
    .sort((a, b) => b.count - a.count)
  return { skill, total: reasons.reduce((s, x) => s + x.count, 0), reasons }
})

const FRICTION: FrictionItem[] = [
  { key: 'listening_replay', title: 'إعادة تشغيل الصوت في Listening', description: 'متوسط إعادات التشغيل لكل Session — المرتفع يعني أن الصوت سريع أو صعب', skill: 'LISTENING', value: 4.6, format: 'num', baseline: 2.9, affectedUsers: 9, severity: 'high', category: 'content' },
  { key: 'abandon_speaking', title: 'الانسحاب من Speaking', description: 'تمارين بدأت ولم تكتمل', skill: 'SPEAKING', value: 0.31, format: 'pct', baseline: 0.24, affectedUsers: 7, severity: 'medium', category: 'ux' },
  { key: 'reading_translation', title: 'تكرار فتح الترجمة في Reading', description: 'ترجمات مفتوحة لكل Session', skill: 'READING', value: 5.2, format: 'num', baseline: 4.8, affectedUsers: 14, severity: 'medium', category: 'content' },
  { key: 'retry', title: 'إعادة المحاولة', description: 'أسئلة احتاجت أكثر من محاولة', skill: null, value: 0.24, format: 'pct', baseline: 0.26, affectedUsers: 18, severity: 'low', category: 'learning' },
  { key: 'immediate_back', title: 'رجوع فوري', description: 'غادر شاشة التمرين خلال أقل من 4 ثوانٍ', skill: null, value: 11, format: 'int', baseline: null, affectedUsers: 6, severity: 'low', category: 'ux' },
  { key: 'ai_errors', title: 'أخطاء الـAI والمحتوى الاحتياطي', description: 'طلبات فشلت أو Sessions استخدمت محتوى احتياطيًا', skill: null, value: 0.018, format: 'pct', baseline: null, affectedUsers: 3, severity: 'low', category: 'ai' },
]

const FEEDBACK: FeedbackItem[] = [
  ['الصوت في Listening سريع جدًا على مستوى B1', 'LISTENING'], ['ما فهمت ليش فشلت في Speaking مع إني استخدمت الكلمة', 'SPEAKING'],
  ['أتمنى يكون فيه وضع ليلي أوضح', 'OTHER'], ['الصوت سريع شوي في الاستماع', 'LISTENING'], ['التلميحات في التهجئة ممتازة', 'SPELLING'],
].map(([body, category], i) => ({
  id: `f-${i}`, body, category, status: i < 3 ? 'NEW' : 'HANDLED', createdAt: iso(i * 2 + 1), handledAt: i < 3 ? null : iso(i),
  appVersion: '1.2.0', platform: i % 2 ? 'ios' : 'android',
  user: { id: USERS[i].id, name: USERS[i].name, email: USERS[i].email, phone: USERS[i].phone },
  notes: i === 0 ? [{ id: 'n1', body: 'Investigating Listening speed at B1.', author: 'Owner', createdAt: iso(0) }] : [],
}))

const HISTORY: InquirySummary[] = [
  { id: 'q-1', section: 'speaking', question: 'لماذا Speaking لديه Abandonment مرتفع؟', summary: 'أغلب الانسحاب يحدث بعد أول رد خاطئ في المحادثة، ويتركز في B1+.', interpretedBy: 'ai', createdAt: iso(3), author: 'Owner' },
]

export class MockAdminApi implements AdminApi {
  async login(email: string): Promise<Session> {
    return wait({ token: 'mock', refreshToken: 'mock', expiresAt: iso(-1), user: { id: 'owner', email, displayName: 'Owner', role: 'OWNER' } })
  }
  async logout() {}

  meta(): Promise<Meta> {
    return wait({
      me: { id: 'owner', name: 'Owner (تجريبي)', email: 'owner@wordos.app', role: 'OWNER' },
      canSeeContact: true,
      skills: SKILLS,
      levels: ['A1', 'A2', 'B1', 'B2', 'C1', 'C2'],
      interests: [{ interest: 'technology', count: 12 }, { interest: 'football', count: 9 }, { interest: 'travel', count: 7 }],
      segments: [{ key: 'all', label: 'كل المستخدمين' }, { key: 'joined:30', label: 'انضموا خلال 30 يومًا' }, { key: 'inactive:3', label: 'غير نشطين منذ 3 أيام' }, { key: 'weak:speaking', label: 'ضعف في Speaking' }, { key: 'hoarders', label: 'أضافوا كثيرًا وأتقنوا قليلًا' }, { key: 'heavy', label: 'Heavy users' }],
      attention: FLAGS.map((key) => ({ key, label: key, description: '' })),
      funnel: [],
      tracking: { clientSince: iso(20), serverSince: iso(20) },
    })
  }

  overview(f: Filters): Promise<Overview> {
    const days = f.days ?? 30
    return wait({
      period: period(f),
      health: [
        kpi('total_users', 'إجمالي المستخدمين', 1248, 1151, 'int'),
        kpi('dau', 'النشطون اليوم', 214, 198, 'int'),
        kpi('wau', 'النشطون هذا الأسبوع', 612, 640, 'int'),
        kpi('mau', 'النشطون هذا الشهر', 1012, 934, 'int'),
        kpi('active_days', 'Active Days', 8420, 7810, 'int'),
        kpi('sessions', 'Sessions', 12410, 11320, 'int'),
        kpi('learning_time', 'وقت التعلم', 61240, 57110, 'min'),
        kpi('retention_d7', 'Retention D7', 0.42, 0.39, 'pct'),
      ],
      engagement: Array.from({ length: days }, (_, i) => ({
        date: day(days - 1 - i), activeUsers: int(150, 260), sessions: int(300, 520), learningMinutes: int(1500, 2600), newUsers: int(2, 14),
      })),
      funnel: [
        ['signup', 'التسجيل', 320], ['onboarding', 'إكمال الإعداد', 281], ['first_word', 'أول كلمة', 249], ['first_skill', 'أول مهارة', 221],
        ['first_completed', 'أول مهارة مكتملة', 188], ['second_session', 'Session ثانية', 131], ['first_mastered', 'أول كلمة متقنة', 64], ['first_active', 'أول كلمة Active مستخدمة', 41],
      ].map(([key, label, count], i, all) => ({
        key: key as string, label: label as string, hint: '', count: count as number, share: (count as number) / 320,
        dropOff: i === 0 ? null : 1 - (count as number) / (all[i - 1][2] as number),
      })),
      lifecycle: {
        added: 3120, studied: 2840, learning: 5210, mature: 1840, active: 1520, archived: 320, deleted: 140, due: 860, overdue: 214,
        byStage: SKILLS.map((skill) => ({ skill, waiting: int(200, 900), due: int(60, 260), overdue: int(10, 90) })),
        trend: Array.from({ length: days }, (_, i) => ({ date: day(days - 1 - i), added: int(60, 140), passed: int(120, 260), activated: int(10, 40) })),
      },
      skills: SKILL_SUMMARIES,
      signals: [
        { severity: 'high', area: 'content', title: 'إعادة تشغيل الصوت في Listening', detail: 'متوسط 4.6 إعادات لكل Session مقابل 2.9 في الفترة السابقة', link: '/behavior/friction/listening_replay' },
        { severity: 'medium', area: 'ux', title: 'انسحاب مرتفع من SPEAKING', detail: '31% من التمارين لم تكتمل', link: '/learning/SPEAKING' },
        { severity: 'medium', area: 'ux', title: 'أكبر تسرب في Activation: Session ثانية', detail: '30% لم يعودوا لليوم الثاني', link: '/' },
        { severity: 'low', area: 'feedback', title: '3 ملاحظات جديدة من المستخدمين', detail: 'لم تُقرأ بعد', link: '/feedback' },
      ],
    })
  }

  learning(f: Filters): Promise<Learning> {
    return wait({
      period: period(f),
      skills: SKILLS.map((_, i) => ({ current: SKILL_SUMMARIES[i], previous: PREV_SUMMARIES[i] })),
      failures: FAILURES,
      categories: [
        { category: 'learning', label: 'مشكلة تعلم', count: 210 }, { category: 'ux', label: 'مشكلة UX', count: 96 },
        { category: 'content', label: 'مشكلة محتوى', count: 71 }, { category: 'ai', label: 'مشكلة AI', count: 12 },
      ],
      levels: {
        flows: [
          { skill: 'READING', from: 'A2', to: 'B1', count: 34, direction: 'up' }, { skill: 'READING', from: 'B1', to: 'B2', count: 21, direction: 'up' },
          { skill: 'SPEAKING', from: 'B2', to: 'B1', count: 18, direction: 'down' }, { skill: 'LISTENING', from: 'B1_PLUS', to: 'B1', count: 15, direction: 'down' },
          { skill: 'READING', from: 'B2', to: 'B1', count: 9, direction: 'down' },
        ],
        bySkill: [
          { skill: 'READING', up: 55, down: 12, manual: 50, system: 17 }, { skill: 'LISTENING', up: 14, down: 19, manual: 25, system: 8 },
          { skill: 'SPEAKING', up: 6, down: 22, manual: 24, system: 4 }, { skill: 'WRITING', up: 11, down: 7, manual: 12, system: 6 },
        ],
        byLevel: [{ level: 'B1', downgrades: 14, upgrades: 21 }, { level: 'B1_PLUS', downgrades: 15, upgrades: 4 }, { level: 'B2', downgrades: 27, upgrades: 8 }],
        distribution: [],
      },
      weeklyReview: {
        started: 410, completed: 322, users: 260, wordsReviewed: 3100, accuracy: 0.71, firstAttemptAccuracy: 0.64, medianMs: 240000, dropOff: 0.21,
        mostFailed: WORDS.slice(0, 6).map(([text, meaning]) => ({ text, meaning, failures: int(8, 30), attempts: int(30, 80), users: int(5, 20) })),
        trend: Array.from({ length: 8 }, (_, i) => ({ week: day((7 - i) * 7), reviews: int(30, 60), accuracy: 0.6 + r() * 0.15, firstAttempt: 0.55 + i * 0.015 })),
        afterEffect: { words: 640, firstTime: 0.58, nextTime: 0.71 },
      },
      mostFailedWords: WORDS.slice(0, 10).map(([text, meaning]) => ({
        text, meaning, failures: int(6, 40), users: int(3, 18), skills: [{ skill: pick(SKILLS), count: int(3, 20) }],
      })).sort((a, b) => b.failures - a.failures),
    })
  }

  skill(skill: string, f: Filters): Promise<SkillDetail> {
    const s = skill as Skill
    const i = SKILLS.indexOf(s)
    const days = f.days ?? 30
    return wait({
      period: period(f), skill: s, summary: SKILL_SUMMARIES[i], previous: PREV_SUMMARIES[i],
      content: [
        { key: 'completion', label: 'إكمال التمرين', value: 0.74, format: 'pct' },
        { key: 'replays_per_session', label: 'إعادات لكل Session', value: 3.1, format: 'num' },
        { key: 'level_changes', label: 'تغييرات المستوى', value: 22, format: 'int' },
        { key: 'median_answer', label: 'وقت الإجابة (وسيط)', value: 14000, format: 'ms' },
      ],
      failures: FAILURES[i],
      byUser: USERS.slice(0, 8).map((u) => ({ ...u, evidence: `${int(2, 9)} فشل · ${int(3, 20)} نجاح` })),
      byLevel: ['A2', 'B1', 'B1_PLUS', 'B2', 'C1'].map((level) => ({ level, sessions: int(5, 40), firstAttemptAccuracy: 0.5 + r() * 0.35, abandoned: int(0, 8) })),
      daily: Array.from({ length: days }, (_, d) => ({ date: day(days - 1 - d), passed: int(4, 20), failed: int(1, 9), sessions: int(3, 14) })),
      attempts: [{ attempts: '1', words: 120 }, { attempts: '2', words: 46 }, { attempts: '3', words: 18 }, { attempts: '4', words: 7 }, { attempts: '5+', words: 3 }],
      next: { recovered: 41, waiting: 23, deleted: 4, total: 68 },
      words: WORDS.slice(0, 8).map(([text, meaning]) => ({ text, meaning, failures: int(3, 20), users: int(2, 10), skills: [{ skill: s, count: int(3, 20) }] })),
      mostTranslated: s === 'READING' ? WORDS.slice(3, 10).map(([word]) => ({ word, count: int(5, 40) })) : null,
      levelFlows: [],
    })
  }

  behavior(f: Filters): Promise<Behavior> {
    return wait({
      period: period(f), tracked: true, friction: FRICTION,
      time: {
        metrics: [
          { key: 'median_session', label: 'وسيط مدة الـSession', value: 252000, format: 'ms' },
          { key: 'avg_session', label: 'متوسط مدة الـSession', value: 318000, format: 'ms' },
          { key: 'learning_time', label: 'وقت التعلم الفعلي', value: 3.6e9, format: 'ms' },
          { key: 'app_time', label: 'وقت التطبيق', value: 5.1e9, format: 'ms' },
          { key: 'time_per_question', label: 'الوقت لكل سؤال (وسيط)', value: 13000, format: 'ms' },
          { key: 'time_to_first_answer', label: 'من بدء التمرين إلى أول إجابة', value: 41000, format: 'ms' },
          { key: 'feedback_reading', label: 'وقت قراءة الـFeedback', value: 7000, format: 'ms' },
          { key: 'ai_p95', label: 'زمن استجابة الـAI (P95)', value: 6400, format: 'ms' },
        ],
        sessionDistribution: ['أقل من دقيقة', '1–3 دقائق', '3–5 دقائق', '5–10 دقائق', '10–20 دقيقة', '20–40 دقيقة', 'أكثر من 40 دقيقة'].map((label) => ({ label, count: int(10, 200) })),
        answerDistribution: ['< 5 ث', '5–15 ث', '15–30 ث', '30–60 ث', '1–2 د', '> 2 د'].map((label) => ({ label, count: int(20, 400) })),
        answerBySkill: SKILLS.map((skill) => ({ skill, median: int(5, 40) * 1000, p90: int(40, 120) * 1000, count: int(100, 900) })),
        aiByOperation: ['content', 'speaking_turn', 'writing', 'speaking_eval'].map((operation) => ({ operation, calls: int(100, 900), errors: int(0, 8), median: int(1200, 4000), p95: int(4000, 9000) })),
      },
      content: SKILLS.map((skill) => ({ skill, metrics: [
        { key: 'completion', label: 'إكمال التمرين', value: 0.6 + r() * 0.3, format: 'pct' },
        { key: 'x', label: skill === 'READING' ? 'ترجمات لكل Session' : skill === 'LISTENING' ? 'إعادات لكل Session' : 'محاولات لكل كلمة', value: 1 + r() * 4, format: 'num' },
        { key: 'level_changes', label: 'تغييرات المستوى', value: int(2, 30), format: 'int' },
      ] })),
      mostTranslated: WORDS.slice(0, 8).map(([word]) => ({ word, count: int(5, 50) })).sort((a, b) => b.count - a.count),
      screens: [{ screen: 'session', views: 1200, users: 300, medianMs: 240000, quickExits: 40 }, { screen: 'hub', views: 2100, users: 410, medianMs: 18000, quickExits: 0 }],
      errors: [{ code: 'NETWORK', count: 21, users: 9 }, { code: 'AI_BUSY', count: 4, users: 3 }],
    })
  }

  friction(key: string, f: Filters): Promise<FrictionDetail> {
    const item = FRICTION.find((x) => x.key === key) ?? FRICTION[0]
    return wait({
      period: period(f), item, users: USERS.slice(0, item.affectedUsers).map((u) => ({ ...u, evidence: `${int(2, 12)} حدثًا` })),
      outcome: { completed: 41, open: 3, abandoned: 17 },
      byLevel: [{ level: 'A2', sessions: 6 }, { level: 'B1', sessions: 29 }, { level: 'B2', sessions: 12 }],
      bySkill: [{ skill: item.skill ?? 'LISTENING', sessions: 47 }],
      byDay: Array.from({ length: 14 }, (_, i) => ({ date: day(13 - i), sessions: int(0, 7) })),
      performance: { affectedAccuracy: 0.52, othersAccuracy: 0.71 },
      content: [{ title: 'A Day at the Market', sessions: 9, level: 'B1' }, { title: 'The Science of Sleep', sessions: 6, level: 'B1' }],
    })
  }

  retention(f: Filters): Promise<Retention> {
    const curve = ['< يوم', '1–2 يوم', '2–3 أيام', '3–5 أيام', '5–7 أيام', '1–2 أسبوع', '2–4 أسابيع', '> 4 أسابيع']
      .map((label, i) => ({ label, minHours: [0, 24, 48, 72, 120, 168, 336, 672][i], attempts: int(30, 300), success: 0.9 - i * 0.06 - r() * 0.03 }))
    return this.learning(f).then((l) => ({
      period: period(f),
      kpis: [kpi('d1', 'D1 Retention', 0.61, 0.58, 'pct'), kpi('d7', 'D7 Retention', 0.42, 0.39, 'pct'), kpi('d30', 'D30 Retention', 0.27, 0.25, 'pct'),
        { key: 'returning', label: 'مستخدمون عائدون', value: 740, previous: null, format: 'int' },
        { key: 'recall_success', label: 'Recall Success', value: 0.73, previous: null, format: 'pct' },
        { key: 'recall_failure', label: 'Recall Failure', value: 0.27, previous: null, format: 'pct' }],
      cohorts: Array.from({ length: 8 }, (_, w) => ({
        cohort: day((7 - w) * 7), size: int(20, 60),
        weeks: Array.from({ length: 8 }, (_, k) => (k < 7 - w ? Math.max(0.08, 0.62 - k * 0.07 - r() * 0.05) : null)),
      })),
      recall: { curve, bySkill: SKILLS.map((skill) => ({ skill, curve: curve.map((c) => ({ ...c, success: c.success! - r() * 0.1 })) })), review: curve, samples: 1840, configuredGapDays: 2 },
      recovery: { failed: 420, recovered: 301, rate: 0.72, medianDays: 2.4 },
      weeklyReview: l.weeklyReview,
    }))
  }

  users(f: Filters & { q?: string }): Promise<{ total: number; segment: string | null; rows: UserRow[] }> {
    const rows = USERS.filter((u) => !f.q || u.name.includes(f.q))
    return wait({ total: rows.length, segment: f.segment ?? null, rows })
  }

  attention(): Promise<AttentionGroups> {
    const groups = FLAGS.map((key) => ({
      key, label: { recently_inactive: 'توقف مؤخرًا', high_abandonment: 'انسحاب مرتفع', repeated_failures: 'فشل متكرر', speaking_failure: 'فشل متكرر في Speaking', high_overdue: 'متأخرات كثيرة', hoarder: 'يضيف كثيرًا ويتقن قليلًا' }[key]!,
      description: '', users: USERS.filter((u) => u.flags.includes(key)).map((u) => ({ ...u, evidence: 'دليل تجريبي' })),
    })).filter((g) => g.users.length > 0)
    return wait({ total: groups.reduce((s, g) => s + g.users.length, 0), groups })
  }

  user(id: string): Promise<User360> {
    const row = USERS.find((u) => u.id === id) ?? USERS[0]
    return wait({
      header: { row, email: row.email, phone: row.phone, whatsapp: `https://wa.me/${row.phone?.slice(1)}`, onboarding: 'COMPLETE', interests: ['technology', 'football'], appVersion: '1.2.0', platform: 'ios', funnelStage: 'first_mastered' },
      states: { total: row.words, learning: row.words - row.mastered, mastered: row.mastered, active: row.mastered, archived: 1, deleted: 1, due: 4, overdue: 2 },
      periods: [['today', 'اليوم'], ['week', 'هذا الأسبوع'], ['month', 'هذا الشهر'], ['all', 'منذ التسجيل']].map(([key, label], i) => ({
        key, label, added: i * 4 + 1, passed: i * 7 + 2, failed: i * 2, mastered: i, archived: 0, sessions: i * 5 + 1,
      })),
      skills: SKILL_SUMMARIES.map((summary) => ({ summary, selectedLevel: 'B1', assessedLevel: 'B1' })),
      words: WORDS.map(([text, meaning], i) => ({
        id: `w${i}`, text, meaning, state: i < 3 ? 'ACTIVE' : 'LEARNING', currentSkill: i < 3 ? null : SKILLS[i % 5], addedAt: iso(i * 3),
        exposures: int(0, 6), due: i % 4 === 0, overdue: i % 7 === 0, failures: int(0, 4),
        skills: SKILLS.map((skill, k) => ({ skill, status: i < 3 || k < i % 5 ? 'PASSED' : k === i % 5 ? (i % 3 === 0 ? 'FAILED' : 'AVAILABLE') : 'PENDING', attempts: int(0, 3), failures: k === i % 5 && i % 3 === 0 ? 1 : 0 })),
      })),
      levels: [{ skill: 'READING', from: 'A2', to: 'B1', type: 'USER_MANUAL_CHANGE', reason: 'changed during a session', at: iso(5) }],
      feedback: [],
      attention: row.flags.map((key) => ({ key, label: key, evidence: 'دليل تجريبي' })),
      activity: Array.from({ length: 90 }, (_, i) => ({ date: day(89 - i), count: r() < 0.45 ? int(1, 12) : 0 })),
    })
  }

  timeline(): Promise<{ events: TimelineEvent[] }> {
    const steps: [string, string, TimelineEvent['tone'], Skill | null][] = [
      ['word_mastered', 'أتقن «perspective»', 'good', null], ['skill_passed', 'اجتاز Writing — perspective', 'good', 'WRITING'],
      ['session_started', 'بدأ Writing', 'neutral', 'WRITING'], ['skill_passed', 'اجتاز Listening — perspective', 'good', 'LISTENING'],
      ['answer', 'إجابة صحيحة — perspective', 'good', 'LISTENING'], ['answer', 'إجابة خاطئة — perspective', 'bad', 'LISTENING'],
      ['audio_replay', 'أعاد تشغيل الصوت', 'neutral', 'LISTENING'], ['audio_replay', 'أعاد تشغيل الصوت', 'neutral', 'LISTENING'],
      ['session_started', 'بدأ Listening', 'neutral', 'LISTENING'], ['skill_passed', 'اجتاز Reading — perspective', 'good', 'READING'],
      ['translation', 'فتح الترجمة — perspective', 'neutral', 'READING'], ['session_started', 'بدأ Reading', 'neutral', 'READING'],
      ['word_added', 'أضاف كلمة «perspective»', 'neutral', null], ['onboarding', 'أكمل اختبار المستوى', 'good', null], ['signup', 'التسجيل', 'neutral', null],
    ]
    return wait({ events: steps.map(([kind, title, tone, skill], i) => ({
      id: `t${i}`, at: iso(i * 0.6), kind, title, tone, skill, word: null, sessionId: null, detail: { المستوى: 'B1', المدة: `${int(5, 60)} ث` },
    })) })
  }

  compare(a: string, b: string): Promise<{ a: CohortSide; b: CohortSide }> {
    const side = (key: string, shift: number): CohortSide => ({
      key, label: key, size: int(40, 300),
      metrics: [
        { key: 'active_days', label: 'متوسط أيام النشاط', value: 6 + shift * 3, format: 'num' },
        { key: 'sessions', label: 'متوسط Sessions', value: 14 + shift * 6, format: 'num' },
        { key: 'mastered', label: 'متوسط الكلمات المتقنة', value: 4 + shift * 2, format: 'num' },
        { key: 'd7', label: 'D7 Retention', value: 0.4 + shift * 0.1, format: 'pct' },
      ],
      skills: SKILL_SUMMARIES.map((s) => ({ skill: s.skill, firstAttemptAccuracy: (s.firstAttemptAccuracy ?? 0) + shift * 0.05, passRate: s.passRate, abandonmentRate: s.abandonmentRate })),
    })
    return wait({ a: side(a, 0), b: side(b, -1) })
  }

  feedback(): Promise<FeedbackPage> {
    return wait({
      summary: { total: FEEDBACK.length, unread: FEEDBACK.filter((f) => f.status === 'NEW').length, lastWeek: 4, byCategory: [{ category: 'LISTENING', count: 2, unread: 1 }, { category: 'SPEAKING', count: 1, unread: 1 }, { category: 'OTHER', count: 1, unread: 1 }, { category: 'SPELLING', count: 1, unread: 0 }] },
      repeated: [{ term: 'الصوت', messages: 2, examples: ['f-0', 'f-3'] }, { term: 'سريع', messages: 2, examples: ['f-0', 'f-3'] }],
      items: FEEDBACK,
    })
  }
  async addNote(id: string, body: string) {
    FEEDBACK.find((f) => f.id === id)?.notes.push({ id: String(Date.now()), body, author: 'Owner', createdAt: new Date().toISOString() })
  }
  async setFeedbackHandled(id: string, handled: boolean) {
    const f = FEEDBACK.find((x) => x.id === id)
    if (f) f.status = handled ? 'HANDLED' : 'NEW'
  }

  inquiries() {
    return wait({ items: HISTORY })
  }
  async inquiry(id: string): Promise<Investigation> {
    const h = HISTORY.find((x) => x.id === id) ?? HISTORY[0]
    return this.ask(h.section, h.question)
  }
  ask(section: string, question: string): Promise<Investigation> {
    const inv: Investigation = {
      id: `q-${Date.now()}`, section, sectionLabel: section, question, createdAt: new Date().toISOString(), author: 'Owner',
      summary: 'الانسحاب من Speaking يتركز بعد أول رد لم يستخدم الكلمة المطلوبة، وأعلى ما يكون عند مستوى B1+.',
      evidence: [
        { label: 'Speaking — الانسحاب', value: 0.31, format: 'pct', previous: 0.24 },
        { label: 'Speaking — دقة المحاولة الأولى', value: 0.58, format: 'pct', previous: 0.61 },
        { label: 'استخدام الكلمة المطلوبة', value: 0.47, format: 'pct', context: 'الإجابات التي استخدمت كلمة مستهدفة' },
      ],
      charts: [{ id: 'failures_speaking', type: 'bar', title: 'أسباب الفشل في Speaking', unit: 'int', labels: REASONS.SPEAKING.map((x) => x[1]), series: [{ name: 'حالات', values: REASONS.SPEAKING.map(() => int(4, 30)) }] }],
      affectedUsers: USERS.slice(0, 6).map((u) => ({ ...u, evidence: 'انسحب من Speaking' })), affectedLabel: 'انسحبوا من Speaking',
      where: [{ dimension: 'المستوى', value: 'B1_PLUS', detail: 'أدنى دقة: 49%' }, { dimension: 'السبب', value: 'لم يستخدم الكلمة المطلوبة', detail: 'مشكلة تعلم' }],
      comparison: { title: 'Speaking: الحالية مقابل السابقة', aLabel: 'الحالية', bLabel: 'السابقة', rows: [{ label: 'الانسحاب', a: 0.31, b: 0.24, format: 'pct' }, { label: 'دقة المحاولة الأولى', a: 0.58, b: 0.61, format: 'pct' }] },
      interpretation: ['الانسحاب ارتفع من 24% إلى 31%.', 'أكثر أسباب الفشل «لم يستخدم الكلمة المطلوبة».'],
      hypotheses: ['قد لا يكون واضحًا للمتعلم أن عليه استخدام الكلمة في رده (UX).', 'قد تكون أسئلة المحادثة عند B1+ أصعب من المستوى (محتوى).'],
      investigate: [{ title: 'افتح Timeline من انسحبوا', why: 'هل انسحبوا بعد أول رد خاطئ؟' }],
      interpretedBy: 'ai', dataNote: 'بيانات تجريبية — ليست من قاعدة البيانات.', periodFrom: iso(30), periodTo: iso(0),
    }
    return wait(inv, 900)
  }

  system() {
    return wait({
      configuration: { skillIntervalDays: 2, weeklyReviewPeriodDays: 7, weeklyReviewMaturityDays: 7, maxAttemptsPerItem: 3, reportingUtcOffsetHours: 3 },
      thresholds: { overdueDays: 3, abandonAfterHours: 24, inactiveDays: 3, sessionCapMinutes: 60, weakAccuracy: 0.6, minimumSample: 3 },
      tracking: [{ name: 'answer_submitted', source: 'SERVER', count: 1200 }, { name: 'audio_replayed', source: 'CLIENT', count: 340 }],
      versions: [{ version: '1.2.0', platform: 'ios', users: 140 }, { version: '1.2.0', platform: 'android', users: 210 }],
      admins: [{ name: 'Owner', email: 'owner@wordos.app', role: 'OWNER' }],
    })
  }
  audit() {
    return wait({ items: [{ id: 1, action: 'user.viewed', targetId: USERS[0].id, detail: null, address: '127.0.0.1', createdAt: iso(0), actor: 'Owner' }] })
  }
}
