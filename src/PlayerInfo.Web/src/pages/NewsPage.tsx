import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorMessage } from '../api';
import { applyRange, parseRange, RANGES } from '../newsFilters';
import { NEWS_CATEGORIES, vnDayKey, type NewsItem, type NewsPage, type Team } from '../types';
import { groupByDay, groupByTeam, NewsGroupList } from './NewsGroups';

const PAGE_SIZE = 100;

export function NewsArticlesPage() {
  const [params, setParams] = useSearchParams();
  const view = params.get('view') === 'team' ? 'team' : 'day';
  const teamId = params.get('team') ?? '';
  const category = params.get('category') ?? '';
  const range = parseRange(params.get('range'), '7d');
  const hot = params.get('hot') === '1';
  const day = params.get('day') ?? vnDayKey(new Date());

  const [teams, setTeams] = useState<Team[]>([]);
  const [items, setItems] = useState<NewsItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function update(changes: Record<string, string>) {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    setParams(next, { replace: true });
  }

  useEffect(() => {
    api<Team[]>('/api/watchlist/teams')
      .then(setTeams)
      .catch(() => setTeams([]));
  }, []);

  const load = useCallback(
    async (pageToLoad: number) => {
      setLoading(true);
      setError(null);
      try {
        const query = new URLSearchParams({ page: String(pageToLoad), pageSize: String(PAGE_SIZE) });
        if (teamId) query.set('teamId', teamId);
        if (category) query.set('category', category);
        if (hot) query.set('hot', 'true');
        applyRange(query, range, day);

        const result = await api<NewsPage>(`/api/news?${query}`);
        setItems((prev) => (pageToLoad === 1 ? result.items : [...prev, ...result.items]));
        setTotal(result.total);
        setPage(pageToLoad);
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        setLoading(false);
      }
    },
    [teamId, category, range, day, hot],
  );

  useEffect(() => {
    void load(1);
  }, [load]);

  const groups = useMemo(() => (view === 'team' ? groupByTeam(items) : groupByDay(items)), [items, view]);
  const trackedTeams = teams.filter((t) => t.isTracked);

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Tin tức</h2>
        <button className="btn btn-ghost" onClick={() => void load(1)} disabled={loading}>
          Làm mới
        </button>
      </div>

      <div className="card news-filters">
        <div className="segmented" role="group" aria-label="Nhóm theo">
          <button className={view === 'day' ? 'active' : ''} onClick={() => update({ view: '' })}>
            Theo ngày
          </button>
          <button className={view === 'team' ? 'active' : ''} onClick={() => update({ view: 'team' })}>
            Theo đội
          </button>
        </div>

        <label>
          <span className="muted small">Đội</span>
          <select value={teamId} onChange={(e) => update({ team: e.target.value })}>
            <option value="">Tất cả đội</option>
            {trackedTeams.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        </label>

        <label>
          <span className="muted small">Loại tin</span>
          <select value={category} onChange={(e) => update({ category: e.target.value })}>
            <option value="">Tất cả</option>
            {NEWS_CATEGORIES.map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </select>
        </label>

        <label>
          <span className="muted small">Thời gian</span>
          <select value={range} onChange={(e) => update({ range: e.target.value === '7d' ? '' : e.target.value })}>
            {RANGES.map((r) => (
              <option key={r.value} value={r.value}>
                {r.label}
              </option>
            ))}
          </select>
        </label>

        {range === 'day' && (
          <label>
            <span className="muted small">Ngày</span>
            <input type="date" value={day} max={vnDayKey(new Date())} onChange={(e) => update({ day: e.target.value })} />
          </label>
        )}

        <label className="hot-toggle">
          <input type="checkbox" checked={hot} onChange={(e) => update({ hot: e.target.checked ? '1' : '' })} />
          <span>🔥 Chỉ tin nóng</span>
        </label>

        <div className="muted small news-total">
          {loading && items.length === 0 ? 'Đang tải…' : `${items.length} / ${total} tin`}
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      <section className="card">
        {!loading && items.length === 0 && !error && (
          <p className="muted">Không có tin nào khớp bộ lọc. Thử chọn khoảng thời gian dài hơn hoặc chạy job "Tin tức".</p>
        )}
        <NewsGroupList groups={groups} mode={view} />
        {items.length < total && (
          <div className="load-more">
            <button className="btn" onClick={() => void load(page + 1)} disabled={loading}>
              {loading ? 'Đang tải…' : `Tải thêm (${total - items.length} tin)`}
            </button>
          </div>
        )}
      </section>
    </div>
  );
}
