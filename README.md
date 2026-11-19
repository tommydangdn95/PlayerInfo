# PlayerInfo — Hệ Thống Thu Thập Tin Tức Cầu Thủ & Đội Bóng (.NET 9 + Render + Neon)

Hệ thống tự động thu thập dữ liệu bóng đá (chấn thương, chuyển nhượng, lịch thi đấu) và tin tức báo chí, lọc trùng lặp, tóm tắt tiếng Việt bằng Gemini AI và lưu trữ trên **Neon Serverless PostgreSQL**.

## 1. Cấu trúc dự án

- `src/PlayerInfo.Api`: ASP.NET Core 9 Web API + Background Queue (`Channel<JobRequest>`) + Clients (`ApiFootballClient`, `RssNewsClient`, `NewsDataClient`, `GeminiEnricherClient`, `TelegramNotifier`) + đăng nhập JWT.
- `src/PlayerInfo.Web`: Trang quản trị (React + Vite): đăng nhập, tổng quan (quota, chạy job, tin mới), quản lý đội bóng & từ khóa.
- `tests/PlayerInfo.UnitTests`: Unit tests cho clients, controllers, filter xác thực, `CollectorPipeline`, `Deduplicator`, `QuotaManager` và cấu hình.
- Schema DB được quản lý bằng EF Core migrations (`src/PlayerInfo.Api/Data/Migrations`) và tự áp dụng khi app khởi động.
- `Dockerfile`: Multi-stage build để deploy trực tiếp lên **Render Free Web Service**.

## 2. Biến môi trường cần cấu hình trên Render

Trong **Render Dashboard -> Web Service -> Environment Variables**, thêm các biến sau:

| Biến môi trường | Mô tả |
| :--- | :--- |
| `ConnectionStrings__DefaultConnection` | Connection string từ **Neon** (có `SslMode=Require;`, hoặc URI `postgresql://...`). **Bắt buộc** khi chạy production |
| `Cron__SecretKey` | Chuỗi bí mật tự đặt (header `X-Cron-Secret`) cho cron-job.org, chỉ dùng được với `POST /api/jobs/run` |
| `Auth__GoogleClientId` | OAuth Client ID (loại *Web application*) tạo trên Google Cloud Console, dạng `xxxx.apps.googleusercontent.com` |
| `Auth__AllowedGoogleEmails` | Email Google (Gmail) được phép vào trang admin (nhiều email cách nhau dấu phẩy) |
| `Auth__AdminUsername` | Tên đăng nhập dự phòng bằng mật khẩu (mặc định `admin`) |
| `Auth__AdminPasswordHash` | Hash mật khẩu dự phòng, tạo bằng `dotnet run --project src/PlayerInfo.Api -- hash-password "mat-khau"` (để trống = tắt đăng nhập mật khẩu) |
| `Auth__JwtSigningKey` | Chuỗi ngẫu nhiên ≥ 32 ký tự để ký JWT (đổi khóa = đăng xuất mọi phiên) |
| `ApiKeys__ApiFootball` | API Key từ `api-sports.io` (giới hạn 100 req/ngày, app tự chặn ở 90) |
| `ApiKeys__NewsData` | API Key từ `newsdata.io` *(tùy chọn)* |
| `ApiKeys__Gemini` | API Key từ Google AI Studio *(tùy chọn — dùng để tóm tắt & phân loại)* |
| `Telegram__BotToken` | Token bot Telegram *(tùy chọn)* |
| `Telegram__ChatId` | Chat ID nhận bản tin *(tùy chọn)* |

## 3. Lịch chạy đề xuất trên `cron-job.org` (Giờ VN)

Với mỗi khung giờ, tạo 1 job ping đánh thức Render trước 2 phút (`GET /api/health`) và 1 job chạy chính (`POST /api/jobs/run?mode=...` kèm header `X-Cron-Secret`):

| Giờ VN | Ping đánh thức | Job chính |
| :--- | :--- | :--- |
| **00:00** | `23:58` `GET /api/health` | `00:00` `POST /api/jobs/run?mode=full` |
| **03:00** | `02:58` `GET /api/health` | `03:00` `POST /api/jobs/run?mode=news` |
| **06:00** | `05:58` `GET /api/health` | `06:00` `POST /api/jobs/run?mode=full` |
| **18:00** | `17:58` `GET /api/health` | `18:00` `POST /api/jobs/run?mode=transfers_news` |
| **21:00** | `20:58` `GET /api/health` | `21:00` `POST /api/jobs/run?mode=transfers_news` |

## 4. Trang quản trị (thêm đội bóng / từ khóa)

Mở `https://<app>.onrender.com/` → **Sign in with Google** (tài khoản có email trong `Auth__AllowedGoogleEmails`), hoặc dùng mật khẩu dự phòng.

Tạo Google Client ID (một lần): [console.cloud.google.com](https://console.cloud.google.com) → tạo project → **Google Auth Platform / OAuth consent screen** (External, thêm email của bạn làm test user) → **Credentials → Create credentials → OAuth client ID** → loại **Web application** → *Authorized JavaScript origins*: `https://<app>.onrender.com`, `http://localhost`, `http://localhost:5011`, `http://localhost:5173` → copy **Client ID** vào `Auth__GoogleClientId`.

- **Tổng quan:** quota API hôm nay, nút chạy job ngay (`news`, `transfers_news`, `full`, `roster`), tin mới nhất.
- **Đội bóng:** gõ tên đội (tiếng Anh) → **Tìm** (tốn 1 request API-Football) → **Theo dõi**. Mỗi đội có công tắc theo dõi/tạm dừng, chọn giải, thêm/xóa từ khóa tìm tin (`en`/`vi`), xóa đội.

Mọi API (trừ `/api/health` và đăng nhập) đều cần JWT. cron-job.org vẫn dùng header `X-Cron-Secret`, nhưng chỉ gọi được `POST /api/jobs/run`.

## 5. Chạy test local

```bash
dotnet test -p:UseSharedCompilation=false -m:1
```

## 6. Chạy local

```bash
# API (Development: DB InMemory, đăng nhập admin / admin)
dotnet run --project src/PlayerInfo.Api --urls http://localhost:5011

# Giao diện: chạy dev server có hot reload (proxy /api sang :5011)
cd src/PlayerInfo.Web && npm ci && npm run dev      # http://localhost:5173
# hoặc build vào wwwroot để API phục vụ luôn tại http://localhost:5011
npm run build
```

## 7. Thay đổi schema

Sau khi sửa entity hoặc `AppDbContext`:

```bash
dotnet ef migrations add <TenMigration> -p src/PlayerInfo.Api -o Data/Migrations
```

Migration được áp dụng tự động khi app khởi động.
