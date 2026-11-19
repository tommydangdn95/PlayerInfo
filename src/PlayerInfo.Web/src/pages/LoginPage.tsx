import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { useAuth } from '../auth';
import { renderGoogleButton } from '../google';

interface AuthConfig {
  passwordLogin: boolean;
  google: { clientId: string } | null;
}

export function LoginPage() {
  const { session, login, loginWithGoogle } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? '/';

  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [showPassword, setShowPassword] = useState(false);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const googleButton = useRef<HTMLDivElement>(null);

  // Keep the latest callbacks for the Google button, which is rendered once.
  const onGoogleCredential = useRef<(credential: string) => void>(() => {});
  onGoogleCredential.current = async (credential: string) => {
    setBusy(true);
    setError(null);
    try {
      await loginWithGoogle(credential);
      navigate(from, { replace: true });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    api<AuthConfig>('/api/auth/config')
      .then(setConfig)
      .catch((err) => setError(errorMessage(err)));
  }, []);

  useEffect(() => {
    if (!config?.google || !googleButton.current) return;
    renderGoogleButton(googleButton.current, config.google.clientId, (credential) =>
      onGoogleCredential.current(credential),
    ).catch((err) => setError(errorMessage(err)));
  }, [config]);

  if (session) return <Navigate to={from} replace />;

  async function onPassword(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(username.trim(), password);
      navigate(from, { replace: true });
    } catch (err) {
      setError(errorMessage(err));
      setPassword('');
    } finally {
      setBusy(false);
    }
  }

  // Password form is the only option when Google sign-in is not configured.
  const passwordVisible = config?.passwordLogin && (showPassword || !config.google);

  return (
    <div className="login-wrap">
      <div className="card login-card">
        <h1>⚽ PlayerInfo</h1>
        <p className="muted">Đăng nhập trang quản trị</p>

        {!config && !error && <p className="muted">Đang tải…</p>}

        {config?.google && (
          <div className="google-button-wrap">
            <div ref={googleButton} />
            {busy && !passwordVisible && <p className="muted small">Đang đăng nhập…</p>}
          </div>
        )}

        {config?.google && config.passwordLogin && !showPassword && (
          <button className="link-button" onClick={() => setShowPassword(true)}>
            Đăng nhập bằng mật khẩu (dự phòng)
          </button>
        )}

        {passwordVisible && (
          <form className="login-form" onSubmit={onPassword}>
            {config?.google && <div className="divider">hoặc</div>}
            <label>
              Tên đăng nhập
              <input
                autoFocus
                autoComplete="username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                required
              />
            </label>
            <label>
              Mật khẩu
              <input
                type="password"
                autoComplete="current-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
              />
            </label>
            <button className="btn btn-primary" type="submit" disabled={busy}>
              {busy ? 'Đang đăng nhập…' : 'Đăng nhập'}
            </button>
          </form>
        )}

        {config && !config.google && !config.passwordLogin && (
          <div className="alert alert-error">Chưa cấu hình cách đăng nhập nào trên server.</div>
        )}

        {error && <div className="alert alert-error">{error}</div>}
      </div>
    </div>
  );
}
