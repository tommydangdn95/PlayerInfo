import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { api, errorMessage } from '../api';
import {
  addDays,
  CLAIM_LABELS,
  confidenceMeta,
  dayHeading,
  formatDateTime,
  formatTime,
  topicEmoji,
  vnDayKey,
  type ArticleRef,
  type Briefing,
  type MatchInfo,
} from '../types';
import { ConfidenceBadge, TeamCrest, TierBadge, TopicTag } from './StoryBits';

/** Stories from this importance up are the "key news"; the rest is folded away. */
const KEY_IMPORTANCE = 3;

/** One team, one day: key stories (deduplicated across sources), matches, injuries and transfers. */
export function BriefingPage() {
  const { teamId } = useParams();
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const today = vnDayKey(new Date());
  const day = params.get('day') ?? today;

  const [briefing, setBriefing] = useState<Briefing | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try {
      setBriefing(await api<Briefing>(`/api/stories/briefing/${teamId}?day=${day}`));
    } catch (err) {
      setError(errorMessage(err));
    }
  }, [teamId, day]);

  useEffect(() => {
    void load();
  }, [load]);

  // Jump to the story clicked on the dashboard.
  useEffect(() => {
    if (briefing && location.hash) {
      document.getElementById(location.hash.slice(1))?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }, [briefing, location.hash]);

  function setDay(next: string) {
    setParams(next === today ? {} : { day: next }, { replace: true });
  }

  async function copy() {
    if (!briefing) return;
    await navigator.clipboard.writeText(buildShareText(briefing));
    setCopied(true);
    setTimeout(() => setCopied(false), 2500);
  }

  if (error) return <div className="alert alert-error">{error}</div>;
  if (!briefing) return <p className="muted">Đang tải…</p>;

  const key = briefing.stories.filter((s) => s.story.importance >= KEY_IMPORTANCE);
  const minor = briefing.stories.filter((s) => s.story.importance < KEY_IMPORTANCE);

  return (
    <div className="stack briefing">
      <div className="briefing-head card">
        <TeamCrest name={briefing.team.name} logoUrl={briefing.team.logoUrl} size={56} />
        <div className="grow">
          <h2>{briefing.team.name}</h2>
          <div className="muted">Bản tin {dayHeading(day)}</div>
        </div>
        <div className="row">
          <button className="btn" onClick={() => setDay(addDays(day, -1))} title="Ngày trước">
            ‹
          </button>
          <input type="date" value={day} max={today} onChange={(e) => e.target.value && setDay(e.target.value)} />
          <button className="btn" onClick={() => setDay(addDays(day, 1))} disabled={day >= today} title="Ngày sau">
            ›
          </button>
          <button className="btn btn-primary" onClick={() => void copy()} disabled={briefing.stories.length === 0}>
            {copied ? '✓ Đã chép' : '📋 Copy để đăng'}
          </button>
        </div>
      </div>

      <div className="grid-2">
        <MatchCard title="Trận gần nhất" match={briefing.lastMatch} />
        <MatchCard title="Trận sắp tới" match={briefing.nextMatch} />
      </div>

      <div className="briefing-body">
        <div className="stack">
          <section className="card">
            <h3>🔥 Điểm tin chính</h3>
            {key.length === 0 && <p className="muted">Không có tin quan trọng trong ngày.</p>}
            <div className="story-list">
              {key.map((s, i) => (
                <StoryCard key={s.story.id} index={i + 1} item={s} />
              ))}
            </div>
          </section>

          {minor.length > 0 && (
            <details className="card">
              <summary>
                <strong>Tin khác ({minor.length})</strong> <span className="muted small">ít quan trọng hơn</span>
              </summary>
              <div className="story-list">
                {minor.map((s) => (
                  <StoryCard key={s.story.id} item={s} />
                ))}
              </div>
            </details>
          )}

          {briefing.pendingCount > 0 && (
            <details className="card">
              <summary>
                <strong>Bài chưa được AI gộp ({briefing.pendingCount})</strong>{' '}
                <span className="muted small">đang chờ Gemini xử lý, chưa kiểm chứng</span>
              </summary>
              <SourceList articles={briefing.pendingArticles} />
            </details>
          )}
        </div>

        <aside className="stack">
          <section className="card">
            <h3>🏥 Lực lượng</h3>
            {briefing.injuries.length === 0 ? (
              <p className="muted small">Không có báo cáo chấn thương trong 30 ngày (dữ liệu ESPN).</p>
            ) : (
              <ul className="plain-list">
                {briefing.injuries.map((i) => (
                  <li key={i.playerName}>
                    <strong>{i.playerName}</strong>
                    {i.status && <span className={`tag ${i.status === 'Out' ? 'tag-injury' : ''}`}>{i.status}</span>}
                    <div className="muted small">
                      {i.reason} · {formatDateTime(i.reportedDateUtc)}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="card">
            <h3>🔁 Chuyển nhượng</h3>
            {briefing.transfers.length === 0 ? (
              <p className="muted small">Không có chuyển nhượng chính thức trong 90 ngày (dữ liệu API-Football).</p>
            ) : (
              <ul className="plain-list">
                {briefing.transfers.map((t, i) => (
                  <li key={i}>
                    <strong>{t.incoming ? '⬅️ Đến' : '➡️ Đi'}: {t.playerName}</strong>
                    <div className="muted small">
                      {t.from} → {t.to}
                      {t.type && ` · ${t.type}`} · {formatDateTime(t.dateUtc)}
                    </div>
                  </li>
                ))}
              </ul>
            )}
            <p className="muted small">Tin đồn chuyển nhượng nằm ở phần điểm tin.</p>
          </section>

          <Link to="/" className="small">
            ← Về tổng quan
          </Link>
        </aside>
      </div>
    </div>
  );
}

function StoryCard({ item, index }: { item: Briefing['stories'][number]; index?: number }) {
  const { story, articles, articleCount } = item;
  return (
    <article id={`story-${story.id}`} className={`story-card ${story.isHot ? 'story-hot' : ''}`}>
      <h4>
        {index !== undefined && <span className="story-index">{index}.</span>}
        {story.isHot && '🔥 '}
        {story.headlineVi}
      </h4>
      <div className="story-meta">
        <TopicTag topic={story.topic} />
        <ConfidenceBadge confidence={story.confidence} />
        <span className="muted small">
          {story.sourceCount} nguồn · cập nhật {formatTime(story.lastUpdatedUtc)}
        </span>
      </div>
      {story.summaryVi && <p className="story-summary">{story.summaryVi}</p>}
      <details>
        <summary className="small">Xem {articleCount} bài gốc</summary>
        <SourceList articles={articles} />
        {articleCount > articles.length && <p className="muted small">… và {articleCount - articles.length} bài khác</p>}
      </details>
    </article>
  );
}

function SourceList({ articles }: { articles: ArticleRef[] }) {
  return (
    <ul className="source-list">
      {articles.map((a) => (
        <li key={a.id}>
          <TierBadge tier={a.sourceTier} />
          <span className="source-name">{a.sourceName}</span>
          {a.claimType && <span className="muted small">{CLAIM_LABELS[a.claimType]}</span>}
          <span className="muted small">{formatDateTime(a.publishedAtUtc)}</span>
          <a href={a.url} target="_blank" rel="noreferrer noopener" className="source-title">
            {a.title}
          </a>
        </li>
      ))}
    </ul>
  );
}

function MatchCard({ title, match }: { title: string; match: MatchInfo | null }) {
  return (
    <div className="card match-card">
      <div className="muted small">{title}</div>
      {match ? (
        <>
          <div className="match-line">
            {match.home} {match.homeGoals !== null && match.awayGoals !== null ? `${match.homeGoals} – ${match.awayGoals}` : 'vs'}{' '}
            {match.away}
          </div>
          <div className="muted small">
            {match.league} · {formatDateTime(match.kickoffUtc)}
          </div>
        </>
      ) : (
        <div className="muted small">Chưa có dữ liệu (lịch đấu ESPN chỉ đồng bộ từ hôm qua đến ngày mai).</div>
      )}
    </div>
  );
}

function matchText(m: MatchInfo): string {
  const score = m.homeGoals !== null && m.awayGoals !== null ? `${m.homeGoals}–${m.awayGoals}` : 'vs';
  return `${m.home} ${score} ${m.away} (${m.league}, ${formatDateTime(m.kickoffUtc)})`;
}

/** Plain text for Facebook / Zalo / Telegram: key stories first, then data. */
export function buildShareText(b: Briefing): string {
  const lines: string[] = [];
  lines.push(`⚽ ${b.team.name.toUpperCase()} – Bản tin ${dayHeading(b.day)}`);
  lines.push('');
  if (b.lastMatch) lines.push(`📊 Trận gần nhất: ${matchText(b.lastMatch)}`);
  if (b.nextMatch) lines.push(`📅 Trận sắp tới: ${matchText(b.nextMatch)}`);
  if (b.lastMatch || b.nextMatch) lines.push('');

  const key = b.stories.filter((s) => s.story.importance >= KEY_IMPORTANCE);
  const stories = key.length > 0 ? key : b.stories.slice(0, 3);
  lines.push('🔥 ĐIỂM TIN CHÍNH');
  stories.forEach((s, i) => {
    const conf = confidenceMeta(s.story.confidence);
    lines.push(`${i + 1}. ${topicEmoji(s.story.topic)} ${s.story.headlineVi} (${conf.emoji} ${conf.label})`);
    if (s.story.summaryVi) lines.push(s.story.summaryVi);
    const sources = [...new Set(s.articles.map((a) => a.sourceName))].slice(0, 3);
    if (sources.length > 0) lines.push(`Nguồn: ${sources.join(', ')}`);
    lines.push('');
  });

  if (b.injuries.length > 0) {
    lines.push('🏥 LỰC LƯỢNG');
    b.injuries.forEach((i) => lines.push(`- ${i.playerName}${i.status ? ` (${i.status})` : ''}: ${i.reason}`));
    lines.push('');
  }

  const hashtag = b.team.name.replace(/[^\p{L}\p{N}]/gu, '');
  lines.push(`#${hashtag} #BongDa`);
  return lines.join('\n');
}
