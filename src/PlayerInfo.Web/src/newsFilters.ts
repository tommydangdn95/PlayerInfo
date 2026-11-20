import { addDays, vnDayKey, vnDayStartUtc } from './types';

export type Range = 'today' | '3d' | '7d' | '30d' | 'all' | 'day';

export const RANGES: { value: Range; label: string }[] = [
  { value: 'today', label: 'Hôm nay' },
  { value: '3d', label: '3 ngày' },
  { value: '7d', label: '7 ngày' },
  { value: '30d', label: '30 ngày' },
  { value: 'all', label: 'Tất cả' },
  { value: 'day', label: 'Chọn ngày…' },
];

export function parseRange(value: string | null, fallback: Range): Range {
  return RANGES.some((r) => r.value === value) ? (value as Range) : fallback;
}

/** [fromUtc, toUtc) for a range, counted in Vietnam days. */
export function rangeBounds(range: Range, day: string): { from?: Date; to?: Date } {
  const today = vnDayKey(new Date());
  const tomorrow = vnDayStartUtc(addDays(today, 1));
  switch (range) {
    case 'today':
      return { from: vnDayStartUtc(today), to: tomorrow };
    case '3d':
      return { from: vnDayStartUtc(addDays(today, -2)), to: tomorrow };
    case '7d':
      return { from: vnDayStartUtc(addDays(today, -6)), to: tomorrow };
    case '30d':
      return { from: vnDayStartUtc(addDays(today, -29)), to: tomorrow };
    case 'day':
      return { from: vnDayStartUtc(day), to: vnDayStartUtc(addDays(day, 1)) };
    default:
      return {};
  }
}

/** Adds fromUtc/toUtc for the range to a query string. */
export function applyRange(query: URLSearchParams, range: Range, day: string): URLSearchParams {
  const { from, to } = rangeBounds(range, day);
  if (from) query.set('fromUtc', from.toISOString());
  if (to) query.set('toUtc', to.toISOString());
  return query;
}
