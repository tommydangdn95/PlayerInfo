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

/**
 * Reads a Server-Sent Events stream with fetch() (EventSource cannot send the Authorization header).
 * Calls `onEvent` for every `data:` event with its `id`; resolves when the server closes the stream
 * and rejects on network errors or when `signal` aborts.
 */
export async function readEventStream(
  path: string,
  onEvent: (id: string | null, data: string) => void,
  signal: AbortSignal,
): Promise<void> {
  const session = loadSession();
  const res = await fetch(path, {
    headers: {
      Accept: 'text/event-stream',
      ...(session ? { Authorization: `Bearer ${session.accessToken}` } : {}),
    },
    signal,
  });

  if (res.status === 401 && session) {
    clearSession();
    window.dispatchEvent(new Event(LOGOUT_EVENT));
    throw new ApiError('Phiên đăng nhập đã hết hạn, vui lòng đăng nhập lại.', 401);
  }
  if (!res.ok || !res.body) {
    throw new ApiError(await readError(res), res.status);
  }

  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) return;
    buffer += value.replace(/\r\n/g, '\n');

    let sep: number;
    while ((sep = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, sep);
      buffer = buffer.slice(sep + 2);

      let id: string | null = null;
      const data: string[] = [];
      for (const line of block.split('\n')) {
        if (line.startsWith('id:')) id = line.slice(3).trim();
        else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
      }
      if (data.length > 0) onEvent(id, data.join('\n'));
    }
  }
}
