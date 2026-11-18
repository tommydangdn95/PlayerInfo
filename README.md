# PlayerInfo — Hệ Thống Thu Thập Tin Tức Cầu Thủ & Đội Bóng (.NET 9 + Render + Neon)

Hệ thống tự động thu thập dữ liệu bóng đá (chấn thương, chuyển nhượng, lịch thi đấu) và tin tức báo chí, lọc trùng lặp, tóm tắt tiếng Việt bằng Gemini AI và lưu trữ trên **Neon Serverless PostgreSQL**.

## 1. Cấu trúc dự án

- `src/PlayerInfo.Api`: ASP.NET Core 9 Web API + Background Queue (`Channel<JobRequest>`) + Clients (`ApiFootballClient`, `RssNewsClient`, `NewsDataClient`, `GeminiEnricherClient`, `TelegramNotifier`).
- `tests/PlayerInfo.UnitTests`: Unit tests cho `Deduplicator` và `QuotaManager`.
- `Dockerfile`: Multi-stage build để deploy trực tiếp lên **Render Free Web Service**.

## 2. Biến môi trường cần cấu hình trên Render

Trong **Render Dashboard -> Web Service -> Environment Variables**, thêm các biến sau:

| Biến môi trường | Mô tả |
| :--- | :--- |
| `ConnectionStrings__DefaultConnection` | Connection string từ **Neon** (có `SslMode=Require;`) |
| `Cron__SecretKey` | Chuỗi bí mật tự đặt (dùng cho header `X-Cron-Secret`) |
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

## 4. Thêm đội bóng / từ khóa theo dõi

Sau khi deploy (hoặc chạy local), gọi API để thêm CLB và từ khóa tìm kiếm:

```bash
# 1. Thêm đội bóng (ExternalApiId lấy từ API-Football, VD: 33 = Manchester United)
POST /api/watchlist/teams
{ "externalApiId": 33, "name": "Manchester United", "country": "England", "leagueExternalId": 39 }

# 2. Thêm từ khóa tìm kiếm cho Google News RSS & lọc RSS
POST /api/watchlist/aliases
{ "entityType": "Team", "entityId": 1, "keyword": "Manchester United", "language": "en" }

POST /api/watchlist/aliases
{ "entityType": "Team", "entityId": 1, "keyword": "Man Utd", "language": "vi" }
```

## 5. Chạy test local

```bash
dotnet test -p:UseSharedCompilation=false -m:1
```
