import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { addDays, dayHeading, formatTime, vnDayKey } from '../types';
import { Bars, fmt, formatBytes, LimitBar, Outcome } from './Bars';

interface Totals {
  calls: number;
  success: number;
  failed: number;
  rateLimited: number;
  timeouts: number;
  avgDurationMs: number;
  maxDurationMs: number;
  responseBytes: number;
  totalTokens: number;
  promptTokens: number;
  outputTokens: number;
}

interface Usage {
  day: string;
  today: Totals;
  callsByHour: number[];
  providers: {
    provider: string;
    totals: Totals;
    callsByHour: number[];
    failedByHour: number[];
    lastAtUtc: string | null;
    lastOutcome: string | null;
    quotaUsed: number | null;
    quotaLimit: number | null;
  }[];
  history: { provider: string; days: { day: string; calls: number; failed: number; totalTokens: number }[] }[];
  recent: {
    atUtc: string;
    provider: string;
    model: string | null;
    endpoint: string;
    statusCode: number | null;
    outcome: string;
    durationMs: number;
    responseBytes: number | null;
    totalTokens: number;
    jobId: string | null;
  }[];
}

const PROVIDER_NOTES: Record<string, string> = {
  ESPN: 'Đội, lịch đấu, đội hình, chấn thương, tin. Miễn phí, không giới hạn.',
  RSS: 'Google News và các báo (BBC, ESPN FC, VnExpress…). Miễn phí.',
  'API-Football': 'Chỉ dùng cho chuyển nhượng. Giới hạn theo ngày UTC.',
  NewsData: 'Tìm tin bổ sung (nếu có key). Giới hạn theo ngày UTC.',
  Gemini: 'AI gắn tag, tóm tắt, gộp câu chuyện. Giới hạn từng model: xem trang Gemini.',
  Telegram: 'Gửi bản tin.',
};

const HOURS = Array.from({ length: 24 }, (_, h) => `${String(h).padStart(2, '0')}:00`);
const sec = (ms: number) => (ms / 1000).toFixed(ms < 10000 ? 1 : 0);

/** Dashboard (home page): every external API the app calls: per provider today, by hour, over 14 days, and the latest calls. */
export function UsagePage() {
  const today = vnDayKey(new Date());
  const [day, setDay] = useState(today);
  const [usage, setUsage] = useState<Usage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [providerFilter, setProviderFilter] = useState('');

  const load = useCallback(async () => {
    setError(null);
    try {
      setUsage(await api<Usage>(`/api/usage?day=${day}&days=14`));
    } catch (err) {
      setError(errorMessage(err));
    }
  }, [day]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error) return <div className="alert alert-error">{error}</div>;
  if (!usage) return <p className="muted">Đang tải…</p>;

  const t = usage.today;
  const currentHour = day === today ? new Date().getHours() : 23;
  const recent = usage.recent.filter((c) => !providerFilter || c.provider === providerFilter);

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Dashboard · {dayHeading(day)}</h2>
        <div className="row">
          <button className="btn" onClick={() => setDay(addDays(day, -1))} title="Ngày trước">
            ‹
          </button>
          <input type="date" value={day} max={today} onChange={(e) => e.target.value && setDay(e.target.value)} />
          <button className="btn" onClick={() => setDay(addDays(day, 1))} disabled={day >= today} title="Ngày sau">
            ›
          </button>
          <button className="btn btn-ghost" onClick={() => void load()}>
            Làm mới
          </button>
        </div>
      </div>

      <section className="stat-grid">
        <div className="card stat">
          <div className="muted small">Tổng lệnh gọi</div>
          <div className="stat-value">{fmt(t.calls)}</div>
          <div className="muted small">{usage.providers.filter((p) => p.totals.calls > 0).length} API được gọi</div>
        </div>
        <div className="card stat">
          <div className="muted small">Lỗi</div>
          <div className="stat-value">
            {fmt(t.failed)}
            <span className="stat-sub"> · {t.calls > 0 ? Math.round((t.failed / t.calls) * 100) : 0}%</span>
          </div>
          <div className="muted small">
            429 hết lượt: {t.rateLimited} · quá thời gian: {t.timeouts}
          </div>
        </div>
        <div className="card stat">
          <div className="muted small">Thời gian phản hồi</div>
          <div className="stat-value">
            {sec(t.avgDurationMs)}s <span className="stat-sub">TB</span>
          </div>
          <div className="muted small">Lâu nhất {sec(t.maxDurationMs)}s</div>
        </div>
        <div className="card stat">
          <div className="muted small">Token Gemini</div>
          <div className="stat-value">{fmt(t.totalTokens)}</div>
          <div className="muted small">
            Vào {fmt(t.promptTokens)} · ra {fmt(t.outputTokens)} · <Link to="/gemini">chi tiết →</Link>
          </div>
        </div>
        <div className="card stat">
          <div className="muted small">Dữ liệu tải về</div>
          <div className="stat-value">{formatBytes(t.responseBytes)}</div>
          <div className="muted small">Tổng dung lượng phản hồi</div>
        </div>
      </section>

      <section className="card">
        <h3>Lệnh gọi theo giờ (giờ Việt Nam)</h3>
        <Bars
          values={usage.callsByHour}
          labels={HOURS}
          defaultIndex={currentHour}
          detail={(h) =>
            `${fmt(usage.callsByHour[h])} lệnh` +
            usage.providers
              .filter((p) => p.callsByHour[h] > 0)
              .map((p) => ` · ${p.provider} ${p.callsByHour[h]}`)
              .join('')
          }
          axis={['00:00', '23:00']}
        />
      </section>

      <section className="provider-grid">
        {usage.providers.map((p) => {
          const history = usage.history.find((h) => h.provider === p.provider)?.days ?? [];
          return (
            <article key={p.provider} className="card provider-card">
              <div className="provider-head">
                <h3>{p.provider}</h3>
                {p.lastOutcome && (
                  <span className="small muted">
                    gần nhất {p.lastAtUtc && formatTime(p.lastAtUtc)} · <Outcome outcome={p.lastOutcome} />
                  </span>
                )}
              </div>
              <p className="muted small provider-note">{PROVIDER_NOTES[p.provider] ?? ''}</p>

              <div className="provider-stats">
                <div>
                  <div className="provider-num">{fmt(p.totals.calls)}</div>
                  <div className="muted small">lệnh</div>
                </div>
                <div>
                  <div className={`provider-num ${p.totals.failed > 0 ? 'outcome-bad' : ''}`}>{fmt(p.totals.failed)}</div>
                  <div className="muted small">lỗi</div>
                </div>
                <div>
                  <div className="provider-num">{sec(p.totals.avgDurationMs)}s</div>
                  <div className="muted small">TB / lâu nhất {sec(p.totals.maxDurationMs)}s</div>
                </div>
                <div>
                  <div className="provider-num">{p.provider === 'Gemini' ? fmt(p.totals.totalTokens) : formatBytes(p.totals.responseBytes)}</div>
                  <div className="muted small">{p.provider === 'Gemini' ? 'token' : 'tải về'}</div>
                </div>
              </div>

              {p.quotaLimit !== null && p.quotaUsed !== null && (
                <div className="provider-quota">
                  <div className="quota-label small">
                    <span>Hạn mức hôm nay (UTC)</span>
                    <span>
                      {fmt(p.quotaUsed)} / {fmt(p.quotaLimit)}
                    </span>
                  </div>
                  <LimitBar used={p.quotaUsed} limit={p.quotaLimit} />
                </div>
              )}

              <div className="muted small">Theo giờ</div>
              <Bars
                values={p.callsByHour}
                labels={HOURS}
                height={56}
                defaultIndex={currentHour}
                detail={(h) => `${p.callsByHour[h]} lệnh${p.failedByHour[h] ? `, ${p.failedByHour[h]} lỗi` : ''}`}
              />

              {history.length > 0 && (
                <>
                  <div className="muted small">14 ngày</div>
                  <Bars
                    values={history.map((d) => d.calls)}
                    labels={history.map((d) => `${d.day.slice(8, 10)}/${d.day.slice(5, 7)}`)}
                    height={56}
                    detail={(i) =>
                      `${fmt(history[i].calls)} lệnh, ${fmt(history[i].failed)} lỗi` +
                      (p.provider === 'Gemini' ? `, ${fmt(history[i].totalTokens)} token` : '')
                    }
                  />
                </>
              )}
            </article>
          );
        })}
      </section>

      <section className="card">
        <div className="page-head">
          <h3>Lệnh gọi gần nhất trong ngày</h3>
          <select value={providerFilter} onChange={(e) => setProviderFilter(e.target.value)}>
            <option value="">Tất cả API</option>
            {usage.providers.map((p) => (
              <option key={p.provider} value={p.provider}>
                {p.provider}
              </option>
            ))}
          </select>
        </div>
        {recent.length === 0 ? (
          <p className="muted small">Không có lệnh gọi.</p>
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead>
                <tr>
                  <th>Giờ</th>
                  <th>API</th>
                  <th>Địa chỉ</th>
                  <th>Kết quả</th>
                  <th className="num">Thời gian (s)</th>
                  <th className="num">Dung lượng</th>
                  <th className="num">Token</th>
                  <th>Job</th>
                </tr>
              </thead>
              <tbody>
                {recent.map((c, i) => (
                  <tr key={i}>
                    <td className="nowrap">{formatTime(c.atUtc)}</td>
                    <td className="nowrap">
                      {c.provider}
                      {c.model && <div className="muted small">{c.model}</div>}
                    </td>
                    <td className="endpoint" title={c.endpoint}>
                      {c.endpoint.replace(/^https?:\/\//, '')}
                    </td>
                    <td>
                      <Outcome outcome={c.outcome} status={c.statusCode} />
                    </td>
                    <td className="num">{sec(c.durationMs)}</td>
                    <td className="num">{c.responseBytes !== null ? formatBytes(c.responseBytes) : '—'}</td>
                    <td className="num">{c.totalTokens > 0 ? fmt(c.totalTokens) : '—'}</td>
                    <td>{c.jobId ? <Link to={`/logs?job=${c.jobId}`}>{c.jobId}</Link> : <span className="muted">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
