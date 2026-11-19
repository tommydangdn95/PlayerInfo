export interface Alias {
  id: number;
  keyword: string;
  language: 'en' | 'vi';
}

export interface Team {
  id: number;
  /** ESPN team id; null for teams not yet linked to ESPN. */
  espnId: string | null;
  /** ESPN league slug, e.g. "eng.1". */
  leagueSlug: string | null;
  /** Optional API-Football id, only used for transfers. */
  apiFootballId: number | null;
  name: string;
  country: string | null;
  logoUrl: string | null;
  isTracked: boolean;
  updatedAtUtc: string;
  aliases: Alias[];
}

export interface League {
  slug: string;
  name: string;
}

/** A team of an ESPN league, as listed for adding/linking. */
export interface LeagueTeam {
  espnId: string;
  name: string;
  shortName: string | null;
  logoUrl: string | null;
  existingTeamId: number | null;
}

/** API-Football search result, used to link a team for transfers. */
export interface ApiFootballTeam {
  apiFootballId: number;
  name: string;
  country: string | null;
  logoUrl: string | null;
  founded: number | null;
  national: boolean;
  linkedTeamId: number | null;
}

export interface Quota {
  dateUtc: string;
  apiFootball: number;
  newsData: number;
  apiFootballLimit: number;
  newsDataLimit: number;
}

export interface NewsItem {
  id: number;
  title: string;
  summaryVi: string | null;
  category: string;
  sourceName: string;
  url: string;
  publishedAtUtc: string;
}

export interface NewsPage {
  page: number;
  pageSize: number;
  items: NewsItem[];
}

export type JobMode = 'news' | 'transfers_news' | 'full' | 'roster';

export function leagueName(slug: string | null | undefined, leagues: League[]): string {
  if (!slug) return '—';
  return leagues.find((l) => l.slug === slug)?.name ?? slug;
}

export function formatDateTime(utc: string): string {
  return new Date(utc).toLocaleString('vi-VN', {
    timeZone: 'Asia/Ho_Chi_Minh',
    hour: '2-digit',
    minute: '2-digit',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
}
