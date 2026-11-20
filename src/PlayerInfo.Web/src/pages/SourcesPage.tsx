import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage } from '../api';
import { TIERS, type NewsSourceRule } from '../types';
import { TierBadge } from './StoryBits';

interface Unclassified {
  sourceName: string;
  articleCount: number;
}

/**
 * Source credibility ("Nguồn tin"): a source whose name or domain contains the pattern gets the tier.
 * Junk sources are hidden; stories are rated "confirmed" only when reliable sources report them.
 */
export function SourcesPage() {
  const [sources, setSources] = useState<NewsSourceRule[]>([]);
  const [unclassified, setUnclassified] = useState<Unclassified[]>([]);
  const [pattern, setPattern] = useState('');
  const [tier, setTier] = useState(2);
  const [filter, setFilter] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [s, u] = await Promise.all([
        api<NewsSourceRule[]>('/api/sources'),
        api<Unclassified[]>('/api/sources/unclassified'),
      ]);
      setSources(s);
      setUnclassified(u);
    } catch (err) {
      setError(errorMessage(err));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function run(action: () => Promise<{ reapplied: number }>, done: string) {
    setError(null);
    setMessage(null);
    try {
      const result = await action();
      setMessage(`${done} Đã cập nhật lại ${result.reapplied} bài trong 30 ngày.`);
      await load();
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  const add = (p: string, t: number) =>
    run(async () => {
      const r = await api<{ reapplied: number }>('/api/sources', { method: 'POST', body: { pattern: p, tier: t } });
      setPattern('');
      return r;
    }, `Đã thêm "${p}".`);

  const update = (s: NewsSourceRule, t: number) =>
    run(() => api<{ reapplied: number }>(`/api/sources/${s.id}`, { method: 'PUT', body: { pattern: s.pattern, tier: t } }), `Đã đổi "${s.pattern}".`);

  const remove = (s: NewsSourceRule) =>
    run(() => api<{ reapplied: number }>(`/api/sources/${s.id}`, { method: 'DELETE' }), `Đã xóa "${s.pattern}".`);

  const shown = sources.filter((s) => s.pattern.toLowerCase().includes(filter.trim().toLowerCase()));

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Nguồn tin</h2>
      </div>

      <div className="card">
        <p className="muted small">
          Nguồn nào có tên hoặc tên miền chứa mẫu bên dưới sẽ được xếp hạng tương ứng (mẫu dài hơn được ưu tiên). Nguồn
          không khớp mẫu nào tính là Hạng 3. Một câu chuyện chỉ được coi là <strong>chính thức</strong> hay{' '}
          <strong>nhiều nguồn uy tín</strong> khi có nguồn hạng 1–2 đưa tin. Nguồn <strong>Rác</strong> bị ẩn và không gửi
          cho Gemini.
        </p>
        <div className="tier-legend">
          {TIERS.map((t) => (
            <span key={t.value}>
              <TierBadge tier={t.value} /> <span className="muted small">{t.hint}</span>
            </span>
          ))}
        </div>
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault();
            if (pattern.trim()) void add(pattern.trim(), tier);
          }}
        >
          <input placeholder="Tên nguồn hoặc tên miền, ví dụ: arsenal.com" value={pattern} onChange={(e) => setPattern(e.target.value)} />
          <select value={tier} onChange={(e) => setTier(Number(e.target.value))}>
            {TIERS.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
          <button className="btn btn-primary" type="submit">
            Thêm
          </button>
        </form>
        {message && <div className="alert alert-info">{message}</div>}
        {error && <div className="alert alert-error">{error}</div>}
      </div>

      <div className="grid-2 sources-grid">
        <section className="card">
          <div className="page-head">
            <h3>Quy tắc ({sources.length})</h3>
            <input className="filter" placeholder="Lọc…" value={filter} onChange={(e) => setFilter(e.target.value)} />
          </div>
          <table className="table">
            <tbody>
              {shown.map((s) => (
                <tr key={s.id}>
                  <td>{s.pattern}</td>
                  <td>
                    <select value={s.tier} onChange={(e) => void update(s, Number(e.target.value))}>
                      {TIERS.map((t) => (
                        <option key={t.value} value={t.value}>
                          {t.label}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td>
                    <button className="btn btn-ghost" onClick={() => void remove(s)} title="Xóa quy tắc">
                      ✕
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>

        <section className="card">
          <h3>Nguồn chưa xếp hạng (7 ngày qua)</h3>
          <p className="muted small">Đang tính là Hạng 3. Xếp hạng nhanh các nguồn đưa nhiều tin nhất:</p>
          {unclassified.length === 0 && <p className="muted small">Mọi nguồn đều đã có quy tắc.</p>}
          <table className="table">
            <tbody>
              {unclassified.map((u) => (
                <tr key={u.sourceName}>
                  <td>
                    {u.sourceName} <span className="muted small">({u.articleCount} bài)</span>
                  </td>
                  <td className="nowrap">
                    {TIERS.map((t) => (
                      <button key={t.value} className="btn btn-ghost btn-xs" onClick={() => void add(u.sourceName, t.value)}>
                        {t.label}
                      </button>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      </div>
    </div>
  );
}
