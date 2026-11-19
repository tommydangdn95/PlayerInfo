import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, clearSession, loadSession, LOGOUT_EVENT, saveSession, type Session } from './api';
import { disableGoogleAutoSelect } from './google';

interface AuthContextValue {
  session: Session | null;
  login: (username: string, password: string) => Promise<void>;
  loginWithGoogle: (credential: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => loadSession());

  useEffect(() => {
    const onLogout = () => setSession(null);
    window.addEventListener(LOGOUT_EVENT, onLogout);
    return () => window.removeEventListener(LOGOUT_EVENT, onLogout);
  }, []);

  // Drop the session as soon as the token expires, even if the page stays open.
  useEffect(() => {
    if (!session) return;
    const ms = new Date(session.expiresAtUtc).getTime() - Date.now();
    const timer = window.setTimeout(() => {
      clearSession();
      setSession(null);
    }, Math.max(0, Math.min(ms, 2_147_000_000)));
    return () => window.clearTimeout(timer);
  }, [session]);

  const login = useCallback(async (username: string, password: string) => {
    const result = await api<Session>('/api/auth/login', { method: 'POST', body: { username, password } });
    saveSession(result);
    setSession(result);
  }, []);

  const loginWithGoogle = useCallback(async (credential: string) => {
    const result = await api<Session>('/api/auth/google', { method: 'POST', body: { credential } });
    saveSession(result);
    setSession(result);
  }, []);

  const logout = useCallback(() => {
    clearSession();
    disableGoogleAutoSelect();
    setSession(null);
  }, []);

  const value = useMemo(
    () => ({ session, login, loginWithGoogle, logout }),
    [session, login, loginWithGoogle, logout],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
