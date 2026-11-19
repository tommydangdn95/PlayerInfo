import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { api, errorMessage } from '../api';
import { leagueName, type Alias, type ApiFootballTeam, type League, type LeagueTeam, type Team } from '../types';

export function TeamsPage() {
  const [teams, setTeams] = useState<Team[] | null>(null);
  const [leagues, setLeagues] = useState<League[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState('');

  const load = useCallback(async () => {
    setError(null);
    try {
      const [t, l] = await Promise.all([api<Team[]>('/api/watchlist/teams'), api<League[]>('/api/watchlist/leagues')]);
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
        leagueName(t.leagueSlug, leagues).toLowerCase().includes(q) ||
        t.aliases.some((a) => a.keyword.toLowerCase().includes(q)),
    );
  }, [teams, filter, leagues]);

  const trackedCount = teams?.filter((t) => t.isTracked).length ?? 0;
  const transferTeams = teams?.filter((t) => t.isTracked && t.apiFootballId).length ?? 0;
  const unlinked = teams?.filter((t) => !t.espnId).length ?? 0;

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Đội bóng</h2>
        <span className="muted">{teams ? `${trackedCount} đang theo dõi / ${teams.length} đội` : ''}</span>
      </div>

      {unlinked > 0 && (
        <div className="alert alert-warn">
          {unlinked} đội chưa liên kết ESPN nên chưa có chấn thương, đội hình và tin ESPN. Chọn đội ESPN tương ứng ở thẻ của đội.
        </div>
      )}
      {transferTeams > 19 && (
        <div className="alert alert-warn">
          {transferTeams} đội đang lấy chuyển nhượng từ API-Football: dễ vượt quota 90 request/ngày (khuyến nghị ≤ 19).
        </div>
      )}
      {error && <div className="alert alert-error">{error}</div>}

      <AddTeamPanel leagues={leagues} onAdded={() => void load()} />

      <section className="card">
        <div className="page-head">
          <h3>Danh sách đội</h3>
          <input
            className="filter"
            placeholder="Lọc theo tên, giải, từ khóa…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
        </div>
        {!teams && <p className="muted">Đang tải…</p>}
        {teams && teams.length === 0 && <p className="muted">Chưa có đội nào. Chọn giải và đội ở phía trên.</p>}
        <div className="team-list">
          {visible?.map((team) => (
            <TeamCard key={team.id} team={team} leagues={leagues} onChanged={replaceTeam} onDeleted={removeTeam} />
          ))}
        </div>
      </section>
    </div>
  );
}

// ---------------------------------------------------------------- ESPN league teams

function useLeagueTeams(slug: string) {
  const [teams, setTeams] = useState<LeagueTeam[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    if (!slug) return;
    setTeams(null);
    setError(null);
    try {
      setTeams(await api<LeagueTeam[]>(`/api/watchlist/league-teams?league=${encodeURIComponent(slug)}`));
    } catch (err) {
      setError(errorMessage(err));
    }
  }, [slug]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return { teams, error, reload, setTeams };
}

function AddTeamPanel({ leagues, onAdded }: { leagues: League[]; onAdded: () => void }) {
  const [league, setLeague] = useState('');
  const [query, setQuery] = useState('');
  const [withAlias, setWithAlias] = useState(true);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!league && leagues.length > 0) setLeague(leagues[0].slug);
  }, [leagues, league]);

  const { teams, error: loadError, setTeams } = useLeagueTeams(league);

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? teams?.filter((t) => t.name.toLowerCase().includes(q) || (t.shortName ?? '').toLowerCase().includes(q)) : teams;
  }, [teams, query]);

  async function add(t: LeagueTeam) {
    setBusyId(t.espnId);
    setError(null);
    try {
      const team = await api<Team>('/api/watchlist/teams', {
        method: 'POST',
        body: { espnId: t.espnId, leagueSlug: league, name: t.name, logoUrl: t.logoUrl },
      });
      if (withAlias) {
        await api('/api/watchlist/aliases', {
          method: 'POST',
          body: { entityType: 'Team', entityId: team.id, keyword: t.name, language: 'en' },
        });
      }
      setTeams((prev) => prev?.map((x) => (x.espnId === t.espnId ? { ...x, existingTeamId: team.id } : x)) ?? null);
      onAdded();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusyId(null);
    }
  }

  return (
    <section className="card">
      <h3>Thêm đội</h3>
      <div className="row">
        <select value={league} onChange={(e) => setLeague(e.target.value)}>
          {leagues.map((l) => (
            <option key={l.slug} value={l.slug}>
              {l.name}
            </option>
          ))}
        </select>
        <input placeholder="Lọc tên đội…" value={query} onChange={(e) => setQuery(e.target.value)} />
        <label className="checkbox small" title="Tạo sẵn từ khóa tìm tin bằng tên đội (tiếng Anh)">
          <input type="checkbox" checked={withAlias} onChange={(e) => setWithAlias(e.target.checked)} />
          Tạo từ khóa từ tên đội
        </label>
      </div>
      <p className="muted small">Danh sách đội lấy từ ESPN (không tốn quota).</p>
      {(error || loadError) && <div className="alert alert-error">{error ?? loadError}</div>}
      {!teams && !loadError && <p className="muted">Đang tải…</p>}
      {teams && teams.length === 0 && <p className="muted">ESPN không trả về đội nào cho giải này.</p>}

      <div className="league-grid">
        {visible?.map((t) => (
          <div key={t.espnId} className="league-team">
            <TeamLogo url={t.logoUrl} />
            <span className="grow">{t.name}</span>
            {t.existingTeamId ? (
              <span className="tag tag-ok">Đã có</span>
            ) : (
              <button className="btn btn-primary" onClick={() => void add(t)} disabled={busyId !== null}>
                {busyId === t.espnId ? '…' : 'Theo dõi'}
              </button>
            )}
          </div>
        ))}
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- Team card

type TeamPatch = Partial<{
  isTracked: boolean;
  leagueSlug: string;
  espnId: string;
  apiFootballId: number;
}>;

function TeamCard({
  team,
  leagues,
  onChanged,
  onDeleted,
}: {
  team: Team;
  leagues: League[];
  onChanged: (team: Team) => void;
  onDeleted: (id: number) => void;
}) {
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);

  async function patch(body: TeamPatch) {
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

  return (
    <div className={`team-card ${team.isTracked ? '' : 'team-paused'}`}>
      <div className="team-head">
        <TeamLogo url={team.logoUrl} />
        <div className="grow">
          <strong>{team.name}</strong>
          <div className="muted small">
            {leagueName(team.leagueSlug, leagues)}
            {team.espnId ? ` · ESPN ${team.espnId}` : ''}
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

      {!team.espnId && <LinkEspn team={team} leagues={leagues} busy={busy} onLink={(slug, espnId) => void patch({ leagueSlug: slug, espnId })} />}

      <AliasEditor team={team} onChanged={onChanged} />

      <ApiFootballLink team={team} busy={busy} onLink={(id) => void patch({ apiFootballId: id })} />

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

/** For teams added before the switch to ESPN: pick the matching ESPN team. */
function LinkEspn({
  team,
  leagues,
  busy,
  onLink,
}: {
  team: Team;
  leagues: League[];
  busy: boolean;
  onLink: (leagueSlug: string, espnId: string) => void;
}) {
  const [league, setLeague] = useState(team.leagueSlug ?? leagues[0]?.slug ?? '');
  const [espnId, setEspnId] = useState('');
  const { teams } = useLeagueTeams(league);

  useEffect(() => {
    // Preselect the ESPN team with the same name, if any.
    const match = teams?.find((t) => t.name.toLowerCase() === team.name.toLowerCase() && !t.existingTeamId);
    setEspnId(match?.espnId ?? '');
  }, [teams, team.name]);

  return (
    <div className="link-box">
      <div className="small">⚠️ Chưa liên kết ESPN</div>
      <div className="row">
        <select value={league} onChange={(e) => setLeague(e.target.value)}>
          {leagues.map((l) => (
            <option key={l.slug} value={l.slug}>
              {l.name}
            </option>
          ))}
        </select>
        <select value={espnId} onChange={(e) => setEspnId(e.target.value)}>
          <option value="">— chọn đội —</option>
          {teams
            ?.filter((t) => !t.existingTeamId)
            .map((t) => (
              <option key={t.espnId} value={t.espnId}>
                {t.name}
              </option>
            ))}
        </select>
        <button className="btn" disabled={busy || !espnId} onClick={() => onLink(league, espnId)}>
          Liên kết
        </button>
      </div>
    </div>
  );
}

/** Optional API-Football id, only used to sync transfers. */
function ApiFootballLink({ team, busy, onLink }: { team: Team; busy: boolean; onLink: (id: number) => void }) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState(team.name);
  const [results, setResults] = useState<ApiFootballTeam[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function search(e: FormEvent) {
    e.preventDefault();
    setSearching(true);
    setError(null);
    try {
      const found = await api<ApiFootballTeam[]>(`/api/watchlist/search-teams?q=${encodeURIComponent(query.trim())}`);
      setResults(found);
      if (found.length === 0) setError('Không tìm thấy (hoặc đã hết quota API-Football hôm nay).');
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setSearching(false);
    }
  }

  if (team.apiFootballId) {
    return (
      <div className="row small">
        <span className="muted">Chuyển nhượng (API-Football):</span>
        <span>id {team.apiFootballId}</span>
        <button className="link-button" disabled={busy} onClick={() => onLink(0)}>
          bỏ liên kết
        </button>
      </div>
    );
  }

  if (!open) {
    return (
      <div className="row small">
        <span className="muted">Chuyển nhượng: chưa bật</span>
        <button className="link-button" onClick={() => setOpen(true)}>
          liên kết API-Football
        </button>
      </div>
    );
  }

  return (
    <div className="link-box">
      <div className="small muted">Tìm đội trên API-Football để lấy chuyển nhượng (mỗi lần tìm tốn 1 request quota).</div>
      <form className="row" onSubmit={search}>
        <input value={query} onChange={(e) => setQuery(e.target.value)} minLength={3} required />
        <button className="btn" type="submit" disabled={searching}>
          {searching ? '…' : 'Tìm'}
        </button>
        <button className="btn btn-ghost" type="button" onClick={() => setOpen(false)}>
          Đóng
        </button>
      </form>
      {error && <div className="error-text">{error}</div>}
      {results?.map((r) => (
        <div key={r.apiFootballId} className="result-row">
          <TeamLogo url={r.logoUrl} />
          <div className="grow small">
            <strong>{r.name}</strong> · {r.country ?? '—'} · id {r.apiFootballId}
          </div>
          {r.linkedTeamId ? (
            <span className="tag">Đã gắn</span>
          ) : (
            <button className="btn btn-primary" disabled={busy} onClick={() => onLink(r.apiFootballId)}>
              Chọn
            </button>
          )}
        </div>
      ))}
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
      <div className="muted small">Từ khóa tìm tin (Google News, RSS):</div>
      <div className="chips">
        {team.aliases.length === 0 && <span className="muted small">Chưa có từ khóa (vẫn có tin ESPN nếu đã liên kết).</span>}
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
