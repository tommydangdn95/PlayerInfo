export interface Alias {
  id: number;
  keyword: string;
  language: 'en' | 'vi';
}

export interface Team {
  id: number;
  externalApiId: number;
  name: string;
  country: string | null;
  logoUrl: string | null;
  leagueExternalId: number | null;
  isTracked: boolean;
  updatedAtUtc: string;
  aliases: Alias[];
}

export interface TeamSearchResult {
  externalApiId: number;
  name: string;
  country: string | null;
  logoUrl: string | null;
  founded: number | null;
  national: boolean;
  existingTeamId: number | null;
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

/** Names for common API-Football league ids; unknown ids fall back to "Giải #id". */
export const LEAGUE_NAMES: Record<number, string> = {
  39: 'Premier League (Anh)',
  140: 'La Liga (Tây Ban Nha)',
  135: 'Serie A (Ý)',
  78: 'Bundesliga (Đức)',
  61: 'Ligue 1 (Pháp)',
  2: 'Champions League',
  3: 'Europa League',
};

export function leagueName(id: number | null | undefined): string {
  if (!id) return '—';
  return LEAGUE_NAMES[id] ?? `Giải #${id}`;
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
