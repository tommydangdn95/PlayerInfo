import { CLAIM_LABELS, dayHeading, formatDateTime, formatTime, vnDayKey, type NewsItem, type NewsTeam } from '../types';
import { TierBadge, TopicTag } from './StoryBits';

export const UNTAGGED_TEAM: NewsTeam = { id: 0, name: 'Tin chung (chưa gắn đội)', logoUrl: null };

interface Group {
  key: string;
  title: string;
  team?: NewsTeam;
  items: NewsItem[];
}

/** Groups by Vietnam calendar day, newest day first (items keep their newest-first order). */
export function groupByDay(items: NewsItem[]): Group[] {
  const groups = new Map<string, Group>();
  for (const item of items) {
    const key = vnDayKey(item.publishedAtUtc);
    let group = groups.get(key);
    if (!group) {
      group = { key, title: dayHeading(key), items: [] };
      groups.set(key, group);
    }
    group.items.push(item);
  }
  return [...groups.values()];
}

/** Groups by team; an article about two teams appears under both. Teams with most news first, untagged last. */
export function groupByTeam(items: NewsItem[]): Group[] {
  const groups = new Map<number, Group>();
  for (const item of items) {
    const teams = item.teams.length > 0 ? item.teams : [UNTAGGED_TEAM];
    for (const team of teams) {
      let group = groups.get(team.id);
      if (!group) {
        group = { key: `team-${team.id}`, title: team.name, team, items: [] };
        groups.set(team.id, group);
      }
      group.items.push(item);
    }
  }
  return [...groups.values()].sort((a, b) => {
    if (a.team?.id === 0) return 1;
    if (b.team?.id === 0) return -1;
    return b.items.length - a.items.length || a.title.localeCompare(b.title, 'vi');
  });
}

export function NewsGroupList({ groups, mode }: { groups: Group[]; mode: 'day' | 'team' }) {
  return (
    <div className="news-groups">
      {groups.map((group) => (
        <section key={group.key} className="news-group">
          <h4 className="news-group-head">
            {group.team && <TeamLogo team={group.team} />}
            <span>{group.title}</span>
            <span className="news-count">{group.items.length} tin</span>
          </h4>
          <ul className="news-list">
            {group.items.map((item) => (
              <NewsRow key={item.id} item={item} mode={mode} hideTeamId={group.team?.id} />
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

function NewsRow({ item, mode, hideTeamId }: { item: NewsItem; mode: 'day' | 'team'; hideTeamId?: number }) {
  const otherTeams = item.teams.filter((t) => t.id !== hideTeamId);
  return (
    <li>
      <div className="news-meta">
        <span className="news-time" title={formatDateTime(item.publishedAtUtc)}>
          {mode === 'day' ? formatTime(item.publishedAtUtc) : formatDateTime(item.publishedAtUtc)}
        </span>
        {item.isHot && <span className="tag tag-hot">🔥 Tin nóng</span>}
        {item.importance > 0 && <TopicTag topic={item.category} />}
        {item.claimType && <span className="tag">{CLAIM_LABELS[item.claimType]}</span>}
        {otherTeams.map((team) => (
          <span key={team.id} className="team-chip">
            <TeamLogo team={team} />
            {team.name}
          </span>
        ))}
        {mode === 'day' && item.teams.length === 0 && <span className="team-chip team-chip-none">Chưa gắn đội</span>}
        <TierBadge tier={item.sourceTier} />
        <span className="muted small">{item.sourceName}</span>
      </div>
      <a href={item.url} target="_blank" rel="noreferrer noopener">
        {item.title}
      </a>
      {item.summaryVi && <p className="summary">{item.summaryVi}</p>}
    </li>
  );
}

export function TeamLogo({ team }: { team: NewsTeam }) {
  return team.logoUrl ? <img className="team-logo" src={team.logoUrl} alt="" loading="lazy" /> : null;
}
