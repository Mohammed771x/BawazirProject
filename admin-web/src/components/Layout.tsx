import {
  Activity, AlertOctagon, LayoutDashboard, LogOut, Menu, MessageSquareText, Moon, Repeat,
  Search, Settings, Sun, Users, GraduationCap,
} from 'lucide-react'
import { useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation } from 'react-router-dom'
import { isMock } from '../api'
import { useAuth } from '../auth/AuthContext'
import { FilterBar } from './FilterBar'
import { initials } from './UserTable'

const NAV = [
  { to: '/', label: 'نظرة عامة', icon: LayoutDashboard, end: true },
  { to: '/users', label: 'المستخدمون', icon: Users },
  { to: '/learning', label: 'التعلم والمهارات', icon: GraduationCap },
  { to: '/behavior', label: 'سلوك المستخدم وUX', icon: Activity },
  { to: '/retention', label: 'Retention', icon: Repeat },
  { to: '/problems', label: 'المشكلات والتحليل', icon: AlertOctagon },
  { to: '/feedback', label: 'Feedback', icon: MessageSquareText },
  { to: '/inquiry', label: 'تحليل واستفسار', icon: Search },
  { to: '/settings', label: 'إعدادات النظام', icon: Settings },
]

// Pages that are about one thing, not a population: global filters would
// change nothing there, so they are not shown.
const NO_FILTERS = [/^\/feedback/, /^\/inquiry/, /^\/settings/, /^\/users\/[^/]+/]

function useTheme() {
  const [theme, setTheme] = useState<string | null>(() => {
    try { return localStorage.getItem('wordos.admin.theme') } catch { return null }
  })
  useEffect(() => {
    if (theme) document.documentElement.dataset.theme = theme
    else delete document.documentElement.dataset.theme
    try { if (theme) localStorage.setItem('wordos.admin.theme', theme); else localStorage.removeItem('wordos.admin.theme') } catch { /* ignore */ }
  }, [theme])
  const dark = theme ? theme === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches
  return { dark, toggle: () => setTheme(dark ? 'light' : 'dark') }
}

export function Layout() {
  const { meta, signOut } = useAuth()
  const location = useLocation()
  const [open, setOpen] = useState(false)
  const { dark, toggle } = useTheme()
  const showFilters = !NO_FILTERS.some((r) => r.test(location.pathname))

  useEffect(() => setOpen(false), [location.pathname])

  return (
    <div className="shell">
      <aside className={`sidebar ${open ? 'open' : ''}`} aria-label="التنقل">
        <div className="brand">
          <div className="brand-mark">W</div>
          <div>
            <div className="brand-title">WordOS Admin</div>
            <div className="brand-sub">Product Intelligence &amp; Learning Analytics</div>
          </div>
        </div>
        {NAV.map(({ to, label, icon: Icon, end }) => (
          <NavLink key={to} to={to + (showFilters && location.search && to !== '/feedback' && to !== '/inquiry' && to !== '/settings' ? location.search : '')}
            end={end} className={({ isActive }) => `nav-item ${isActive ? 'active' : ''}`}>
            <Icon />{label}
          </NavLink>
        ))}
        <div className="sidebar-foot">
          <button className="nav-item" style={{ border: 0, background: 'none', cursor: 'pointer' }} onClick={toggle}>
            {dark ? <Sun /> : <Moon />}{dark ? 'الوضع الفاتح' : 'الوضع الداكن'}
          </button>
          <div className="me">
            <span className="avatar">{initials(meta?.me.name ?? '?')}</span>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ fontWeight: 500, fontSize: 13 }}>{meta?.me.name}</div>
              <div className="muted" style={{ fontSize: 11.5 }}>{meta?.me.role === 'OWNER' ? 'المالك' : 'محلل'}</div>
            </div>
            <button className="btn ghost sm" onClick={signOut} title="تسجيل الخروج" aria-label="تسجيل الخروج"><LogOut /></button>
          </div>
        </div>
      </aside>
      {open && <div className="overlay" style={{ zIndex: 55 }} onClick={() => setOpen(false)} />}
      <div className="main">
        {isMock && <div className="mock-banner">بيانات تجريبية — هذا الوضع لا يتصل بقاعدة البيانات (VITE_ADMIN_MOCK=true).</div>}
        <header className="topbar">
          <button className="btn ghost sm menu-btn" onClick={() => setOpen(true)} aria-label="القائمة"><Menu /></button>
          {showFilters ? <FilterBar /> : <div style={{ flex: 1 }} />}
        </header>
        <main className="content">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
