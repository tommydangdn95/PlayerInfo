import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage } from '../api';
import { formatDateTime } from '../types';
import { fmt, formatBytes, LimitBar } from './Bars';

interface TableInfo {
  name: string;
  rows: number;
  deadRows: number | null;
  totalBytes: number | null;
  tableBytes: number | null;
  indexBytes: number | null;
  lastVacuumUtc: string | null;
  lastAnalyzeUtc: string | null;
}

interface DatabaseInfo {
  provider: string;
  version: string | null;
  databaseName: string | null;
  host: string | null;
  sizeBytes: number | null;
  limitBytes: number;
  articleRetentionDays: number;
  apiCallRetentionDays: number;
  appliedMigrations: string[];
  pendingMigrations: string[];
  tables: TableInfo[];
  growth: {
    articlesLast7Days: number;
    articlesPerDay: number;
    bytesPerArticle: number | null;
    projectedNewsBytes: number | null;
    projectedTotalBytes: number | null;
    oldestArticleUtc: string | null;
    newestArticleUtc: string | null;
  };
  error: string | null;
}

/** What each table holds and how long it keeps it. */
const TABLES: Record<string, { label: string; keep: string }> = {
  NewsArticles: { label: 'Bài báo', keep: 'xóa sau {articles} ngày' },
  ArticleEntities: { label: 'Tag đội/cầu thủ của bài', keep: 'xóa cùng bài' },
  Stories: { label: 'Câu chuyện (bài gộp)', keep: 'xóa khi hết bài' },
  ApiCalls: { label: 'Thống kê lệnh gọi API', keep: 'xóa sau {calls} ngày' },
  LeagueTeams: { label: 'Danh sách đội theo giải (ESPN)', keep: 'thay mới mỗi mùa' },
  Teams: { label: 'Đội đang theo dõi', keep: 'giữ' },
  Players: { label: 'Cầu thủ', keep: 'giữ' },
  WatchlistAliases: { label: 'Từ khóa tìm tin', keep: 'giữ' },
  NewsSources: { label: 'Hạng nguồn tin', keep: 'giữ' },
  Fixtures: { label: 'Lịch đấu', keep: 'giữ' },
  PlayerInjuries: { label: 'Chấn thương', keep: 'giữ' },
  PlayerTransfers: { label: 'Chuyển nhượng', keep: 'giữ' },
  ApiQuotaLogs: { label: 'Bộ đếm quota API-Football/NewsData', keep: 'giữ (1 dòng/ngày)' },
  __EFMigrationsHistory: { label: 'Lịch sử migration', keep: 'giữ' },
};

/** Database size against the Neon free tier, per-table sizes and where news storage is heading. */
export function DatabasePage() {
  const [info, setInfo] = useState<DatabaseInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setInfo(await api<DatabaseInfo>('/api/database'));
    } catch (err) {
      setError(errorMessage(err));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (error) return <div className="alert alert-error">{error}</div>;
  if (!info) return <p className="muted">Đang tải…</p>;

  const g = info.growth;
  const isPostgres = info.provider === 'PostgreSQL' && info.sizeBytes !== null;
  const pct = info.sizeBytes !== null ? Math.round((info.sizeBytes / info.limitBytes) * 1000) / 10 : null;
  const projectedPct = g.projectedTotalBytes !== null ? Math.round((g.projectedTotalBytes / info.limitBytes) * 100) : null;
  const total = info.tables.reduce((s, t) => s + (t.totalBytes ?? 0), 0);
  const keep = (name: string) =>
    (TABLES[name]?.keep ?? '')
      .replace('{articles}', String(info.articleRetentionDays))
      .replace('{calls}', String(info.apiCallRetentionDays));

  return (
    <div className="stack">
      <div className="page-head">
        <h2>Cơ sở dữ liệu</h2>
        <button className="btn btn-ghost" onClick={() => void load()}>
          Làm mới
        </button>
      </div>

      {info.error && <div className="alert alert-error">{info.error}</div>}
      {info.provider === 'InMemory' && (
        <div className="alert alert-warn">
          Đang chạy DB tạm trong bộ nhớ (local): chỉ có số dòng, không có dung lượng. Trên Render trang này đọc trực tiếp từ Neon.
        </div>
      )}
      {info.pendingMigrations.length > 0 && (
        <div className="alert alert-warn">Còn {info.pendingMigrations.length} migration chưa chạy: {info.pendingMigrations.join(', ')}</div>
      )}

      <section className="stat-grid">
        <div className="card stat">
          <div className="muted small">Dung lượng đang dùng</div>
          <div className="stat-value">
            {info.sizeBytes !== null ? formatBytes(info.sizeBytes) : '—'}
            <span className="stat-sub"> / {formatBytes(info.limitBytes)}</span>
          </div>
          {info.sizeBytes !== null && <LimitBar used={info.sizeBytes} limit={info.limitBytes} />}
          <div className="muted small">{pct !== null ? `${pct}% gói miễn phí Neon` : 'Không có số liệu dung lượng'}</div>
        </div>
        <div className="card stat">
          <div className="muted small">Bài mới mỗi ngày</div>
          <div className="stat-value">{fmt(g.articlesPerDay)}</div>
          <div className="muted small">{fmt(g.articlesLast7Days)} bài trong 7 ngày qua</div>
        </div>
        <div className="card stat">
          <div className="muted small">Ước tính khi ổn định</div>
          <div className="stat-value">{g.projectedTotalBytes !== null ? formatBytes(g.projectedTotalBytes) : '—'}</div>
          <div className="muted small">
            {projectedPct !== null
              ? `${projectedPct}% giới hạn · tin giữ ${info.articleRetentionDays} ngày, ~${formatBytes(g.bytesPerArticle ?? 0)}/bài`
              : 'Cần dữ liệu trên Neon'}
          </div>
        </div>
        <div className="card stat">
          <div className="muted small">Tin cũ nhất đang lưu</div>
          <div className="stat-value small-value">{g.oldestArticleUtc ? formatDateTime(g.oldestArticleUtc) : '—'}</div>
          <div className="muted small">Mới nhất: {g.newestArticleUtc ? formatDateTime(g.newestArticleUtc) : '—'}</div>
        </div>
      </section>

      <section className="card">
        <h3>Các bảng</h3>
        <div className="table-scroll">
          <table className="table">
            <thead>
              <tr>
                <th>Bảng</th>
                <th>Giữ bao lâu</th>
                <th className="num">Số dòng</th>
                {isPostgres && (
                  <>
                    <th className="num">Dữ liệu</th>
                    <th className="num">Chỉ mục</th>
                    <th className="num">Tổng</th>
                    <th>Tỉ lệ</th>
                    <th className="num">Dòng chết</th>
                    <th>Dọn gần nhất</th>
                  </>
                )}
              </tr>
            </thead>
            <tbody>
              {info.tables.map((t) => (
                <tr key={t.name}>
                  <td>
                    {TABLES[t.name]?.label ?? t.name}
                    <div className="muted small">
                      <code>{t.name}</code>
                    </div>
                  </td>
                  <td className="small">{keep(t.name)}</td>
                  <td className="num">{fmt(t.rows)}</td>
                  {isPostgres && (
                    <>
                      <td className="num">{formatBytes(t.tableBytes ?? 0)}</td>
                      <td className="num">{formatBytes(t.indexBytes ?? 0)}</td>
                      <td className="num">
                        <strong>{formatBytes(t.totalBytes ?? 0)}</strong>
                      </td>
                      <td className="share-cell">
                        <LimitBar used={t.totalBytes ?? 0} limit={Math.max(1, total)} />
                        <span className="small muted">{total > 0 ? Math.round(((t.totalBytes ?? 0) / total) * 100) : 0}%</span>
                      </td>
                      <td className="num">{fmt(t.deadRows ?? 0)}</td>
                      <td className="small nowrap">{t.lastVacuumUtc ? formatDateTime(t.lastVacuumUtc) : '—'}</td>
                    </>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted small">
          Số dòng trên Neon là ước tính của Postgres (cập nhật khi Postgres tự phân tích bảng). "Dòng chết" là dòng đã xóa/sửa,
          chờ Postgres tự dọn để dùng lại chỗ trống; dung lượng không giảm ngay sau khi xóa nhưng sẽ được dùng lại cho dữ liệu mới.
          Neon tính dung lượng gói theo dữ liệu thật cộng phần lịch sử để khôi phục, nên con số trên trang Neon có thể cao hơn một chút.
        </p>
      </section>

      {isPostgres && (
        <section className="card">
          <h3>Thông tin kết nối</h3>
          <ul className="plain-list small">
            <li>
              Máy chủ: <code>{info.host ?? '—'}</code> · CSDL: <code>{info.databaseName ?? '—'}</code>
            </li>
            <li>Phiên bản: {info.version ?? '—'}</li>
            <li>
              Migration đã chạy ({info.appliedMigrations.length}): <span className="muted">{info.appliedMigrations.join(', ')}</span>
            </li>
            <li className="muted">
              Đổi giới hạn hiển thị bằng <code>Database__StorageLimitMb</code> (mặc định 1024); đổi số ngày giữ tin bằng{' '}
              <code>Collector__ArticleRetentionDays</code>.
            </li>
          </ul>
        </section>
      )}
    </div>
  );
}
