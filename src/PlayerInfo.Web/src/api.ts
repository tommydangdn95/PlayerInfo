// Thin fetch wrapper: attaches the admin JWT and turns a 401 into a logout.

const SESSION_KEY = 'playerinfo.session';
export const LOGOUT_EVENT = 'playerinfo:logout';

export interface Session {
  accessToken: string;
  expiresAtUtc: string;
  username: string;
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
  }
}

export function loadSession(): Session | null {
  try {
    const raw = localStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    const session = JSON.parse(raw) as Session;
    if (new Date(session.expiresAtUtc).getTime() <= Date.now()) {
      localStorage.removeItem(SESSION_KEY);
      return null;
    }
    return session;
  } catch {
    return null;
  }
}

export function saveSession(session: Session): void {
  localStorage.setItem(SESSION_KEY, JSON.stringify(session));
}

export function clearSession(): void {
  localStorage.removeItem(SESSION_KEY);
}

async function readError(res: Response): Promise<string> {
  try {
    const body = await res.json();
    if (typeof body?.error === 'string') return body.error;
    if (typeof body?.title === 'string') return body.title;
  } catch {
    // not JSON
  }
  if (res.status === 429) return 'Thử quá nhiều lần, vui lòng đợi 1 phút.';
  if (res.status === 403) return 'Không có quyền thực hiện thao tác này.';
  return `Lỗi ${res.status}`;
}

export async function api<T>(path: string, options: { method?: string; body?: unknown } = {}): Promise<T> {
  const session = loadSession();
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (session) headers.Authorization = `Bearer ${session.accessToken}`;

  const res = await fetch(path, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });

  if (res.status === 401 && session) {
    clearSession();
    window.dispatchEvent(new Event(LOGOUT_EVENT));
    throw new ApiError('Phiên đăng nhập đã hết hạn, vui lòng đăng nhập lại.', 401);
  }

  if (!res.ok) {
    throw new ApiError(await readError(res), res.status);
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : 'Đã có lỗi xảy ra.';
}
