import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { addDays, dayHeading, vnDayKey, type TeamStories } from '../types';
import { ConfidenceBadge, TeamCrest, TopicTag } from './StoryBits';

const PER_TEAM = 3;

/**
 * Dashboard: one card per tracked team with the day's most important stories (the same news from several
 * sources counts once), linking to the team's full briefing.
 */
export function TeamNewsBoard({ refreshKey = 0 }: { refreshKey?: number }) {
  const today = vnDayKey(new Date());
  const [teamId, setTeamId] = useState('');
  const [day, setDay] = useState(today);
  const [hot, setHot] = useState(false);
  const [allTeams, setAllTeams] = useState<TeamStories[]>([]);
  const [teams, setTeams] = useState<TeamStories[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const query = new URLSearchParams({ day, perTeam: String(PER_TEAM) });
      if (teamId) query.set('teamId', teamId);
      if (hot) query.set('hot', 'true');
      const result = await api<TeamStories[]>(`/api/stories/by-team?${query}`);
      setTeams(result);
      if (!teamId) setAllTeams(result); // the club filter always lists every tracked team
    } catch (err) {
      setError(errorMessage(err));
    }
  }, [teamId, day, hot, refreshKey]);

  useEffect(() => {
    void load();
  }, [load]);

  const briefingLink = (id: number) => `/briefing/${id}?day=${day}`;

  return (
    <section className="stack">
      <div className="page-head">
        <h3>Điểm tin theo câu lạc bộ · {dayHeading(day)}</h3>
        <Link to="/news">Xem tất cả bài báo →</Link>
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
            <Link to={briefingLink(team.id)} className="team-news-head">
              <TeamCrest name={team.name} logoUrl={team.logoUrl} />
              <div className="grow">
                <div className="team-news-name">{team.name}</div>
                <div className="muted small">
                  {team.storyCount} câu chuyện
                  {team.hotCount > 0 && <span className="hot-count"> · 🔥 {team.hotCount} tin nóng</span>}
                </div>
              </div>
            </Link>

            {team.stories.length === 0 ? (
              <p className="muted small team-news-empty">
                {hot ? 'Không có tin nóng trong ngày.' : 'Chưa có câu chuyện nào trong ngày.'}
                {team.pendingCount > 0 && <> {team.pendingCount} bài đang chờ AI xử lý.</>}
              </p>
            ) : (
              <ul className="team-news-list">
                {team.stories.map((story) => (
                  <li key={story.id}>
                    <Link
                      className="team-news-title"
                      to={`${briefingLink(team.id)}#story-${story.id}`}
                      title={story.summaryVi ? `${story.headlineVi}\n\n${story.summaryVi}` : story.headlineVi}
                    >
                      {story.isHot && '🔥 '}
                      {story.headlineVi}
                    </Link>
                    <div className="story-meta">
                      <TopicTag topic={story.topic} />
                      <ConfidenceBadge confidence={story.confidence} />
                      <span className="muted small">{story.sourceCount} nguồn</span>
                    </div>
                  </li>
                ))}
              </ul>
            )}

            <Link className="team-news-more small" to={briefingLink(team.id)}>
              Bản tin đầy đủ
              {team.storyCount > team.stories.length && ` (+${team.storyCount - team.stories.length} câu chuyện)`} →
            </Link>
          </article>
        ))}
      </div>
    </section>
  );
}
