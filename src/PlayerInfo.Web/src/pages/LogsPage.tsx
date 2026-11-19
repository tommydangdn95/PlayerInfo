import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { ApiError, errorMessage, readEventStream } from '../api';
import { HttpTraceDetails, type HttpTrace } from './HttpTraceDetails';

interface LogEntry {
  id: number;
  timestampUtc: string;
  level: 'Info' | 'Warning' | 'Error' | 'Critical' | string;
  category: string;
  message: string;
  exception: string | null;
  jobId: string | null;
  jobMode: string | null;
  http: HttpTrace | null;
}

type Status = 'connecting' | 'live' | 'reconnecting' | 'error';

/** Entries kept in the browser (the server keeps its own last 1000). */
const MAX_ENTRIES = 2000;

const timeFormat = new Intl.DateTimeFormat('vi-VN', {
  timeZone: 'Asia/Ho_Chi_Minh',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  day: '2-digit',
  month: '2-digit',
});

export function LogsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [entries, setEntries] = useState<LogEntry[]>([]);
  const [status, setStatus] = useState<Status>('connecting');
  const [error, setError] = useState<string | null>(null);
  const [paused, setPaused] = useState(false);
  const [pendingCount, setPendingCount] = useState(0);
  const [autoScroll, setAutoScroll] = useState(true);
  const [kind, setKind] = useState<'all' | 'http' | 'app' | 'warn'>('all');
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set());
  const jobFilter = searchParams.get('job') ?? '';

  const lastId = useRef(0);
  const pausedRef = useRef(false);
  const pending = useRef<LogEntry[]>([]);
  const bottom = useRef<HTMLDivElement>(null);

  pausedRef.current = paused;

  // Connect, and reconnect after drops (Render restarts, network), resuming after the last id seen.
  useEffect(() => {
    const controller = new AbortController();
    let retryDelay = 1000;

    const append = (batch: LogEntry[]) =>
      setEntries((prev) => {
        const next = prev.concat(batch);
        return next.length > MAX_ENTRIES ? next.slice(next.length - MAX_ENTRIES) : next;
      });

    async function run() {
      while (!controller.signal.aborted) {
        try {
          let connected = false;
          await readEventStream(
            `/api/logs/stream?after=${lastId.current}`,
            (_id, data) => {
              if (!connected) {
                connected = true;
                retryDelay = 1000;
                setStatus('live');
                setError(null);
              }
              const entry = JSON.parse(data) as LogEntry;
              if (entry.id <= lastId.current) return;
              lastId.current = entry.id;
              if (pausedRef.current) {
                pending.current.push(entry);
                setPendingCount(pending.current.length);
              } else {
                append([entry]);
              }
            },
            controller.signal,
          );
          setStatus('live'); // a stream with no backlog yet is still connected
        } catch (err) {
          if (controller.signal.aborted) return;
          if (err instanceof ApiError && err.status === 401) return; // logged out
          setError(errorMessage(err));
        }
        if (controller.signal.aborted) return;
        setStatus('reconnecting');
        await new Promise((r) => setTimeout(r, retryDelay));
        retryDelay = Math.min(retryDelay * 2, 15000);
      }
    }

    void run();
    return () => controller.abort();
  }, []);

  // Release entries collected while paused.
  useEffect(() => {
    if (paused || pending.current.length === 0) return;
    const batch = pending.current;
    pending.current = [];
    setPendingCount(0);
    setEntries((prev) => {
      const next = prev.concat(batch);
      return next.length > MAX_ENTRIES ? next.slice(next.length - MAX_ENTRIES) : next;
    });
  }, [paused]);

  const jobs = useMemo(() => {
    const map = new Map<string, { id: string; mode: string; running: boolean; failed: boolean }>();
    for (const e of entries) {
      if (!e.jobId) continue;
      const job = map.get(e.jobId) ?? { id: e.jobId, mode: e.jobMode ?? '?', running: false, failed: false };
      if (e.message.startsWith('Starting CollectorPipeline')) job.running = true;
      if (e.message.startsWith('Finished CollectorPipeline')) job.running = false;
      if (e.level === 'Error' || e.level === 'Critical' || e.message.includes('failed steps')) job.failed = true;
      map.set(e.jobId, job);
    }
    return [...map.values()].reverse();
  }, [entries]);

  const running = jobs.filter((j) => j.running);

  const visible = useMemo(
    () =>
      entries.filter(
        (e) =>
          (kind === 'all' ||
            (kind === 'http' && e.http != null) ||
            (kind === 'app' && e.http == null) ||
            (kind === 'warn' && e.level !== 'Info')) &&
          (!jobFilter || e.jobId === jobFilter || (e.jobId === null && e.message.includes(jobFilter))),
      ),
    [entries, kind, jobFilter],
  );

  useEffect(() => {
    if (autoScroll && !paused) bottom.current?.scrollIntoView({ block: 'end' });
  }, [visible, autoScroll, paused]);

  function toggle(id: number) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function setJobFilter(job: string) {
    const next = new URLSearchParams(searchParams);
    if (job) next.set('job', job);
    else next.delete('job');
    setSearchParams(next, { replace: true });
  }

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Nhật ký</h2>
        <span className={`status status-${status}`}>
          {status === 'live' ? '● Trực tiếp' : status === 'connecting' ? 'Đang kết nối…' : 'Mất kết nối, đang thử lại…'}
        </span>
      </div>

      <p className="muted small">
        Log của app theo thời gian thực, chỉ giữ trong bộ nhớ server (khoảng 1000 dòng gần nhất), mất khi app khởi động lại.
      </p>

      {running.length > 0 && (
        <div className="alert alert-info">
          Đang chạy:{' '}
          {running.map((j) => (
            <button key={j.id} className="link-button" onClick={() => setJobFilter(j.id)}>
              {j.mode} ({j.id})
            </button>
          ))}
        </div>
      )}
      {error && status !== 'live' && <div className="alert alert-error">{error}</div>}

      <section className="card">
        <div className="row log-toolbar">
          <select value={kind} onChange={(e) => setKind(e.target.value as 'all' | 'http' | 'app' | 'warn')}>
            <option value="all">Tất cả</option>
            <option value="http">Chỉ gọi API (request/response)</option>
            <option value="app">Chỉ log của app</option>
            <option value="warn">Chỉ cảnh báo &amp; lỗi</option>
          </select>
          <select value={jobFilter} onChange={(e) => setJobFilter(e.target.value)}>
            <option value="">Mọi job</option>
            {jobs.map((j) => (
              <option key={j.id} value={j.id}>
                {j.mode} · {j.id}
                {j.running ? ' (đang chạy)' : j.failed ? ' (có lỗi)' : ''}
              </option>
            ))}
          </select>
          <label className="checkbox small">
            <input type="checkbox" checked={autoScroll} onChange={(e) => setAutoScroll(e.target.checked)} />
            Tự cuộn
          </label>
          <button className="btn" onClick={() => setPaused((p) => !p)}>
            {paused ? `Tiếp tục${pendingCount ? ` (+${pendingCount})` : ''}` : 'Tạm dừng'}
          </button>
          <button className="btn btn-ghost" onClick={() => setEntries([])}>
            Xóa màn hình
          </button>
          <span className="muted small grow log-count">
            {visible.length} / {entries.length} dòng
          </span>
        </div>

        <div className="log-view">
          <table className="log-table">
            <thead>
              <tr>
                <th>Giờ</th>
                <th>Mức</th>
                <th>Job</th>
                <th>Nguồn</th>
                <th>Nội dung</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((e) => {
                const isOpen = expanded.has(e.id);
                return (
                  <Fragment key={e.id}>
                    <tr
                      className={`log-${e.level.toLowerCase()}${e.http ? ' log-http' : ''}${isOpen ? ' log-open' : ''}`}
                      onClick={e.http ? () => toggle(e.id) : undefined}
                      title={e.http ? 'Bấm để xem request/response' : undefined}
                    >
                      <td className="nowrap">{timeFormat.format(new Date(e.timestampUtc))}</td>
                      <td>
                        <span className={`log-level log-level-${e.level.toLowerCase()}`}>{e.level}</span>
                      </td>
                      <td className="nowrap">
                        {e.jobId ? (
                          <button
                            className="link-button"
                            onClick={(ev) => {
                              ev.stopPropagation();
                              setJobFilter(e.jobId!);
                            }}
                            title="Lọc theo job này"
                          >
                            {e.jobMode} · {e.jobId}
                          </button>
                        ) : (
                          <span className="muted">—</span>
                        )}
                      </td>
                      <td className="nowrap muted">{e.category}</td>
                      <td className="log-message">
                        {e.http && <span className="log-caret">{isOpen ? '▾' : '▸'} </span>}
                        {e.message}
                        {e.exception && <div className="log-exception">{e.exception}</div>}
                      </td>
                    </tr>
                    {isOpen && e.http && (
                      <tr className="log-detail-row">
                        <td colSpan={5}>
                          <HttpTraceDetails trace={e.http} />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
          {visible.length === 0 && (
            <p className="muted log-empty">
              {status === 'live' ? 'Chưa có log. Chạy một job ở trang Tổng quan để xem log xuất hiện ở đây.' : 'Đang tải…'}
            </p>
          )}
          <div ref={bottom} />
        </div>
      </section>
    </div>
  );
}
