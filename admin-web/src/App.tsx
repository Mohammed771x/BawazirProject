import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import { LoginPage } from './auth/LoginPage'
import { Layout } from './components/Layout'
import { PageSkeleton } from './components/ui'
import { BehaviorPage, FrictionPage } from './pages/BehaviorPage'
import { ChangesPage } from './pages/ChangesPage'
import { DecisionsPage } from './pages/DecisionsPage'
import { FeedbackPage } from './pages/FeedbackPage'
import { InquiryPage } from './pages/InquiryPage'
import { LearningPage } from './pages/LearningPage'
import { NotificationsPage } from './pages/NotificationsPage'
import { OverviewPage } from './pages/OverviewPage'
import { ProblemsPage } from './pages/ProblemsPage'
import { RetentionPage } from './pages/RetentionPage'
import { SettingsPage } from './pages/SettingsPage'
import { SkillDetailPage } from './pages/SkillDetailPage'
import { User360Page } from './pages/User360Page'
import { UsersPage } from './pages/UsersPage'

/**
 * Twelve sections, each with its drill-downs beneath it:
 * Overview → Analysis → Investigation → User → Event, and then
 * Decide → Implement → Measure (ADR-132).
 */
export default function App() {
  const { meta, ready } = useAuth()
  if (!ready) return <div className="content"><PageSkeleton /></div>
  if (!meta) return <LoginPage />

  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<OverviewPage />} />
        <Route path="users" element={<UsersPage />} />
        <Route path="users/:id" element={<User360Page />} />
        <Route path="learning" element={<LearningPage />} />
        <Route path="learning/:skill" element={<SkillDetailPage />} />
        <Route path="behavior" element={<BehaviorPage />} />
        <Route path="behavior/friction/:key" element={<FrictionPage />} />
        <Route path="retention" element={<RetentionPage />} />
        <Route path="notifications" element={<NotificationsPage />} />
        <Route path="problems" element={<ProblemsPage />} />
        <Route path="feedback" element={<FeedbackPage />} />
        <Route path="inquiry" element={<InquiryPage />} />
        <Route path="inquiry/:id" element={<InquiryPage />} />
        <Route path="decisions" element={<DecisionsPage />} />
        <Route path="decisions/:id" element={<DecisionsPage />} />
        <Route path="changes" element={<ChangesPage />} />
        <Route path="changes/:id" element={<ChangesPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  )
}
