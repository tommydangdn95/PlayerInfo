import { Navigate, NavLink, Outlet, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth';
import { DashboardPage } from './pages/DashboardPage';
import { LoginPage } from './pages/LoginPage';
import { LogsPage } from './pages/LogsPage';
import { TeamsPage } from './pages/TeamsPage';

function RequireAuth() {
  const { session } = useAuth();
  const location = useLocation();
  if (!session) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }
  return <Layout />;
}

function Layout() {
  const { session, logout } = useAuth();
  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">⚽ PlayerInfo Admin</div>
        <nav>
          <NavLink to="/" end>
            Tổng quan
          </NavLink>
          <NavLink to="/teams">Đội bóng</NavLink>
          <NavLink to="/logs">Nhật ký</NavLink>
        </nav>
        <div className="user">
          <span>{session?.username}</span>
          <button className="btn btn-ghost" onClick={logout}>
            Đăng xuất
          </button>
        </div>
      </header>
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<RequireAuth />}>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/teams" element={<TeamsPage />} />
        <Route path="/logs" element={<LogsPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
