import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { applyRange } from '../newsFilters';
import { addDays, categoryLabel, dayHeading, formatTime, vnDayKey, type TeamNews } from '../types';

const PER_TEAM = 3;

/** Dashboard news: one card per tracked team with its logo and newest articles of a single day (today by default). */
export function TeamNewsBoard({ refreshKey = 0 }: { refreshKey?: number }) {
  const [teamId, setTeamId] = useState('');
  const today = vnDayKey(new Date());
  const [day, setDay] = useState(today);
  const [hot, setHot] = useState(false);
  const [allTeams, setAllTeams] = useState<TeamNews[]>([]);
  const [teams, setTeams] = useState<TeamNews[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const query = applyRange(new URLSearchParams({ perTeam: String(PER_TEAM) }), 'day', day);
      if (teamId) query.set('teamId', teamId);
      if (hot) query.set('hot', 'true');
      const result = await api<TeamNews[]>(`/api/news/by-team?${query}`);
      setTeams(result);
      if (!teamId) setAllTeams(result); // the club filter always lists every tracked team
    } catch (err) {
      setError(errorMessage(err));
    }
  }, [teamId, day, hot, refreshKey]);

  useEffect(() => {
    void load();
  }, [load]);

  const newsLink = (id: number) => {
    const q = new URLSearchParams({ team: String(id), range: 'day', day });
    if (hot) q.set('hot', '1');
    return `/news?${q}`;
  };

  return (
    <section className="stack">
      <div className="page-head">
        <h3>Tin theo câu lạc bộ · {dayHeading(day)}</h3>
        <Link to="/news">Xem tất cả tin →</Link>
      </div>

      <div className="card news-filters">
        <label>
          <span className="muted small">Câu lạc bộ</span>
          <select value={teamId} onChange={(e) => setTeamId(e.target.value)}>
            <option value="">Tất cả CLB đang theo dõi</option>
            {allTeams.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        </label>

        <label>
          <span className="muted small">Ngày</span>
          <div className="row">
            <button className="btn" onClick={() => setDay(addDays(day, -1))} title="Ngày trước">
              ‹
            </button>
            <input type="date" value={day} max={today} onChange={(e) => e.target.value && setDay(e.target.value)} />
            <button className="btn" onClick={() => setDay(addDays(day, 1))} disabled={day >= today} title="Ngày sau">
              ›
            </button>
            {day !== today && (
              <button className="btn" onClick={() => setDay(today)}>
                Hôm nay
              </button>
            )}
          </div>
        </label>

        <label className="hot-toggle">
          <input type="checkbox" checked={hot} onChange={(e) => setHot(e.target.checked)} />
          <span>🔥 Chỉ tin nóng</span>
        </label>
      </div>

      {error && <div className="alert alert-error">{error}</div>}
      {!teams && !error && <p className="muted">Đang tải…</p>}
      {teams && teams.length === 0 && (
        <div className="card muted">
          Chưa theo dõi đội nào. Thêm đội ở mục <Link to="/teams">Đội bóng</Link>.
        </div>
      )}

      <div className="team-news-grid">
        {teams?.map((team) => (
          <article key={team.id} className="card team-news-card">
            <header className="team-news-head">
              {team.logoUrl ? (
                <img className="team-news-logo" src={team.logoUrl} alt="" loading="lazy" />
              ) : (
                <span className="team-news-logo team-news-logo-empty">{team.name.charAt(0)}</span>
              )}
              <div className="grow">
                <div className="team-news-name">{team.name}</div>
                <div className="muted small">
                  {team.total} tin trong ngày{team.hotCount > 0 && <span className="hot-count"> · 🔥 {team.hotCount} tin nóng</span>}
                </div>
              </div>
            </header>

            {team.items.length === 0 ? (
              <p className="muted small team-news-empty">{hot ? 'Không có tin nóng trong ngày.' : 'Chưa có tin trong ngày.'}</p>
            ) : (
              <ul className="team-news-list">
                {team.items.map((item) => (
                  <li key={item.id}>
                    <a
                      className="team-news-title"
                      href={item.url}
                      target="_blank"
                      rel="noreferrer noopener"
                      title={item.summaryVi ? `${item.title}\n\n${item.summaryVi}` : item.title}
                    >
                      {item.isHot && <span className="hot-dot">🔥 </span>}
                      {item.title}
                    </a>
                    <div className="muted small team-news-meta">
                      {formatTime(item.publishedAtUtc)} · {categoryLabel(item.category)} · {item.sourceName}
                    </div>
                  </li>
                ))}
              </ul>
            )}

            {team.total > team.items.length && (
              <Link className="team-news-more small" to={newsLink(team.id)}>
                Xem thêm {team.total - team.items.length} tin →
              </Link>
            )}
          </article>
        ))}
      </div>
    </section>
  );
}
