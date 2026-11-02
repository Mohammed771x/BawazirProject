// Mirrors `backend/src/WordOs.Api/Admin/*` exactly (docs/05-API-CONTRACT.md §Admin intelligence).
// Every value that can be undefined is `number | null`: "no data" and "zero"
// are different answers, and a chart that draws a missing value as 0% lies.

export type Skill = 'READING' | 'LISTENING' | 'SPEAKING' | 'WRITING' | 'SPELLING'
export type Format = 'int' | 'pct' | 'num' | 'ms' | 'min'

export interface Filters {
  days?: number
  from?: string
  to?: string
  level?: string
  skill?: string
  interest?: string
  status?: string
  segment?: string
  userId?: string
}

export interface Period {
  from: string
  to: string
  prevFrom: string
  prevTo: string
  days: number
  learners: number
}

export interface Kpi {
  key: string
  label: string
  value: number | null
  previous: number | null
  format: Format
  hint?: string | null
}

export interface Metric {
  key: string
  label: string
  value: number | null
  format: Format
  hint?: string | null
}

export interface DayPoint {
  date: string
  activeUsers: number
  sessions: number
  learningMinutes: number
  newUsers: number
}

export interface FunnelStage {
  key: string
  label: string
  hint: string
  count: number
  share: number | null
  dropOff: number | null
}

export interface SkillSummary {
  skill: Skill
  sessions: number
  completed: number
  abandoned: number
  practice: number
  words: number
  questions: number
  attempts: number
  passed: number
  failed: number
  firstAttemptAccuracy: number | null
  overallAccuracy: number | null
  passRate: number | null
  failRate: number | null
  retryRate: number | null
  abandonmentRate: number | null
  avgSessionMs: number | null
  medianSessionMs: number | null
  medianAnswerMs: number | null
}

export interface StageCount { skill: Skill; waiting: number; due: number; overdue: number }

export interface Lifecycle {
  added: number
  studied: number
  learning: number
  mature: number
  active: number
  archived: number
  deleted: number
  due: number
  overdue: number
  byStage: StageCount[]
  trend: { date: string; added: number; passed: number; activated: number }[]
}

export interface Signal {
  severity: 'high' | 'medium' | 'low'
  area: string
  title: string
  detail: string
  link: string | null
}

export interface Overview {
  period: Period
  health: Kpi[]
  engagement: DayPoint[]
  funnel: FunnelStage[]
  lifecycle: Lifecycle
  skills: SkillSummary[]
  signals: Signal[]
}

export interface Reason { key: string; label: string; category: string; count: number }
export interface SkillFailures { skill: Skill; total: number; reasons: Reason[] }
export interface CategoryCount { category: string; label: string; count: number }
export interface LevelFlow { skill: Skill; from: string; to: string; count: number; direction: 'up' | 'down' | 'same' }
export interface LevelSkillSummary { skill: Skill; up: number; down: number; manual: number; system: number }
export interface LevelRow { level: string; downgrades: number; upgrades: number }
export interface LevelDistribution { skill: Skill; levels: { level: string; selected: number; assessed: number }[] }

export interface FailedWord {
  text: string
  meaning: string
  failures: number
  users: number
  skills: { skill: Skill | null; count: number }[]
}

export interface WeeklyReviewStats {
  started: number
  completed: number
  users: number
  wordsReviewed: number
  accuracy: number | null
  firstAttemptAccuracy: number | null
  medianMs: number | null
  dropOff: number | null
  mostFailed: { text: string; meaning: string; failures: number; attempts: number; users: number }[]
  trend: { week: string; reviews: number; accuracy: number | null; firstAttempt: number | null }[]
  afterEffect: { words: number; firstTime: number | null; nextTime: number | null }
}

export interface Learning {
  period: Period
  skills: { current: SkillSummary; previous: SkillSummary }[]
  failures: SkillFailures[]
  categories: CategoryCount[]
  levels: {
    flows: LevelFlow[]
    bySkill: LevelSkillSummary[]
    byLevel: LevelRow[]
    distribution: LevelDistribution[]
  }
  weeklyReview: WeeklyReviewStats
  mostFailedWords: FailedWord[]
}

export interface UserRow {
  id: string
  name: string
  email: string
  phone: string | null
  joinedAt: string
  lastActiveAt: string | null
  activeDays: number
  sessions: number
  words: number
  mastered: number
  level: string | null
  status: 'new' | 'active' | 'inactive'
  streak: number
  flags: string[]
  evidence?: string | null
}

export interface SkillDetail {
  period: Period
  skill: Skill
  summary: SkillSummary
  previous: SkillSummary
  content: Metric[]
  failures: SkillFailures
  byUser: UserRow[]
  byLevel: { level: string; sessions: number; firstAttemptAccuracy: number | null; abandoned: number }[]
  daily: { date: string; passed: number; failed: number; sessions: number }[]
  attempts: { attempts: string; words: number }[]
  /** ADR-132 — the skill split by the prompt version that wrote its content. */
  byVersion?: {
    version: string; model: string | null; first: string; last: string; sessions: number; learners: number
    completion: number | null; abandoned: number; questions: number; firstAttemptAccuracy: number | null
    retryRate: number | null; fallback: number | null; medianSessionMs: number | null
  }[]
  next: { recovered: number; waiting: number; deleted: number; total: number }
  words: FailedWord[]
  mostTranslated: { word: string; count: number }[] | null
  levelFlows: LevelFlow[]
}

export interface FrictionItem {
  key: string
  title: string
  description: string
  skill: Skill | null
  value: number | null
  format: Format
  baseline: number | null
  affectedUsers: number
  severity: 'high' | 'medium' | 'low' | 'none'
  category: string
}

export interface Bucket { label: string; count: number }

export interface TimeStats {
  metrics: Metric[]
  sessionDistribution: Bucket[]
  answerDistribution: Bucket[]
  answerBySkill: { skill: Skill; median: number | null; p90: number | null; count: number }[]
  aiByOperation: { operation: string; calls: number; errors: number; median: number | null; p95: number | null }[]
}

export interface Behavior {
  period: Period
  tracked: boolean
  friction: FrictionItem[]
  time: TimeStats
  content: { skill: Skill; metrics: Metric[] }[]
  mostTranslated: { word: string; count: number }[]
  screens: { screen: string; views: number; users: number; medianMs: number | null; quickExits: number }[]
  errors: { code: string; count: number; users: number }[]
}

export interface FrictionDetail {
  period: Period
  item: FrictionItem
  users: UserRow[]
  outcome: { completed: number; open: number; abandoned: number }
  byLevel: { level: string; sessions: number }[]
  bySkill: { skill: Skill; sessions: number }[]
  byDay: { date: string; sessions: number }[]
  performance: { affectedAccuracy: number | null; othersAccuracy: number | null }
  content: { title: string; sessions: number; level: string }[]
}

export interface RecallPoint { label: string; minHours: number; attempts: number; success: number | null }
export interface CohortRow { cohort: string; size: number; weeks: (number | null)[] }

export interface Retention {
  period: Period
  kpis: Kpi[]
  cohorts: CohortRow[]
  recall: {
    curve: RecallPoint[]
    bySkill: { skill: Skill; curve: RecallPoint[] }[]
    review: RecallPoint[]
    samples: number
    configuredGapDays: number
  }
  recovery: { failed: number; recovered: number; rate: number | null; medianDays: number | null }
  weeklyReview: WeeklyReviewStats
}

export interface UsersList { total: number; segment: string | null; rows: UserRow[] }

export interface AttentionGroups {
  total: number
  groups: { key: string; label: string; description: string; users: UserRow[] }[]
}

export interface User360 {
  header: {
    row: UserRow
    email: string
    phone: string | null
    whatsapp: string | null
    onboarding: string
    interests: string[]
    appVersion: string | null
    platform: string | null
    funnelStage: string
  }
  states: {
    total: number
    learning: number
    mastered: number
    active: number
    archived: number
    deleted: number
    due: number
    overdue: number
  }
  periods: { key: string; label: string; added: number; passed: number; failed: number; mastered: number; archived: number; sessions: number }[]
  skills: { summary: SkillSummary; selectedLevel: string | null; assessedLevel: string | null }[]
  words: {
    id: string
    text: string
    meaning: string
    state: string
    currentSkill: Skill | null
    addedAt: string
    exposures: number
    due: boolean
    overdue: boolean
    failures: number
    skills: { skill: Skill; status: string | null; attempts: number; failures: number }[]
  }[]
  levels: { skill: Skill; from: string | null; to: string | null; type: string; reason: string; at: string }[]
  feedback: { id: string; body: string; category: string | null; status: string; createdAt: string }[]
  attention: { key: string; label: string; evidence: string }[]
  activity: { date: string; count: number }[]
}

export interface TimelineEvent {
  id: string
  at: string
  kind: string
  title: string
  tone: 'good' | 'bad' | 'neutral'
  skill: Skill | null
  word: string | null
  sessionId: string | null
  detail: Record<string, string>
}

export interface CohortSide {
  key: string
  label: string
  size: number
  metrics: Metric[]
  skills: { skill: Skill; firstAttemptAccuracy: number | null; passRate: number | null; abandonmentRate: number | null }[]
}

export interface FeedbackItem {
  id: string
  body: string
  category: string | null
  status: 'NEW' | 'HANDLED'
  createdAt: string
  handledAt: string | null
  appVersion: string | null
  platform: string | null
  user: { id: string; name: string; email: string; phone: string | null } | null
  notes: { id: string; body: string; author: string; createdAt: string }[]
}

export interface FeedbackPage {
  summary: {
    total: number
    unread: number
    lastWeek: number
    byCategory: { category: string; count: number; unread: number }[]
  }
  repeated: { term: string; messages: number; examples: string[] }[]
  items: FeedbackItem[]
}

export interface ChartSpec {
  id: string
  type: 'line' | 'bar' | 'funnel' | 'distribution' | 'donut'
  title: string
  unit: Format
  labels: string[]
  series: { name: string; values: (number | null)[] }[]
  note?: string | null
}

export interface Investigation {
  id: string
  section: string
  sectionLabel: string
  question: string
  createdAt: string
  author: string | null
  summary: string
  evidence: { label: string; value: number | null; format: Format; context?: string | null; previous?: number | null }[]
  charts: ChartSpec[]
  affectedUsers: UserRow[]
  affectedLabel: string
  where: { dimension: string; value: string; detail: string }[]
  comparison: { title: string; aLabel: string; bLabel: string; rows: { label: string; a: number | null; b: number | null; format: Format }[] } | null
  interpretation: string[]
  hypotheses: string[]
  investigate: { title: string; why: string }[]
  interpretedBy: 'ai' | 'rules'
  dataNote: string
  periodFrom: string
  periodTo: string
  /** ADR-132 — absent on investigations saved before it. */
  parentId?: string | null
  mode?: 'data' | 'discussion'
  filter?: Filters | null
}

export interface InquirySummary {
  id: string
  section: string
  question: string
  summary: string
  interpretedBy: string
  createdAt: string
  author: string | null
  parentId?: string | null
  followUpOn?: string | null
}

// ── Decide → Implement → Measure → Learn (ADR-132) ─────────────────────────

export interface MeasureDef {
  key: string
  label: string
  area: string
  format: Format
  goodWhenUp: boolean
  definition: string
  formula: string
  source: string
  sampleUnit: string
  limitation: string | null
  revised: string
}

export interface Definitions {
  metrics: MeasureDef[]
  events: { name: string; source: 'client' | 'server' | 'table'; where: string; meaning: string }[]
  brief: { requested: string; status: 'recorded' | 'derived' | 'partial' | 'missing'; where: string; note: string | null }[]
  thresholds: Record<string, number>
}

export interface MetricTarget { key: string; direction: 'up' | 'down'; target?: number | null }

export type Outcome = 'improved' | 'worsened' | 'flat' | 'insufficient' | 'unknown'

export interface MeasuredMetric {
  key: string
  label: string
  format: Format
  direction: 'up' | 'down'
  target: number | null
  before: number | null
  beforeSample: number
  after: number | null
  afterSample: number
  change: number | null
  relativeChange: number | null
  outcome: Outcome
  targetMet: boolean | null
  comparisonBefore: number | null
  comparisonAfter: number | null
  comparisonSample: number | null
  netOfComparison: number | null
}

export interface Evaluation {
  computedAt: string
  population: string
  populationSize: number
  before: { from: string; to: string; days: number }
  after: { from: string; to: string; days: number }
  afterComplete: boolean
  comparisonPopulation: string | null
  targeted: MeasuredMetric[]
  guardrails: MeasuredMetric[]
  overlappingChanges: { id: string; title: string; kind: string; shippedOn: string }[]
  cautions: string[]
  summary: string
}

export type DecisionStatus = 'PROPOSED' | 'APPROVED' | 'IMPLEMENTED' | 'CLOSED' | 'REJECTED'
export type Verdict = 'SUCCEEDED' | 'PARTIAL' | 'NOEFFECT' | 'WORSE' | 'INCONCLUSIVE'

export interface DecisionRow {
  id: string
  title: string
  area: string
  status: DecisionStatus
  verdict: Verdict | null
  assignee: string | null
  approvedAt: string | null
  implementedOn: string | null
  reviewOn: string | null
  reviewState: 'done' | 'unscheduled' | 'due' | 'scheduled' | 'measured'
  reviewedAt: string | null
  metrics: string[]
  inquiryId: string | null
  author: string | null
  createdAt: string
  updatedAt: string
}

export interface DecisionInput {
  title: string
  area: string
  problem: string
  action: string
  evidence?: string | null
  hypothesis?: string | null
  assignee?: string | null
  expectedOutcome?: string | null
  inquiryId?: string | null
  segment?: string | null
  baselineFrom?: string | null
  baselineTo?: string | null
  metrics?: MetricTarget[]
  reviewOn?: string | null
}

export interface HistoryItem { action: string; detail: string | null; at: string; actor: string | null }

export interface DecisionDetail {
  decision: DecisionInput & {
    id: string
    segmentLabel: string | null
    status: DecisionStatus
    verdict: Verdict | null
    approvedBy: string | null
    approvedAt: string | null
    implementedOn: string | null
    reviewState: DecisionRow['reviewState']
    actualOutcome: string | null
    author: string | null
    createdAt: string
    updatedAt: string
    metrics: MetricTarget[]
  }
  inquiry: { id: string; section: string; question: string; summary: string; createdAt: string } | null
  changes: { id: string; title: string; kind: string; isExperiment: boolean; shippedOn: string }[]
  evaluation: Evaluation | null
  evaluationNote: string
  /** Taken by the system once the review date passed; null before, or after the date moved. */
  review: Evaluation | null
  reviewedAt: string | null
  history: HistoryItem[]
}

export interface ChangeInput {
  title: string
  kind: string
  isExperiment: boolean
  description: string
  shippedOn: string
  problem?: string | null
  hypothesis?: string | null
  version?: string | null
  segment?: string | null
  comparisonSegment?: string | null
  decisionId?: string | null
  metrics?: MetricTarget[]
  windowDays?: number | null
}

export interface ChangeRow {
  id: string
  title: string
  kind: string
  isExperiment: boolean
  version: string | null
  shippedOn: string
  segment: string | null
  comparisonSegment: string | null
  decisionId: string | null
  verdict: Verdict | null
  metrics: string[]
  author: string | null
  concluded: boolean
}

export interface ChangeDetail {
  change: ChangeInput & {
    id: string
    segmentLabel: string | null
    comparisonLabel: string | null
    metrics: MetricTarget[]
    windowDays: number
    sideEffects: string | null
    result: string | null
    recommendation: string | null
    verdict: Verdict | null
    author: string | null
    createdAt: string
    updatedAt: string
  }
  decision: { id: string; title: string; status: DecisionStatus } | null
  evaluation: Evaluation
  frozen: Evaluation | null
  history: HistoryItem[]
}

export interface NotificationsPage {
  period: Period
  kpis: Kpi[]
  journey: { key: string; label: string; count: number }[]
  byKind: { kind: string; planned: number; opened: number; converted: number }[]
  byHour: number[]
  trend: { date: string; opened: number; converted: number }[]
  users: UserRow[]
  notes: string[]
}

export interface Meta {
  me: { id: string; name: string; email: string; role: 'OWNER' | 'ANALYST' }
  canSeeContact: boolean
  skills: Skill[]
  levels: string[]
  interests: { interest: string; count: number }[]
  segments: { key: string; label: string }[]
  attention: { key: string; label: string; description: string }[]
  funnel: { key: string; label: string; hint: string }[]
  tracking: { clientSince: string | null; serverSince: string | null }
}

export interface SystemInfo {
  configuration: Record<string, number>
  thresholds: Record<string, number>
  tracking: { name: string; source: string; count: number }[]
  versions: { version: string | null; platform: string | null; users: number }[]
  admins: { name: string; email: string; role: string }[]
}

export interface AuditItem {
  id: number
  action: string
  targetId: string | null
  detail: string | null
  address: string | null
  createdAt: string
  actor: string | null
}

export interface Session {
  token: string
  refreshToken: string
  expiresAt: string
  user: { id: string; email: string; displayName: string; role: string }
}

/** One learner's one day — what a square of the activity calendar opens (ADR-132). */
export interface DayDetail {
  date: string
  phoneReported: boolean
  summary: {
    appOpens: number | null; appSessions: number | null; appMs: number | null; learningMs: number | null
    sessions: number; practice: number; completed: number; abandoned: number; questions: number; attempts: number
    firstAttemptAccuracy: number | null; passed: number; failed: number; wordsAdded: number; enteredActive: number
    reviews: number; translations: number; replays: number; hints: number; notificationsOpened: number
  }
  skills: {
    skill: Skill; sessions: number; practice: number; completed: number; abandoned: number; learningMs: number | null
    questions: number; attempts: number; firstAttemptAccuracy: number | null; passed: number; failed: number; levels: string[]
  }[]
  wordsAdded: { id: string; text: string; deleted: boolean }[]
  events: TimelineEvent[]
}
