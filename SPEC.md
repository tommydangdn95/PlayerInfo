# SPEC.md — PlayerInfo Project Specification

> **Purpose of this file:** Machine- and human-readable specification describing what this repository does, its architecture, constraints, API contracts, data model, and rules for any AI coding assistant working in this codebase.

---

## 1. Project Overview

**PlayerInfo** is a zero-cost, automated football (soccer) data and news aggregation backend built on **.NET 9 (ASP.NET Core Web API)**.

It runs as a Dockerized web service on **Render (Free Tier)** backed by **Neon Serverless PostgreSQL (Free Tier)** and is triggered 5 times daily via **`cron-job.org`** at peak European/global football hours (UTC+7).

### Core Goals
1. Sync accurate structured football data (injuries, transfers, match fixtures/results, team rosters) without exceeding free-tier API limits.
2. Aggregate daily football news for tracked teams and players from free RSS feeds (Google News RSS, BBC Sport, ESPN, VnExpress) and NewsData.io.
3. Deduplicate articles using normalized URL SHA-256 hashes.
4. Batch unprocessed articles to **Gemini API** (JSON mode) to filter clickbait/spam, categorize topics, link entities, and generate concise Vietnamese summaries (`SummaryVi`).
5. Optionally send daily digests to a **Telegram** chat and expose REST endpoints for querying news, injuries, transfers, and managing the watchlist.

---

## 2. Tech Stack & Runtime Environment

- **Framework:** `.NET 9.0` (`Microsoft.NET.Sdk.Web`)
- **Database:** PostgreSQL on **Neon** via `Npgsql.EntityFrameworkCore.PostgreSQL` (`9.0.4`) and `EF Core` (`9.0.10`)
- **HTTP Resilience:** `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler`)
- **RSS & HTML Parsing:** `System.ServiceModel.Syndication` (`9.0.0`), `HtmlAgilityPack` (`1.13.1`)
- **OpenAPI:** `Microsoft.AspNetCore.OpenApi` (`MapOpenApi()`)
- **Hosting:** Docker (`Dockerfile`) on Render Free Web Service
- **Scheduler:** External HTTP triggers from `cron-job.org` (no in-process scheduler like Quartz/Hangfire)
- **Testing:** `xUnit` + `Microsoft.EntityFrameworkCore.InMemory` (`tests/PlayerInfo.UnitTests`)

---

## 3. Repository Layout

```text
PlayerInfo/
├── Dockerfile                          # Multi-stage .NET 9 build & runtime image
├── PlayerInfo.sln
├── README.md                           # Deployment & setup instructions
├── SPEC.md                             # This specification
├── src/PlayerInfo.Api/
│   ├── Program.cs                      # DI wiring, PORT binding, EnsureCreatedAsync(), OpenAPI
│   ├── appsettings.json                # Non-secret defaults & RSS feed list
│   ├── Configuration/
│   │   └── CollectorOptions.cs         # ApiKeysOptions, CronOptions, TelegramOptions, CollectorOptions
│   ├── Background/
│   │   └── JobRunnerService.cs         # Bounded Channel<JobRequest> + BackgroundService worker
│   ├── Controllers/
│   │   ├── HealthController.cs         # GET /api/health
│   │   ├── JobsController.cs           # POST /api/jobs/run, GET /api/jobs/quota
│   │   ├── NewsController.cs           # GET /api/news, /api/news/injuries, /api/news/transfers
│   │   └── WatchlistController.cs      # CRUD for tracked teams & search keyword aliases
│   ├── Data/
│   │   ├── AppDbContext.cs             # EF Core DbContext + indexes/constraints
│   │   └── Entities/
│   │       ├── FootballEntities.cs     # Team, Player, WatchlistAlias
│   │       ├── NewsEntities.cs         # NewsArticle, ArticleEntity
│   │       └── MatchAndQuotaEntities.cs# PlayerInjury, PlayerTransfer, Fixture, ApiQuotaLog
│   ├── Clients/
│   │   ├── ApiFootballClient.cs        # v3.api-sports.io client (quota-guarded)
│   │   ├── RssNewsClient.cs            # Google News RSS & direct sports RSS parser
│   │   ├── NewsDataClient.cs           # newsdata.io client (quota-guarded)
│   │   ├── GeminiEnricherClient.cs     # Batched Gemini JSON enrichment
│   │   └── TelegramNotifier.cs         # Optional Telegram Bot API digest sender
│   └── Services/
│       ├── ConnectionStringHelper.cs   # Converts Neon postgresql:// URI -> Npgsql key-value string
│       ├── Deduplicator.cs             # Strips tracking query params & computes SHA-256 UrlHash
│       ├── QuotaManager.cs             # Reads/increments ApiQuotaLogs per provider per UTC day
│       └── CollectorPipeline.cs        # Orchestrates Full, TransfersNews, News, and Roster modes
└── tests/PlayerInfo.UnitTests/
    ├── Helpers/TestHelpers.cs
    ├── ClientsTests.cs
    ├── CollectorPipelineTests.cs
    ├── ConnectionStringHelperTests.cs
    ├── ControllersTests.cs
    ├── DeduplicatorTests.cs
    └── QuotaManagerTests.cs
```

---

## 4. Pipeline Modes & Daily Schedule

### Execution Flow (Cold-Start Safe)
Render Free spins down after 15 minutes of inactivity (30–60s cold start), while `cron-job.org` has a ~30s HTTP timeout. Therefore:
1. `cron-job.org` sends `GET /api/health` 2 minutes before each job to wake the container.
2. `cron-job.org` sends `POST /api/jobs/run?mode={mode}` with header `X-Cron-Secret`.
3. `JobsController` validates the secret and enqueues a `JobRequest` into `JobQueue` (`Channel<JobRequest>`), returning **`202 Accepted`** immediately.
4. `JobRunnerService` executes `CollectorPipeline.RunAsync(mode)` in the background.

### Supported Modes (`CollectorMode`)
| Mode query param | Enum | Tasks Executed |
| :--- | :--- | :--- |
| `full` | `CollectorMode.Full` | Sync injuries (by league) + fixtures (yesterday/today) + transfers (tracked teams) + news ingestion + Gemini AI batching + Telegram digest |
| `transfers_news` | `CollectorMode.TransfersNews` | Sync transfers (tracked teams, last 7 days) + news ingestion + Gemini AI batching + Telegram digest |
| `news` | `CollectorMode.News` | News ingestion (Google News RSS + Sports RSS + NewsData.io) + Gemini AI batching + Telegram digest |
| `roster` | `CollectorMode.Roster` | Manual/on-demand sync of `Players` squads for tracked `Teams` |

### Target Daily Schedule (UTC+7 / Asia/Ho_Chi_Minh)
- **00:00** -> `full` (after early EU matches / end of EU workday)
- **03:00** -> `news` (prime time for Champions League / late EU matches)
- **06:00** -> `full` (overnight match results, injuries, transfers, morning news)
- **18:00** -> `transfers_news` (EU midday press conferences & transfer news)
- **21:00** -> `transfers_news` (pre-match lineups & afternoon EU transfer news)

*(12:00 and 15:00 UTC+7 are intentionally skipped because Europe/US are overnight. Sunday roster cron is intentionally omitted; `roster` is triggered manually when needed.)*

---

## 5. Free-Tier Guardrails & Quotas

| Provider | Free Limit | Safety Cap in App | Enforcement Strategy |
| :--- | :--- | :--- | :--- |
| **API-Football** | 100 req/day | `90/day` (`ApiFootballDailyLimit`) | Query by `league` or `date` instead of per-player; `QuotaManager` blocks calls once `>= 90` in `ApiQuotaLogs`. |
| **NewsData.io** | 200 credits/day | `180/day` (`NewsDataDailyLimit`) | Single combined query per run; `QuotaManager` blocks calls once `>= 180`. |
| **Google News RSS** | Unofficial / IP throttle risk | 1.2–2.5s random jitter | Group aliases per entity with `OR` + `when:1d`; delay between requests. |
| **Gemini API** | Free RPM/RPD limits | Batch size `15` (`GeminiBatchSize`) | Up to 45 pending articles processed per run (3 batches) using structured JSON output. |
| **Neon Postgres** | 1 GB storage, 100 CU-hrs/mo | Store metadata + short `SummaryVi` only | Never store full article HTML bodies or binary images. No `JobExecutionLogs` table (detailed logs go to `stdout` for Render Logs). |

---

## 6. Database Schema (`AppDbContext`)

Tables auto-created on startup via `db.Database.EnsureCreatedAsync()`:

- **`Teams`**: `Id`, `ExternalApiId` (unique), `Name`, `Country`, `LogoUrl`, `LeagueExternalId`, `IsTracked`, `UpdatedAtUtc`
- **`Players`**: `Id`, `ExternalApiId` (unique), `TeamId` (FK nullable), `FullName`, `Position`, `Number`, `PhotoUrl`, `IsTracked`, `UpdatedAtUtc`
- **`WatchlistAliases`**: `Id`, `EntityType` (`Team` | `Player`), `EntityId`, `Keyword`, `Language` (`en` | `vi`)
- **`NewsArticles`**: `Id`, `UrlHash` (SHA-256 unique), `Url`, `Title`, `Snippet`, `SourceName`, `Language`, `SummaryVi`, `Category` (`Injury` | `Transfer` | `MatchPerformance` | `OffPitch` | `Other`), `IsRelevant`, `IsAiProcessed`, `IsNotified`, `PublishedAtUtc`, `FetchedAtUtc`
- **`ArticleEntities`**: Composite PK `(ArticleId, EntityType, EntityId)` linking articles to `Teams` or `Players`
- **`PlayerInjuries`**: `Id`, `PlayerExternalId`, `PlayerName`, `TeamExternalId`, `TeamName`, `Reason`, `Type`, `ReportedDateUtc`, `FetchedAtUtc`
- **`PlayerTransfers`**: `Id`, `PlayerExternalId`, `PlayerName`, `FromTeamExternalId`, `FromTeamName`, `ToTeamExternalId`, `ToTeamName`, `TransferType`, `TransferDateUtc`, `FetchedAtUtc`
- **`Fixtures`**: `Id`, `ExternalFixtureId` (unique), `LeagueExternalId`, `LeagueName`, `HomeTeamName`, `AwayTeamName`, `HomeGoals`, `AwayGoals`, `StatusShort`, `KickoffAtUtc`, `UpdatedAtUtc`
- **`ApiQuotaLogs`**: `Id`, `Provider`, `LogDate` (`DateOnly`), `RequestCount` — unique on `(Provider, LogDate)`

---

## 7. HTTP API Endpoints

| Method | Path | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/health` | None | Probes DB connectivity; used as Render wake-up ping. |
| `POST` | `/api/jobs/run?mode={mode}` | `X-Cron-Secret` (if configured) | Enqueues pipeline job and returns `202 Accepted`. |
| `GET` | `/api/jobs/quota` | None | Returns today's UTC usage for `ApiFootball` and `NewsData`. |
| `GET` | `/api/news?category=&page=1&pageSize=20` | None | Paginated relevant news articles ordered by `PublishedAtUtc` desc. |
| `GET` | `/api/news/injuries?limit=30` | None | Latest tracked player injuries. |
| `GET` | `/api/news/transfers?limit=30` | None | Latest tracked player transfers. |
| `GET` | `/api/watchlist/teams` | None | List tracked teams. |
| `POST` | `/api/watchlist/teams` | None | Add a team (`externalApiId`, `name`, `country`, `leagueExternalId`). |
| `GET` | `/api/watchlist/aliases` | None | List search keyword aliases. |
| `POST` | `/api/watchlist/aliases` | None | Add a keyword alias (`entityType`, `entityId`, `keyword`, `language`). |
| `DELETE` | `/api/watchlist/aliases/{id}` | None | Remove a keyword alias. |

---

## 8. Configuration & Environment Variables

Secrets must never be committed to `appsettings.json`. Set via Render Environment Variables:
- `ConnectionStrings__DefaultConnection` (or `DATABASE_URL`) — supports both `Host=...` and Neon's `postgresql://...` URI via `ConnectionStringHelper.Normalize`.
- `PORT` — automatically bound by `Program.cs` when set by Render.
- `Cron__SecretKey`
- `ApiKeys__ApiFootball`
- `ApiKeys__NewsData` (optional)
- `ApiKeys__Gemini` (optional; if omitted, articles are marked `IsAiProcessed = true` without AI summary)
- `Telegram__BotToken` & `Telegram__ChatId` (optional)

---

## 9. Guidelines for AI Coding Assistants

1. **Preserve Quota Guards:** Any new endpoint call to `API-Football` or `NewsData.io` must check `QuotaManager.CanConsumeAsync` before sending and call `QuotaManager.IncrementAsync` afterward. Never loop per-player over `API-Football`.
2. **Preserve Cold-Start Resilience:** Do not make `POST /api/jobs/run` synchronous; keep `JobQueue` fire-and-forget (`202 Accepted`) and keep `EnableRetryOnFailure` on `Npgsql`.
3. **Keep DB Footprint Lean:** Do not persist full article HTML or verbose debug logs in PostgreSQL. Application logs go to `ILogger` (`stdout`).
4. **Build & Test Command:**
   ```bash
   dotnet test --no-restore -p:UseSharedCompilation=false -m:1
   ```
   *(All unit tests use `EF Core InMemory` and `StubHttpMessageHandler` — no live DB or external API keys required.)*
