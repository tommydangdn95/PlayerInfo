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
  /** Major/breaking news for a tracked club, as judged by Gemini. */
  isHot: boolean;
  /** Teams the article is about: linked directly or through a linked player. */
  teams: NewsTeam[];
}

/** A tracked team with its newest articles (GET /api/news/by-team). */
export interface TeamNews {
  id: number;
  name: string;
  logoUrl: string | null;
  leagueSlug: string | null;
  total: number;
  hotCount: number;
  items: NewsItem[];
}

export interface NewsTeam {
  id: number;
  name: string;
  logoUrl: string | null;
}

export interface NewsPage {
  page: number;
  pageSize: number;
  total: number;
  items: NewsItem[];
}

export const NEWS_CATEGORIES: { value: string; label: string }[] = [
  { value: 'Injury', label: 'Chấn thương' },
  { value: 'Transfer', label: 'Chuyển nhượng' },
  { value: 'MatchPerformance', label: 'Trận đấu' },
  { value: 'OffPitch', label: 'Ngoài sân cỏ' },
  { value: 'Other', label: 'Khác' },
];

export function categoryLabel(category: string): string {
  return NEWS_CATEGORIES.find((c) => c.value === category)?.label ?? category;
}

const VN_TZ = 'Asia/Ho_Chi_Minh';

/** Calendar day of an instant in Vietnam time, as yyyy-MM-dd. */
export function vnDayKey(utc: string | Date): string {
  return new Date(utc).toLocaleDateString('en-CA', { timeZone: VN_TZ });
}

/** UTC instant of 00:00 Vietnam time (UTC+7, no DST) on the given yyyy-MM-dd day. */
export function vnDayStartUtc(dayKey: string): Date {
  return new Date(`${dayKey}T00:00:00+07:00`);
}

export function addDays(dayKey: string, days: number): string {
  const d = vnDayStartUtc(dayKey);
  d.setUTCDate(d.getUTCDate() + days);
  return vnDayKey(d);
}

/** "Hôm nay · Thứ Năm, 20/11/2026" style heading for a yyyy-MM-dd day. */
export function dayHeading(dayKey: string): string {
  const today = vnDayKey(new Date());
  const prefix = dayKey === today ? 'Hôm nay · ' : dayKey === addDays(today, -1) ? 'Hôm qua · ' : '';
  const label = vnDayStartUtc(dayKey).toLocaleDateString('vi-VN', {
    timeZone: VN_TZ,
    weekday: 'long',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
  return prefix + label.charAt(0).toUpperCase() + label.slice(1);
}

export function formatTime(utc: string): string {
  return new Date(utc).toLocaleTimeString('vi-VN', { timeZone: VN_TZ, hour: '2-digit', minute: '2-digit' });
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
