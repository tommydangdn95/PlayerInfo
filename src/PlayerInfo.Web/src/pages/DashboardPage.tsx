import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { type JobMode, type Quota } from '../types';
import { TeamNewsBoard } from './TeamNewsBoard';

const JOBS: { mode: JobMode; label: string; hint: string }[] = [
  { mode: 'news', label: 'Tin tức', hint: 'Tin ESPN, Google News, RSS + Gemini + Telegram. Không tốn quota API-Football.' },
  { mode: 'transfers_news', label: 'Chuyển nhượng + tin', hint: 'Tốn 1 request API-Football cho mỗi đội đã liên kết API-Football.' },
  { mode: 'full', label: 'Đầy đủ', hint: 'Chấn thương, lịch đấu (ESPN), chuyển nhượng (API-Football), tin tức.' },
  { mode: 'roster', label: 'Đội hình', hint: 'Cập nhật cầu thủ từ ESPN. Không tốn quota.' },
];

export function DashboardPage() {
  const [quota, setQuota] = useState<Quota | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<JobMode | null>(null);
  const [jobMessage, setJobMessage] = useState<string | null>(null);
  const [lastJobId, setLastJobId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setQuota(await api<Quota>('/api/jobs/quota'));
    } catch (err) {
      setError(errorMessage(err));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function runJob(mode: JobMode) {
    setConfirming(null);
    setJobMessage(null);
    try {
      const result = await api<{ jobId: string }>(`/api/jobs/run?mode=${mode}`, { method: 'POST' });
      setLastJobId(result.jobId);
      setJobMessage(`Đã đưa job "${mode}" (${result.jobId}) vào hàng đợi.`);
    } catch (err) {
      setLastJobId(null);
      setJobMessage(errorMessage(err));
    }
  }

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Tổng quan</h2>
        <button
          className="btn btn-ghost"
          onClick={() => {
            void load();
            setRefreshKey((k) => k + 1);
          }}
        >
          Làm mới
        </button>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      <section className="grid-2">
        <div className="card">
          <h3>Quota hôm nay (UTC)</h3>
          {quota ? (
            <>
              <QuotaBar label="API-Football" used={quota.apiFootball} limit={quota.apiFootballLimit} />
              <QuotaBar label="NewsData.io" used={quota.newsData} limit={quota.newsDataLimit} />
            </>
          ) : (
            <p className="muted">Đang tải…</p>
          )}
        </div>

        <div className="card">
          <h3>Chạy job ngay</h3>
          <div className="job-list">
            {JOBS.map((job) => (
              <div key={job.mode} className="job-row">
                <div>
                  <strong>{job.label}</strong>
                  <div className="muted small">{job.hint}</div>
                </div>
                {confirming === job.mode ? (
                  <div className="row">
                    <button className="btn btn-primary" onClick={() => void runJob(job.mode)}>
                      Xác nhận
                    </button>
                    <button className="btn btn-ghost" onClick={() => setConfirming(null)}>
                      Hủy
                    </button>
                  </div>
                ) : (
                  <button className="btn" onClick={() => setConfirming(job.mode)}>
                    Chạy
                  </button>
                )}
              </div>
            ))}
          </div>
          {jobMessage && (
            <div className="alert alert-info">
              {jobMessage}{' '}
              {lastJobId && <Link to={`/logs?job=${lastJobId}`}>Xem nhật ký trực tiếp →</Link>}
            </div>
          )}
        </div>
      </section>

      <TeamNewsBoard refreshKey={refreshKey} />
    </div>
  );
}

function QuotaBar({ label, used, limit }: { label: string; used: number; limit: number }) {
  const pct = limit > 0 ? Math.min(100, Math.round((used / limit) * 100)) : 0;
  const level = pct >= 90 ? 'danger' : pct >= 70 ? 'warn' : 'ok';
  return (
    <div className="quota">
      <div className="quota-label">
        <span>{label}</span>
        <span>
          {used} / {limit}
        </span>
      </div>
      <div className="quota-track">
        <div className={`quota-fill quota-${level}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}
