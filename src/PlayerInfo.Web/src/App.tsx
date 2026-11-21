import { useEffect, useState } from 'react';
import { Navigate, NavLink, Outlet, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth';
import { BriefingPage } from './pages/BriefingPage';
import { DashboardPage } from './pages/DashboardPage';
import { DatabasePage } from './pages/DatabasePage';
import { GeminiPage } from './pages/GeminiPage';
import { LoginPage } from './pages/LoginPage';
import { LogsPage } from './pages/LogsPage';
import { NewsArticlesPage } from './pages/NewsPage';
import { SourcesPage } from './pages/SourcesPage';
import { TeamsPage } from './pages/TeamsPage';
import { UsagePage } from './pages/UsagePage';

function RequireAuth() {
  const { session } = useAuth();
  const location = useLocation();
  if (!session) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }
  return <Layout />;
}

const NAV: { to: string; label: string; icon: string; end?: boolean }[] = [
  { to: '/', label: 'Dashboard', icon: '📊', end: true },
  { to: '/overview', label: 'Tổng quan', icon: '🏠' },
  { to: '/news', label: 'Tin tức', icon: '📰' },
  { to: '/teams', label: 'Đội bóng', icon: '⚽' },
  { to: '/sources', label: 'Nguồn tin', icon: '🗞️' },
  { to: '/gemini', label: 'Gemini', icon: '✨' },
  { to: '/database', label: 'Cơ sở dữ liệu', icon: '🗄️' },
  { to: '/logs', label: 'Nhật ký', icon: '📜' },
];

const SIDEBAR_KEY = 'playerinfo.sidebar';

/** Narrow screens: the sidebar is a drawer over the page instead of a column. */
function useIsNarrow() {
  const query = '(max-width: 900px)';
  const [narrow, setNarrow] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const onChange = () => setNarrow(media.matches);
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, []);
  return narrow;
}

function Layout() {
  const { session, logout } = useAuth();
  const location = useLocation();
  const narrow = useIsNarrow();
  // Wide screens: expanded (labels) or collapsed (icons), remembered. Narrow screens: closed until opened.
  const [expanded, setExpanded] = useState(() => localStorage.getItem(SIDEBAR_KEY) !== 'collapsed');
  const [drawerOpen, setDrawerOpen] = useState(false);

  useEffect(() => {
    setDrawerOpen(false); // close the drawer after navigating
  }, [location.pathname]);

  function toggle() {
    if (narrow) {
      setDrawerOpen((open) => !open);
      return;
    }

    setExpanded((value) => {
      localStorage.setItem(SIDEBAR_KEY, value ? 'collapsed' : 'expanded');
      return !value;
    });
  }

  const showLabels = narrow || expanded;
  const sidebarClass = narrow ? `sidebar sidebar-drawer ${drawerOpen ? 'open' : ''}` : `sidebar ${expanded ? '' : 'sidebar-collapsed'}`;

  return (
    <div className="app">
      <header className="topbar">
        <button
          className="hamburger"
          onClick={toggle}
          aria-label={narrow ? (drawerOpen ? 'Đóng menu' : 'Mở menu') : expanded ? 'Thu gọn menu' : 'Mở rộng menu'}
          aria-expanded={narrow ? drawerOpen : expanded}
        >
          <span />
          <span />
          <span />
        </button>
        <div className="brand">⚽ PlayerInfo Admin</div>
        <div className="user">
          <span>{session?.username}</span>
          <button className="btn btn-ghost" onClick={logout}>
            Đăng xuất
          </button>
        </div>
      </header>

      <div className="shell">
        <aside className={sidebarClass}>
          <nav>
            {NAV.map((item) => (
              <NavLink key={item.to} to={item.to} end={item.end} title={showLabels ? undefined : item.label}>
                <span className="nav-icon" aria-hidden="true">
                  {item.icon}
                </span>
                {showLabels && <span className="nav-label">{item.label}</span>}
              </NavLink>
            ))}
          </nav>
        </aside>
        {narrow && drawerOpen && <div className="sidebar-backdrop" onClick={() => setDrawerOpen(false)} />}

        <main className="content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<RequireAuth />}>
        <Route path="/" element={<UsagePage />} />
        <Route path="/overview" element={<DashboardPage />} />
        <Route path="/news" element={<NewsArticlesPage />} />
        <Route path="/briefing/:teamId" element={<BriefingPage />} />
        <Route path="/sources" element={<SourcesPage />} />
        <Route path="/usage" element={<Navigate to="/" replace />} />
        <Route path="/gemini" element={<GeminiPage />} />
        <Route path="/database" element={<DatabasePage />} />
        <Route path="/teams" element={<TeamsPage />} />
        <Route path="/logs" element={<LogsPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
