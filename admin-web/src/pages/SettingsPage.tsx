import { ShieldCheck } from 'lucide-react'
import { api } from '../api'
import { useAuth } from '../auth/AuthContext'
import { Card, Empty, Loader } from '../components/ui'
import { formatDateTime } from '../lib/format'
import { useAsync } from '../lib/useAsync'

const CONFIG_LABEL: Record<string, string> = {
  skillIntervalDays: 'الفجوة بين المهارات (أيام)', weeklyReviewPeriodDays: 'دورة Weekly Review (أيام)',
  weeklyReviewMaturityDays: 'نضج الكلمة للمراجعة (أيام)', maxAttemptsPerItem: 'أقصى محاولات للسؤال', reportingUtcOffsetHours: 'منطقة التقارير (UTC+)',
  overdueDays: 'متأخرة بعد (أيام)', abandonAfterHours: 'منسحب بعد (ساعات)', inactiveDays: 'غير نشط بعد (أيام)',
  sessionCapMinutes: 'أطول Session تُحتسب (دقائق)', weakAccuracy: 'عتبة الضعف (دقة)', minimumSample: 'أقل عينة', longIdleSeconds: 'توقف طويل (ثوانٍ)',
  immediateBackSeconds: 'رجوع فوري (ثوانٍ)', replayStruggle: 'إعادات تعني صعوبة', maxListedUsers: 'أقصى مستخدمين في قائمة',
}

const ACTION_LABEL: Record<string, string> = {
  'user.viewed': 'فتح User 360', 'inquiry.created': 'أنشأ تحقيقًا', 'feedback.note': 'أضاف ملاحظة داخلية',
  'feedback.handled': 'أغلق ملاحظة', 'feedback.reopened': 'أعاد فتح ملاحظة',
}

export function SettingsPage() {
  const { meta } = useAuth()
  const system = useAsync(() => api.system(), [])
  const owner = meta?.me.role === 'OWNER'
  const audit = useAsync(() => (owner ? api.audit() : Promise.resolve({ items: [] })), [owner])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>إعدادات النظام</h1>
          <p>القيم التي تحكم التعلم والتحليل، صحة التتبع، الصلاحيات، وسجل التدقيق. القيم تُغيَّر في إعدادات الخادم لا من هنا (R3).</p>
        </div>
      </div>
      <Loader state={system}>{(s) => (
        <>
          <div className="grid g-2">
            <Card flush title="قواعد التعلم" subtitle="من WordOsConfiguration">
              <table className="table"><tbody>{Object.entries(s.configuration).map(([k, v]) => <tr key={k}><td>{CONFIG_LABEL[k] ?? k}</td><td className="n">{v}</td></tr>)}</tbody></table>
            </Card>
            <Card flush title="عتبات التحليل" subtitle="AdminIntel — ما يعنيه «متأخر» و«منسحب» و«ضعيف»">
              <table className="table"><tbody>{Object.entries(s.thresholds).map(([k, v]) => <tr key={k}><td>{CONFIG_LABEL[k] ?? k}</td><td className="n">{v}</td></tr>)}</tbody></table>
            </Card>
          </div>
          <div className="grid g-2">
            <Card flush title="صحة التتبع — آخر 24 ساعة" subtitle={meta?.tracking.clientSince ? `أحداث التطبيق منذ ${formatDateTime(meta.tracking.clientSince)}` : 'لم تصل أحداث من التطبيق بعد'}>
              {s.tracking.length === 0 ? <Empty title="لا أحداث في آخر 24 ساعة" /> : (
                <table className="table"><thead><tr><th>الحدث</th><th>المصدر</th><th className="n">العدد</th></tr></thead>
                  <tbody>{s.tracking.map((t) => <tr key={t.name + t.source}><td className="ltr" style={{ textAlign: 'right' }}>{t.name}</td><td><span className="badge">{t.source === 'CLIENT' ? 'التطبيق' : 'الخادم'}</span></td><td className="n">{t.count}</td></tr>)}</tbody></table>
              )}
            </Card>
            <Card flush title="إصدارات التطبيق — آخر 30 يومًا">
              {s.versions.length === 0 ? <Empty title="لا بيانات إصدار بعد" /> : (
                <table className="table"><thead><tr><th>الإصدار</th><th>المنصة</th><th className="n">مستخدمون</th></tr></thead>
                  <tbody>{s.versions.map((v, i) => <tr key={i}><td className="ltr" style={{ textAlign: 'right' }}>{v.version}</td><td>{v.platform}</td><td className="n">{v.users}</td></tr>)}</tbody></table>
              )}
            </Card>
          </div>
          <Card flush title={<span className="row"><ShieldCheck size={16} />الصلاحيات</span>} subtitle="المالك يرى كل شيء. المحلل يرى كل الأرقام دون بيانات التواصل، ولا يرى سجل التدقيق. الترقية تتم عبر SQL فقط (ADR-061).">
            <table className="table"><thead><tr><th>الاسم</th><th>البريد</th><th>الدور</th></tr></thead>
              <tbody>{s.admins.map((a) => <tr key={a.email}><td>{a.name}</td><td className="ltr" style={{ textAlign: 'right' }}>{a.email}</td><td><span className={`badge ${a.role === 'OWNER' ? 'brand' : ''}`}>{a.role === 'OWNER' ? 'مالك' : 'محلل'}</span></td></tr>)}</tbody></table>
          </Card>
        </>
      )}</Loader>
      {owner && (
        <Card flush title="سجل التدقيق" subtitle="من فتح بيانات أي مستخدم، ومن غيّر ماذا — آخر 200 عملية">
          <Loader state={audit}>{(a) => a.items.length === 0 ? <Empty title="لا عمليات بعد" /> : (
            <table className="table"><thead><tr><th>الوقت</th><th>المسؤول</th><th>العملية</th><th>الهدف</th><th>العنوان</th></tr></thead>
              <tbody>{a.items.map((i) => (
                <tr key={i.id}><td className="muted">{formatDateTime(i.createdAt)}</td><td>{i.actor}</td><td>{ACTION_LABEL[i.action] ?? i.action}{i.detail && <span className="muted"> · {i.detail}</span>}</td>
                  <td className="ltr muted" style={{ fontSize: 11.5, textAlign: 'right' }}>{i.targetId?.slice(0, 13)}</td><td className="ltr muted" style={{ textAlign: 'right' }}>{i.address}</td></tr>
              ))}</tbody></table>
          )}</Loader>
        </Card>
      )}
    </>
  )
}
