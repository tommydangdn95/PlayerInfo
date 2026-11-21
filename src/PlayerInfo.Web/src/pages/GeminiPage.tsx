import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { formatDateTime } from '../types';
import { Bars, fmt, LimitBar, Outcome } from './Bars';

interface ModelUsage {
  model: string;
  /** Place in the fallback chain; null = not used by the app. */
  order: number | null;
  requestsPerDay: number;
  requestsPerMinute: number;
  tokensPerMinute: number;
  usedToday: number;
  peakRequestsPerMinute: number;
  peakTokensPerMinute: number;
  successToday: number;
  rateLimitedToday: number;
  overloadedToday: number;
  tokensToday: number;
  avgDurationMs: number;
  lastOutcome: string | null;
  lastAtUtc: string | null;
}

interface GeminiUsage {
  configured: boolean;
  batchSize: number;
  quotaDay: string;
  resetAtUtc: string;
  today: { calls: number; success: number; failed: number; rateLimited: number; totalTokens: number; promptTokens: number; outputTokens: number };
  models: ModelUsage[];
  daily: { day: string; requests: number; success: number; failed: number; promptTokens: number; outputTokens: number; totalTokens: number }[];
  recent: {
    atUtc: string;
    model: string | null;
    statusCode: number | null;
    outcome: string;
    durationMs: number;
    promptTokens: number;
    outputTokens: number;
    totalTokens: number;
    articleCount: number;
    jobId: string | null;
  }[];
}

const shortNum = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(n >= 10000 ? 0 : 1)}K` : String(n));

/** Gemini usage against each model's free-tier limits (RPM, TPM, RPD), like Google AI Studio. */
export function GeminiPage() {
  const [usage, setUsage] = useState<GeminiUsage | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setUsage(await api<GeminiUsage>('/api/gemini/usage?days=14'));
    } catch (err) {
      setError(errorMessage(err));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (error) return <div className="alert alert-error">{error}</div>;
  if (!usage) return <p className="muted">Đang tải…</p>;

  const t = usage.today;
  const chain = usage.models.filter((m) => m.order !== null);
  const others = usage.models.filter((m) => m.order === null);
  const capacity = chain.every((m) => m.requestsPerDay > 0) ? chain.reduce((s, m) => s + m.requestsPerDay, 0) : 0;
  const left = chain.reduce((s, m) => s + Math.max(0, m.requestsPerDay - m.usedToday), 0);
  const labels = usage.daily.map((d) => `${d.day.slice(8, 10)}/${d.day.slice(5, 7)}`);

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Gemini</h2>
        <div className="row">
          <Link to="/">← Dashboard</Link>
          <button className="btn btn-ghost" onClick={() => void load()}>
            Làm mới
          </button>
        </div>
      </div>

      {!usage.configured && <div className="alert alert-warn">Chưa cấu hình key Gemini (ApiKeys__Gemini): tin không được AI xử lý.</div>}

      <section className="stat-grid">
        <div className="card stat">
          <div className="muted small">Lệnh còn lại hôm nay</div>
          <div className="stat-value">
            {fmt(left)}
            {capacity > 0 && <span className="stat-sub"> / {fmt(capacity)}</span>}
          </div>
          <LimitBar used={capacity - left} limit={capacity} />
          <div className="muted small">Làm mới lúc {formatDateTime(usage.resetAtUtc)} (nửa đêm giờ Mỹ)</div>
        </div>
        <div className="card stat">
          <div className="muted small">Lệnh hôm nay</div>
          <div className="stat-value">
            {fmt(t.calls)} <span className="stat-sub">· {fmt(t.failed)} lỗi</span>
          </div>
          <div className="muted small">429 hết lượt: {t.rateLimited}</div>
        </div>
        <div className="card stat">
          <div className="muted small">Token hôm nay</div>
          <div className="stat-value">{fmt(t.totalTokens)}</div>
          <div className="muted small">
            Vào {fmt(t.promptTokens)} · ra {fmt(t.outputTokens)}
          </div>
        </div>
        <div className="card stat">
          <div className="muted small">Mỗi lệnh</div>
          <div className="stat-value">
            {usage.batchSize} <span className="stat-sub">bài</span>
          </div>
          <div className="muted small">~{t.success > 0 ? fmt(Math.round(t.totalTokens / t.success)) : '—'} token / lệnh thành công</div>
        </div>
      </section>

      <section className="card">
        <h3>Hạn mức theo model (hôm nay)</h3>
        <p className="muted small">
          App thử lần lượt từ trên xuống: model hết lượt ngày (RPD), đủ lượt phút (RPM), quá tải hoặc trả 429 thì chuyển sang model sau.
          Lệnh lỗi cũng bị tính, giống cách Google đếm. RPM và TPM ở đây là phút bận nhất trong ngày.
        </p>
        <div className="table-scroll">
          <table className="table model-table">
            <thead>
              <tr>
                <th>#</th>
                <th>Model</th>
                <th>RPM (lệnh/phút)</th>
                <th>TPM (token/phút)</th>
                <th>RPD (lệnh/ngày)</th>
                <th className="num">OK</th>
                <th className="num">429</th>
                <th className="num">503</th>
                <th className="num">Token</th>
                <th>Gần nhất</th>
              </tr>
            </thead>
            <tbody>
              {chain.map((m) => (
                <ModelRow key={m.model} m={m} />
              ))}
              {others.length > 0 && (
                <tr>
                  <td colSpan={10} className="muted small">
                    Không nằm trong chuỗi gọi (có hạn mức nhưng app không dùng):
                  </td>
                </tr>
              )}
              {others.map((m) => (
                <ModelRow key={m.model} m={m} />
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted small">
          Đổi trên Render: <code>Collector__GeminiModel</code>, <code>Collector__GeminiFallbackModels__0</code>…,{' '}
          <code>Collector__GeminiModelLimits__N__Model</code> / <code>__RequestsPerDay</code> / <code>__RequestsPerMinute</code> /{' '}
          <code>__TokensPerMinute</code>.
        </p>
      </section>

      <section className="grid-2">
        <div className="card">
          <h3>Lệnh mỗi ngày</h3>
          <Bars
            values={usage.daily.map((d) => d.requests)}
            labels={labels}
            detail={(i) => `${fmt(usage.daily[i].requests)} lệnh: ${fmt(usage.daily[i].success)} thành công, ${fmt(usage.daily[i].failed)} lỗi`}
            axis={[labels[0], `${labels[labels.length - 1]} (hôm nay)`]}
          />
        </div>
        <div className="card">
          <h3>Token mỗi ngày</h3>
          <Bars
            values={usage.daily.map((d) => d.totalTokens)}
            labels={labels}
            detail={(i) => `${fmt(usage.daily[i].totalTokens)} token (vào ${fmt(usage.daily[i].promptTokens)}, ra ${fmt(usage.daily[i].outputTokens)})`}
            axis={[labels[0], `${labels[labels.length - 1]} (hôm nay)`]}
          />
        </div>
      </section>

      <section className="card">
        <h3>50 lệnh gần nhất</h3>
        {usage.recent.length === 0 ? (
          <p className="muted small">Chưa có lệnh nào trong 14 ngày.</p>
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead>
                <tr>
                  <th>Thời gian</th>
                  <th>Model</th>
                  <th>Kết quả</th>
                  <th className="num">Bài</th>
                  <th className="num">Token vào</th>
                  <th className="num">Token ra</th>
                  <th className="num">Thời gian (s)</th>
                  <th>Job</th>
                </tr>
              </thead>
              <tbody>
                {usage.recent.map((c, i) => (
                  <tr key={i}>
                    <td className="nowrap">{formatDateTime(c.atUtc)}</td>
                    <td>
                      <code>{c.model ?? '?'}</code>
                    </td>
                    <td>
                      <Outcome outcome={c.outcome} status={c.statusCode} />
                    </td>
                    <td className="num">{c.articleCount}</td>
                    <td className="num">{fmt(c.promptTokens)}</td>
                    <td className="num">{fmt(c.outputTokens)}</td>
                    <td className="num">{(c.durationMs / 1000).toFixed(1)}</td>
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

function ModelRow({ m }: { m: ModelUsage }) {
  return (
    <tr className={m.order === null ? 'model-unused' : ''}>
      <td className="muted">{m.order ?? '—'}</td>
      <td>
        <code>{m.model}</code>
        {m.order === 1 && <div className="small muted">model chính</div>}
      </td>
      <LimitCell used={m.peakRequestsPerMinute} limit={m.requestsPerMinute} />
      <LimitCell used={m.peakTokensPerMinute} limit={m.tokensPerMinute} short />
      <LimitCell used={m.usedToday} limit={m.requestsPerDay} />
      <td className="num">{fmt(m.successToday)}</td>
      <td className={`num ${m.rateLimitedToday > 0 ? 'outcome-bad' : ''}`}>{fmt(m.rateLimitedToday)}</td>
      <td className={`num ${m.overloadedToday > 0 ? 'outcome-warn' : ''}`}>{fmt(m.overloadedToday)}</td>
      <td className="num">{fmt(m.tokensToday)}</td>
      <td className="small">{m.lastOutcome ? <Outcome outcome={m.lastOutcome} /> : <span className="muted">—</span>}</td>
    </tr>
  );
}

function LimitCell({ used, limit, short }: { used: number; limit: number; short?: boolean }) {
  const show = short ? shortNum : fmt;
  return (
    <td className="limit-cell">
      <LimitBar used={used} limit={limit} />
      <span className="small">
        {show(used)} / {limit > 0 ? show(limit) : '∞'}
      </span>
    </td>
  );
}
