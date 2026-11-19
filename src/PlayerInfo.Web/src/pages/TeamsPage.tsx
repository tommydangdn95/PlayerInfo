import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { api, errorMessage } from '../api';
import { leagueName, type Alias, type Team, type TeamSearchResult } from '../types';

export function TeamsPage() {
  const [teams, setTeams] = useState<Team[] | null>(null);
  const [leagues, setLeagues] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState('');

  const load = useCallback(async () => {
    setError(null);
    try {
      const [t, l] = await Promise.all([api<Team[]>('/api/watchlist/teams'), api<number[]>('/api/watchlist/leagues')]);
      setTeams(t);
      setLeagues(l);
    } catch (err) {
      setError(errorMessage(err));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const replaceTeam = (updated: Team) => setTeams((prev) => prev?.map((t) => (t.id === updated.id ? updated : t)) ?? null);
  const removeTeam = (id: number) => setTeams((prev) => prev?.filter((t) => t.id !== id) ?? null);

  const visible = useMemo(() => {
    const q = filter.trim().toLowerCase();
    if (!teams || !q) return teams;
    return teams.filter(
      (t) =>
        t.name.toLowerCase().includes(q) ||
        (t.country ?? '').toLowerCase().includes(q) ||
        t.aliases.some((a) => a.keyword.toLowerCase().includes(q)),
    );
  }, [teams, filter]);

  const trackedCount = teams?.filter((t) => t.isTracked).length ?? 0;

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Đội bóng</h2>
        <span className="muted">
          {teams ? `${trackedCount} đang theo dõi / ${teams.length} đội` : ''}
        </span>
      </div>

      {trackedCount > 19 && (
        <div className="alert alert-warn">
          Đang theo dõi {trackedCount} đội: có thể vượt quota 90 request/ngày của API-Football (khuyến nghị ≤ 19 đội).
        </div>
      )}
      {error && <div className="alert alert-error">{error}</div>}

      <AddTeamPanel leagues={leagues} onAdded={() => void load()} />

      <section className="card">
        <div className="page-head">
          <h3>Danh sách đội</h3>
          <input
            className="filter"
            placeholder="Lọc theo tên, quốc gia, từ khóa…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
        </div>
        {!teams && <p className="muted">Đang tải…</p>}
        {teams && teams.length === 0 && <p className="muted">Chưa có đội nào. Tìm và thêm đội ở phía trên.</p>}
        <div className="team-list">
          {visible?.map((team) => (
            <TeamCard key={team.id} team={team} leagues={leagues} onChanged={replaceTeam} onDeleted={removeTeam} />
          ))}
        </div>
      </section>
    </div>
  );
}

// ---------------------------------------------------------------- Add team

function AddTeamPanel({ leagues, onAdded }: { leagues: number[]; onAdded: () => void }) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<TeamSearchResult[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'error' | 'info'; text: string } | null>(null);

  async function search(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      const found = await api<TeamSearchResult[]>(`/api/watchlist/search-teams?q=${encodeURIComponent(query.trim())}`);
      setResults(found);
      if (found.length === 0) {
        setMessage({ kind: 'info', text: 'Không tìm thấy đội nào (hoặc đã hết quota API-Football hôm nay).' });
      }
    } catch (err) {
      setMessage({ kind: 'error', text: errorMessage(err) });
    } finally {
      setBusy(false);
    }
  }

  function markAdded(externalApiId: number, teamId: number) {
    setResults((prev) => prev?.map((r) => (r.externalApiId === externalApiId ? { ...r, existingTeamId: teamId } : r)) ?? null);
    onAdded();
  }

  return (
    <section className="card">
      <h3>Thêm đội</h3>
      <form className="row" onSubmit={search}>
        <input
          placeholder="Tên đội bằng tiếng Anh, vd: Arsenal, Real Madrid"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          minLength={3}
          required
        />
        <button className="btn btn-primary" type="submit" disabled={busy}>
          {busy ? 'Đang tìm…' : 'Tìm'}
        </button>
      </form>
      <p className="muted small">Mỗi lần tìm tốn 1 request quota API-Football.</p>
      {message && <div className={`alert alert-${message.kind}`}>{message.text}</div>}

      {results && results.length > 0 && (
        <div className="search-results">
          {results.map((r) => (
            <SearchResultRow key={r.externalApiId} result={r} leagues={leagues} onAdded={markAdded} />
          ))}
        </div>
      )}
    </section>
  );
}

function SearchResultRow({
  result,
  leagues,
  onAdded,
}: {
  result: TeamSearchResult;
  leagues: number[];
  onAdded: (externalApiId: number, teamId: number) => void;
}) {
  const [league, setLeague] = useState<number | ''>('');
  const [withAlias, setWithAlias] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function add() {
    setBusy(true);
    setError(null);
    try {
      const team = await api<Team>('/api/watchlist/teams', {
        method: 'POST',
        body: {
          externalApiId: result.externalApiId,
          name: result.name,
          country: result.country,
          logoUrl: result.logoUrl,
          leagueExternalId: league === '' ? null : league,
        },
      });
      if (withAlias) {
        await api('/api/watchlist/aliases', {
          method: 'POST',
          body: { entityType: 'Team', entityId: team.id, keyword: result.name, language: 'en' },
        });
      }
      onAdded(result.externalApiId, team.id);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="result-row">
      <TeamLogo url={result.logoUrl} />
      <div className="grow">
        <strong>{result.name}</strong>
        <div className="muted small">
          {result.country ?? '—'}
          {result.founded ? ` · thành lập ${result.founded}` : ''}
          {result.national ? ' · đội tuyển quốc gia' : ''} · id {result.externalApiId}
        </div>
        {error && <div className="error-text">{error}</div>}
      </div>
      {result.existingTeamId ? (
        <span className="tag tag-ok">Đã có</span>
      ) : (
        <div className="row">
          <select value={league} onChange={(e) => setLeague(e.target.value === '' ? '' : Number(e.target.value))}>
            <option value="">Giải đấu (tùy chọn)</option>
            {leagues.map((id) => (
              <option key={id} value={id}>
                {leagueName(id)}
              </option>
            ))}
          </select>
          <label className="checkbox small" title="Tạo sẵn từ khóa tìm tin bằng tên đội (tiếng Anh)">
            <input type="checkbox" checked={withAlias} onChange={(e) => setWithAlias(e.target.checked)} />
            Từ khóa
          </label>
          <button className="btn btn-primary" onClick={() => void add()} disabled={busy}>
            {busy ? '…' : 'Theo dõi'}
          </button>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Team card

function TeamCard({
  team,
  leagues,
  onChanged,
  onDeleted,
}: {
  team: Team;
  leagues: number[];
  onChanged: (team: Team) => void;
  onDeleted: (id: number) => void;
}) {
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);

  async function patch(body: Partial<Pick<Team, 'isTracked' | 'leagueExternalId'>>) {
    setBusy(true);
    setError(null);
    try {
      onChanged(await api<Team>(`/api/watchlist/teams/${team.id}`, { method: 'PATCH', body }));
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setBusy(true);
    setError(null);
    try {
      await api(`/api/watchlist/teams/${team.id}`, { method: 'DELETE' });
      onDeleted(team.id);
    } catch (err) {
      setError(errorMessage(err));
      setBusy(false);
    }
  }

  const leagueOptions = team.leagueExternalId && !leagues.includes(team.leagueExternalId)
    ? [...leagues, team.leagueExternalId]
    : leagues;

  return (
    <div className={`team-card ${team.isTracked ? '' : 'team-paused'}`}>
      <div className="team-head">
        <TeamLogo url={team.logoUrl} />
        <div className="grow">
          <strong>{team.name}</strong>
          <div className="muted small">
            {team.country ?? "—"} · API id {team.externalApiId}
          </div>
        </div>
        <label className="switch" title={team.isTracked ? 'Đang theo dõi' : 'Tạm dừng'}>
          <input
            type="checkbox"
            checked={team.isTracked}
            disabled={busy}
            onChange={(e) => void patch({ isTracked: e.target.checked })}
          />
          <span>{team.isTracked ? 'Theo dõi' : 'Tạm dừng'}</span>
        </label>
      </div>

      <div className="row small">
        <span className="muted">Giải:</span>
        <select
          value={team.leagueExternalId ?? 0}
          disabled={busy}
          onChange={(e) => void patch({ leagueExternalId: Number(e.target.value) })}
        >
          <option value={0}>—</option>
          {leagueOptions.map((id) => (
            <option key={id} value={id}>
              {leagueName(id)}
            </option>
          ))}
        </select>
      </div>

      <AliasEditor team={team} onChanged={onChanged} />

      {error && <div className="error-text">{error}</div>}

      <div className="team-actions">
        {confirmDelete ? (
          <>
            <span className="small">Xóa đội và toàn bộ từ khóa?</span>
            <button className="btn btn-danger" onClick={() => void remove()} disabled={busy}>
              Xóa
            </button>
            <button className="btn btn-ghost" onClick={() => setConfirmDelete(false)}>
              Hủy
            </button>
          </>
        ) : (
          <button className="btn btn-ghost btn-danger-text" onClick={() => setConfirmDelete(true)}>
            Xóa đội
          </button>
        )}
      </div>
    </div>
  );
}

function AliasEditor({ team, onChanged }: { team: Team; onChanged: (team: Team) => void }) {
  const [keyword, setKeyword] = useState('');
  const [language, setLanguage] = useState<'en' | 'vi'>('vi');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function add(e: FormEvent) {
    e.preventDefault();
    if (!keyword.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const created = await api<{ id: number; keyword: string; language: 'en' | 'vi' }>('/api/watchlist/aliases', {
        method: 'POST',
        body: { entityType: 'Team', entityId: team.id, keyword: keyword.trim(), language },
      });
      onChanged({ ...team, aliases: [...team.aliases, created].sort((a, b) => a.keyword.localeCompare(b.keyword)) });
      setKeyword('');
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  async function remove(alias: Alias) {
    setError(null);
    try {
      await api(`/api/watchlist/aliases/${alias.id}`, { method: 'DELETE' });
      onChanged({ ...team, aliases: team.aliases.filter((a) => a.id !== alias.id) });
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  return (
    <div className="aliases">
      <div className="muted small">Từ khóa tìm tin:</div>
      <div className="chips">
        {team.aliases.length === 0 && (
          <span className="muted small">Chưa có từ khóa — đội này sẽ không được tìm tin tức.</span>
        )}
        {team.aliases.map((a) => (
          <span key={a.id} className="chip">
            <span className="chip-lang">{a.language}</span>
            {a.keyword}
            <button title="Xóa từ khóa" onClick={() => void remove(a)}>
              ×
            </button>
          </span>
        ))}
      </div>
      <form className="row" onSubmit={add}>
        <input
          placeholder="Thêm từ khóa, vd: Pháo thủ"
          value={keyword}
          onChange={(e) => setKeyword(e.target.value)}
          maxLength={200}
        />
        <select value={language} onChange={(e) => setLanguage(e.target.value as 'en' | 'vi')}>
          <option value="vi">vi</option>
          <option value="en">en</option>
        </select>
        <button className="btn" type="submit" disabled={busy || !keyword.trim()}>
          Thêm
        </button>
      </form>
      {error && <div className="error-text">{error}</div>}
    </div>
  );
}

function TeamLogo({ url }: { url: string | null }) {
  return url ? <img className="logo" src={url} alt="" loading="lazy" /> : <div className="logo logo-empty">⚽</div>;
}
