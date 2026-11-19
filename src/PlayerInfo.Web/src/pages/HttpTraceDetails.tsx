import { useMemo, useState } from 'react';

export interface HttpTrace {
  method: string;
  url: string;
  statusCode: number | null;
  durationMs: number;
  requestContentType: string | null;
  requestBody: string | null;
  requestTruncated: boolean;
  responseContentType: string | null;
  responseBytes: number | null;
  responseBody: string | null;
  responseTruncated: boolean;
  error: string | null;
}

/** Expanded view of one outbound HTTP call: request line + body, response status + body (JSON pretty-printed). */
export function HttpTraceDetails({ trace }: { trace: HttpTrace }) {
  return (
    <div className="http-details">
      <section>
        <h4>Request</h4>
        <div className="http-line">
          <span className="http-method">{trace.method}</span> <span className="http-url">{trace.url}</span>
        </div>
        <Body
          label="Body gửi đi"
          body={trace.requestBody}
          contentType={trace.requestContentType}
          truncated={trace.requestTruncated}
          empty="(không có body)"
        />
      </section>
      <section>
        <h4>Response</h4>
        <div className="http-line">
          {trace.error ? (
            <span className="http-status http-status-error">Lỗi: {trace.error}</span>
          ) : (
            <span className={`http-status ${trace.statusCode && trace.statusCode >= 400 ? 'http-status-error' : 'http-status-ok'}`}>
              {trace.statusCode}
            </span>
          )}{' '}
          <span className="muted">
            {trace.durationMs} ms
            {trace.responseBytes != null ? ` · ${formatSize(trace.responseBytes)}` : ''}
            {trace.responseContentType ? ` · ${trace.responseContentType}` : ''}
          </span>
        </div>
        <Body
          label="Body nhận về"
          body={trace.responseBody}
          contentType={trace.responseContentType}
          truncated={trace.responseTruncated}
          empty="(không có body)"
        />
      </section>
    </div>
  );
}

function Body({
  label,
  body,
  contentType,
  truncated,
  empty,
}: {
  label: string;
  body: string | null;
  contentType: string | null;
  truncated: boolean;
  empty: string;
}) {
  const [copied, setCopied] = useState(false);
  const pretty = useMemo(() => prettyPrint(body, contentType, truncated), [body, contentType, truncated]);

  if (!body) return <div className="muted http-empty">{empty}</div>;

  async function copy() {
    try {
      await navigator.clipboard.writeText(body ?? '');
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard not available (http origin) — ignore
    }
  }

  return (
    <div className="http-body">
      <div className="http-body-head">
        <span className="muted">
          {label}
          {truncated ? ' — đã cắt bớt, chỉ hiện phần đầu' : ''}
        </span>
        <button className="link-button" onClick={() => void copy()}>
          {copied ? 'Đã sao chép' : 'Sao chép'}
        </button>
      </div>
      <pre>{pretty}</pre>
    </div>
  );
}

function prettyPrint(body: string | null, contentType: string | null, truncated: boolean): string {
  if (!body) return '';
  const looksJson = (contentType ?? '').includes('json') || /^\s*[[{]/.test(body);
  if (looksJson && !truncated) {
    try {
      return JSON.stringify(JSON.parse(body), null, 2);
    } catch {
      // not valid JSON: show as is
    }
  }
  return body;
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
