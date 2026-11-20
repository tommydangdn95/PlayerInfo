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
  /** Importance 4-5, as judged by Gemini. */
  isHot: boolean;
  /** 1 (minor) .. 5 (must-know); 0 = not processed by Gemini yet. */
  importance: number;
  claimType: ClaimType | null;
  /** 1 (most reliable) .. 3, 0 = junk. */
  sourceTier: number;
  storyId: number | null;
  /** Teams the article is about: linked directly or through a linked player. */
  teams: NewsTeam[];
}

export type ClaimType = 'Official' | 'Report' | 'Rumour' | 'Opinion';
export type Confidence = 'Official' | 'Confirmed' | 'Rumour' | 'Opinion';

/** The same news reported by one or more sources, grouped by Gemini. */
export interface Story {
  id: number;
  headlineVi: string;
  summaryVi: string | null;
  topic: string;
  importance: number;
  isHot: boolean;
  confidence: Confidence;
  sourceCount: number;
  bestTier: number;
  firstSeenUtc: string;
  lastUpdatedUtc: string;
}

/** A tracked team with the day's most important stories (GET /api/stories/by-team). */
export interface TeamStories {
  id: number;
  name: string;
  logoUrl: string | null;
  leagueSlug: string | null;
  storyCount: number;
  hotCount: number;
  /** Articles of the day not grouped into a story yet (Gemini pending or unavailable). */
  pendingCount: number;
  stories: Story[];
}

export interface ArticleRef {
  id: number;
  title: string;
  sourceName: string;
  sourceTier: number;
  claimType: ClaimType | null;
  url: string;
  publishedAtUtc: string;
}

export interface MatchInfo {
  kickoffUtc: string;
  league: string;
  home: string;
  away: string;
  homeGoals: number | null;
  awayGoals: number | null;
  status: string;
}

export interface Briefing {
  team: { id: number; name: string; logoUrl: string | null; leagueSlug: string | null };
  day: string;
  lastMatch: MatchInfo | null;
  nextMatch: MatchInfo | null;
  injuries: { playerName: string; reason: string; status: string | null; reportedDateUtc: string }[];
  transfers: { playerName: string; from: string; to: string; type: string | null; dateUtc: string; incoming: boolean }[];
  stories: { story: Story; articleCount: number; articles: ArticleRef[] }[];
  pendingCount: number;
  pendingArticles: ArticleRef[];
}

export interface NewsSourceRule {
  id: number;
  pattern: string;
  tier: number;
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

/** News topics, as tagged by Gemini (same list as the API's NewsTaxonomy.Topics). */
export const NEWS_CATEGORIES: { value: string; label: string; emoji: string }[] = [
  { value: 'Transfer', label: 'Chuyển nhượng', emoji: '🔁' },
  { value: 'Injury', label: 'Chấn thương & lực lượng', emoji: '🏥' },
  { value: 'Match', label: 'Trận đấu', emoji: '⚽' },
  { value: 'PreMatch', label: 'Trước trận', emoji: '📋' },
  { value: 'Manager', label: 'HLV & ban huấn luyện', emoji: '👔' },
  { value: 'Turmoil', label: 'Nội bộ & khủng hoảng', emoji: '⚠️' },
  { value: 'Discipline', label: 'Kỷ luật', emoji: '🟥' },
  { value: 'Club', label: 'CLB & tài chính', emoji: '💰' },
  { value: 'Other', label: 'Khác', emoji: '📰' },
];

const LEGACY_TOPICS: Record<string, string> = { MatchPerformance: 'Match', OffPitch: 'Other' };

function topicMeta(topic: string) {
  const value = LEGACY_TOPICS[topic] ?? topic;
  return NEWS_CATEGORIES.find((c) => c.value === value) ?? NEWS_CATEGORIES[NEWS_CATEGORIES.length - 1];
}

export function categoryLabel(category: string): string {
  return topicMeta(category).label;
}

export function topicEmoji(topic: string): string {
  return topicMeta(topic).emoji;
}

export const CONFIDENCE: Record<Confidence, { label: string; emoji: string; className: string }> = {
  Official: { label: 'Chính thức', emoji: '✅', className: 'conf-official' },
  Confirmed: { label: 'Nhiều nguồn uy tín', emoji: '🟢', className: 'conf-confirmed' },
  Rumour: { label: 'Tin đồn', emoji: '🟠', className: 'conf-rumour' },
  Opinion: { label: 'Bình luận', emoji: '💬', className: 'conf-opinion' },
};

export function confidenceMeta(confidence: string) {
  return CONFIDENCE[confidence as Confidence] ?? CONFIDENCE.Rumour;
}

export const CLAIM_LABELS: Record<ClaimType, string> = {
  Official: 'Chính thức',
  Report: 'Đưa tin',
  Rumour: 'Tin đồn',
  Opinion: 'Bình luận',
};

export const TIERS: { value: number; label: string; hint: string }[] = [
  { value: 1, label: 'Hạng 1', hint: 'Hãng tin, đài lớn, cơ quan chính thức' },
  { value: 2, label: 'Hạng 2', hint: 'Báo lớn, báo chuyên, báo địa phương uy tín' },
  { value: 3, label: 'Hạng 3', hint: 'Báo lá cải, trang tổng hợp' },
  { value: 0, label: 'Rác', hint: 'Cá cược, câu view: ẩn đi' },
];

export function tierLabel(tier: number): string {
  return TIERS.find((t) => t.value === tier)?.label ?? `Hạng ${tier}`;
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
