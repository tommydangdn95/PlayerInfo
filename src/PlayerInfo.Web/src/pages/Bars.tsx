import { useState } from 'react';

export const fmt = (n: number) => n.toLocaleString('vi-VN');

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * Single-series bar chart with a hover readout (no legend: the title names the series).
 * An optional dashed line marks a limit.
 */
export function Bars({
  values,
  labels,
  detail,
  limit,
  height = 140,
  defaultIndex,
  axis,
}: {
  values: number[];
  labels: string[];
  detail: (index: number) => string;
  limit?: number;
  height?: number;
  /** Bar described when nothing is hovered (default: the last one). */
  defaultIndex?: number;
  /** Labels under the first and last bar. */
  axis?: [string, string];
}) {
  const [hover, setHover] = useState<number | null>(null);
  const max = Math.max(1, limit ?? 0, ...values);
  const shown = hover ?? defaultIndex ?? values.length - 1;

  return (
    <div>
      <div className="bars-readout small">
        <strong>{labels[shown]}</strong> · {detail(shown)}
      </div>
      <div className="bars" style={{ height }} onMouseLeave={() => setHover(null)}>
        {limit !== undefined && (
          <div className="bars-limit" style={{ bottom: `${(limit / max) * 100}%` }}>
            <span>giới hạn {fmt(limit)}</span>
          </div>
        )}
        {values.map((v, i) => (
          <div
            key={i}
            className={`bar-col ${i === shown ? 'bar-active' : ''}`}
            onMouseEnter={() => setHover(i)}
            title={`${labels[i]}: ${detail(i)}`}
          >
            <div className="bar" style={{ height: `${(v / max) * 100}%` }} />
          </div>
        ))}
      </div>
      {axis && (
        <div className="bars-axis small muted">
          <span>{axis[0]}</span>
          <span>{axis[1]}</span>
        </div>
      )}
    </div>
  );
}

/** used / limit bar; nothing when there is no limit. */
export function LimitBar({ used, limit }: { used: number; limit: number }) {
  if (limit <= 0) return null;
  const pct = Math.min(100, Math.round((used / limit) * 100));
  const level = pct >= 90 ? 'danger' : pct >= 70 ? 'warn' : 'ok';
  return (
    <div className="quota-track">
      <div className={`quota-fill quota-${level}`} style={{ width: `${pct}%` }} />
    </div>
  );
}

export const OUTCOMES: Record<string, { label: string; className: string }> = {
  Success: { label: '✓ Thành công', className: 'outcome-ok' },
  RateLimited: { label: '⛔ Hết lượt (429)', className: 'outcome-bad' },
  Overloaded: { label: '⏳ Quá tải (503)', className: 'outcome-warn' },
  NotFound: { label: '✕ Không tồn tại (404)', className: 'outcome-bad' },
  ClientError: { label: '✕ Lỗi yêu cầu (4xx)', className: 'outcome-bad' },
  ServerError: { label: '✕ Lỗi máy chủ (5xx)', className: 'outcome-bad' },
  Timeout: { label: '⏱ Quá thời gian', className: 'outcome-warn' },
  NetworkError: { label: '✕ Lỗi mạng', className: 'outcome-bad' },
};

export function Outcome({ outcome, status }: { outcome: string; status?: number | null }) {
  const meta = OUTCOMES[outcome];
  return (
    <span className={`nowrap ${meta?.className ?? ''}`}>
      {meta?.label ?? outcome}
      {status && outcome !== 'Success' && !meta?.label.includes(String(status)) ? ` ${status}` : ''}
    </span>
  );
}
