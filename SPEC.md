# SPEC.md — PlayerInfo Project Specification

> **Purpose of this file:** Machine- and human-readable specification describing what this repository does, its architecture, constraints, API contracts, data model, and rules for any AI coding assistant working in this codebase.

---

## 1. Project Overview

**PlayerInfo** is a zero-cost, automated football (soccer) data and news aggregation backend built on **.NET 9 (ASP.NET Core Web API)**.

It runs as a Dockerized web service on **Render (Free Tier)** backed by **Neon Serverless PostgreSQL (Free Tier)** and is triggered 5 times daily via **`cron-job.org`** at peak European/global football hours (UTC+7).

### Core Goals
1. Sync accurate structured football data (injuries, transfers, match fixtures/results, team rosters) without exceeding free-tier API limits.
2. Aggregate daily football news for tracked teams and players from free RSS feeds (Google News RSS, BBC Sport, ESPN, VnExpress) and NewsData.io.
3. Deduplicate articles using normalized URL SHA-256 hashes plus a normalized-title key (catches the same story from Google News and the publisher's own feed).
4. Batch unprocessed articles to **Gemini API** (JSON mode) to filter clickbait/spam, categorize topics, link entities, and generate concise Vietnamese summaries (`SummaryVi`).
5. Optionally send daily digests to a **Telegram** chat and expose REST endpoints for querying news, injuries, transfers, and managing the watchlist.
6. Provide a login-protected **admin UI** (React) to manage tracked teams and keywords, watch quota usage and trigger jobs.

---

## 2. Tech Stack & Runtime Environment

- **Framework:** `.NET 9.0` (`Microsoft.NET.Sdk.Web`)
- **Database:** PostgreSQL on **Neon** via `Npgsql.EntityFrameworkCore.PostgreSQL` (`9.0.4`) and `EF Core` (`9.0.10`)
- **HTTP Resilience:** `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler`)
- **RSS & HTML Parsing:** `System.ServiceModel.Syndication` (`9.0.0`), `HtmlAgilityPack` (`1.13.1`)
- **OpenAPI:** `Microsoft.AspNetCore.OpenApi` (`MapOpenApi()`, requires admin login)
- **Auth:** JWT bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`). Admin signs in with a **Google account** (Google Identity Services button → ID token validated against Google's OIDC keys → app JWT) or, as fallback, a single username/password (ASP.NET Core Identity `PasswordHasher`, PBKDF2)
- **Admin UI:** React 19 + Vite + TypeScript (`src/PlayerInfo.Web`), built into the API's `wwwroot` and served by the same service
- **Hosting:** Docker (`Dockerfile`: Node stage builds the UI, .NET stage builds the API) on Render Free Web Service
- **Scheduler:** External HTTP triggers from `cron-job.org` (no in-process scheduler like Quartz/Hangfire)
- **Testing:** `xUnit` + `Microsoft.EntityFrameworkCore.InMemory` + `Microsoft.AspNetCore.Mvc.Testing` integration tests (`tests/PlayerInfo.UnitTests`)

---

## 3. Repository Layout

```text
PlayerInfo/
├── Dockerfile                          # Multi-stage: Node (UI) + .NET 9 build & runtime image
├── PlayerInfo.sln
├── README.md                           # Deployment & setup instructions
├── SPEC.md                             # This specification
├── src/PlayerInfo.Api/
│   ├── Program.cs                      # DI wiring, auth, PORT binding, per-client resilience, DB init, UI static files, `hash-password` command
│   ├── appsettings.json                # Non-secret defaults, tracked leagues & RSS feed list
│   ├── Configuration/
│   │   ├── AuthOptions.cs              # Admin account + JWT settings
│   │   └── CollectorOptions.cs         # ApiKeysOptions, CronOptions, TelegramOptions, CollectorOptions
│   ├── Security/
│   │   ├── AuthServiceCollectionExtensions.cs # JWT + cron schemes, fallback policy, login rate limit, forwarded headers
│   │   ├── JwtTokenService.cs          # Issues/validates admin JWTs (HS256); Roles, Policies
│   │   ├── AdminCredentialValidator.cs # Verifies the admin username/password hash
│   │   ├── GoogleIdTokenValidator.cs   # Validates Google ID tokens (issuer, audience, age, email_verified) + email allow-list
│   │   └── CronSecretAuthenticationHandler.cs # X-Cron-Secret scheme (role "cron", POST /api/jobs/run only)
│   ├── Background/
│   │   └── JobRunnerService.cs         # Bounded Channel<JobRequest> + BackgroundService worker
│   ├── Controllers/
│   │   ├── AuthController.cs           # GET /api/auth/config, POST /api/auth/google, POST /api/auth/login, GET /api/auth/me
│   │   ├── HealthController.cs         # GET /api/health
│   │   ├── JobsController.cs           # POST /api/jobs/run, GET /api/jobs/quota
│   │   ├── NewsController.cs           # GET /api/news, /api/news/injuries, /api/news/transfers
│   │   └── WatchlistController.cs      # Team search/CRUD & search keyword aliases
│   ├── Data/
│   │   ├── AppDbContext.cs             # EF Core DbContext + indexes/constraints
│   │   ├── DatabaseInitializer.cs      # Migrate() on startup (+ baseline for legacy EnsureCreated DBs), design-time factory
│   │   ├── Migrations/                 # EF Core migrations (InitialCreate, ...)
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
│       ├── Deduplicator.cs             # URL normalization + SHA-256 UrlHash, normalized title key
│       ├── QuotaManager.cs             # Reads/increments ApiQuotaLogs per provider per UTC day (own DbContext per call)
│       └── CollectorPipeline.cs        # Orchestrates Full, TransfersNews, News, Roster modes as isolated steps
├── src/PlayerInfo.Web/                 # Admin UI (React + Vite): login, dashboard, team management
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
3. `RequireCronSecretAttribute` validates the secret, then `JobsController` enqueues a `JobRequest` into `JobQueue` (`Channel<JobRequest>`), returning **`202 Accepted`** immediately.
4. `JobRunnerService` executes `CollectorPipeline.RunAsync(mode)` in the background.
5. The pipeline runs as named steps (`SyncInjuries`, `SyncFixtures`, `SyncTransfers`, `IngestNews`, `EnrichNews`, `NotifyTelegram`). A failing step is logged, its pending EF changes are discarded, and the remaining steps still run.

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
| **API-Football** | 100 req/day | `90/day` (`ApiFootballDailyLimit`) | Query by `league` or `date` instead of per-player; `QuotaManager` blocks calls once `>= 90` in `ApiQuotaLogs`. Each request is counted *before* sending and the HTTP client has no automatic retries. Daily budget is about `14 + 4N` requests (5 leagues, N tracked teams), so ~19 teams max. HTTP-200 responses with an `errors` field (plan/season limits) are logged. |
| **NewsData.io** | 200 credits/day | `180/day` (`NewsDataDailyLimit`) | Single combined query per run (max 100 chars, multi-word keywords quoted); counted before sending, no retries; `QuotaManager` blocks calls once `>= 180`. |
| **Google News RSS** | Unofficial / IP throttle risk | 1.2–2.5s random jitter | Group aliases per entity with `OR` + `when:1d`; delay between requests. |
| **Gemini API** | Free RPM/RPD limits | Batch size `15` (`GeminiBatchSize`) | Up to 45 pending articles per run (3 batches) with `responseSchema` structured output; 90s attempt timeout, 1 retry. On failure the run stops calling Gemini and articles stay pending for the next run; articles pending > 48h are given up (`IsAiProcessed = true`, `IsNotified = true`). |
| **Neon Postgres** | 1 GB storage, 100 CU-hrs/mo | Store metadata + short `SummaryVi` only | Never store full article HTML bodies or binary images. No `JobExecutionLogs` table (detailed logs go to `stdout` for Render Logs). |

---

## 6. Database Schema (`AppDbContext`)

Schema is managed by **EF Core migrations** (`Data/Migrations`), applied on startup by `DatabaseInitializer` (`Database.MigrateAsync()`). A database created by the earlier `EnsureCreated()` startup is detected and `InitialCreate` is recorded as applied. The InMemory provider (Development without a connection string) uses `EnsureCreated()`. Tables:

- **`Teams`**: `Id`, `ExternalApiId` (unique), `Name`, `Country`, `LogoUrl`, `LeagueExternalId`, `IsTracked`, `UpdatedAtUtc`
- **`Players`**: `Id`, `ExternalApiId` (unique), `TeamId` (FK nullable), `FullName`, `Position`, `Number`, `PhotoUrl`, `IsTracked`, `UpdatedAtUtc`
- **`WatchlistAliases`**: `Id`, `EntityType` (`Team` | `Player`), `EntityId` (internal `Teams.Id` / `Players.Id`), `Keyword`, `Language` (`en` | `vi`)
- **`NewsArticles`**: `Id`, `UrlHash` (SHA-256 unique), `Url`, `Title`, `Snippet`, `SourceName`, `Language`, `SummaryVi`, `Category` (`Injury` | `Transfer` | `MatchPerformance` | `OffPitch` | `Other`), `IsRelevant`, `IsAiProcessed`, `IsNotified`, `PublishedAtUtc`, `FetchedAtUtc`
- **`ArticleEntities`**: Composite PK `(ArticleId, EntityType, EntityId)` linking articles to `Teams` or `Players`
- **`PlayerInjuries`**: `Id`, `PlayerExternalId`, `PlayerName`, `TeamExternalId`, `TeamName`, `Reason`, `Type`, `ReportedDateUtc`, `FetchedAtUtc`
- **`PlayerTransfers`**: `Id`, `PlayerExternalId`, `PlayerName`, `FromTeamExternalId`, `FromTeamName`, `ToTeamExternalId`, `ToTeamName`, `TransferType`, `TransferDateUtc`, `FetchedAtUtc`
- **`Fixtures`**: `Id`, `ExternalFixtureId` (unique), `LeagueExternalId`, `LeagueName`, `HomeTeamName`, `AwayTeamName`, `HomeGoals`, `AwayGoals`, `StatusShort`, `KickoffAtUtc`, `UpdatedAtUtc`
- **`ApiQuotaLogs`**: `Id`, `Provider`, `LogDate` (`DateOnly`), `RequestCount` — unique on `(Provider, LogDate)`

---

## 7. HTTP API Endpoints

**Authentication model**
- Every endpoint requires an **admin JWT** (`Authorization: Bearer <token>`) through the authorization *fallback policy*, unless marked otherwise below.
- Tokens come from `POST /api/auth/google` (Google account whose verified email is in `Auth:AllowedGoogleEmails`) or `POST /api/auth/login` (fallback admin password), are signed with HS256 and expire after `Auth:TokenLifetimeHours` (default 12h). Login is rate-limited per client IP (`Auth:LoginAttemptsPerMinute`, default 5).
- `X-Cron-Secret` is a separate authentication scheme for machines (cron-job.org). Its principal has role `cron` and is accepted **only** by `POST /api/jobs/run`. If `Cron:SecretKey` is empty the header is never accepted.

| Method | Path | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/health` | Public | Probes DB connectivity; used as Render wake-up ping. |
| `GET` | `/api/auth/config` | Public | Sign-in methods for the login page: `{ passwordLogin, google: { clientId } | null }`. |
| `POST` | `/api/auth/google` | Public (rate-limited) | `{ credential }` (Google ID token from the Sign-in button) → `{ accessToken, expiresAtUtc, username }`. Token must be signed by Google, issued to `Auth:GoogleClientId`, < 10 min old, with `email_verified` and an allowed email. `401` otherwise, `404` if not configured. |
| `POST` | `/api/auth/login` | Public (rate-limited) | Fallback: `{ username, password }` → `{ accessToken, expiresAtUtc, username }`; `401` on bad credentials, `404` if password sign-in is disabled, `429` when rate-limited. |
| `GET` | `/api/auth/me` | Admin JWT | Current admin username. |
| `POST` | `/api/jobs/run?mode={mode}` | Admin JWT **or** `X-Cron-Secret` | Enqueues pipeline job and returns `202 Accepted`. |
| `GET` | `/api/jobs/quota` | Admin JWT | Today's UTC usage and limits for `ApiFootball` and `NewsData`. |
| `GET` | `/api/news?category=&page=1&pageSize=20` | Admin JWT | Paginated relevant news articles ordered by `PublishedAtUtc` desc. |
| `GET` | `/api/news/injuries?limit=30` | Admin JWT | Latest tracked player injuries. |
| `GET` | `/api/news/transfers?limit=30` | Admin JWT | Latest tracked player transfers. |
| `GET` | `/api/watchlist/teams` | Admin JWT | All teams, each with its keyword aliases. |
| `GET` | `/api/watchlist/teams/{id}` | Admin JWT | One team with its aliases. |
| `GET` | `/api/watchlist/search-teams?q=` | Admin JWT | Searches API-Football teams by name (≥ 3 letters/digits/spaces; costs 1 quota request); marks teams already added. |
| `GET` | `/api/watchlist/leagues` | Admin JWT | Tracked league ids (`Collector:TrackedLeagueIds`). |
| `POST` | `/api/watchlist/teams` | Admin JWT | Add a team (`externalApiId`, `name`, `country`, `leagueExternalId`, `logoUrl`). `409` if `externalApiId` exists. |
| `PATCH` | `/api/watchlist/teams/{id}` | Admin JWT | Partial update (`name`, `country`, `leagueExternalId` (0 clears), `isTracked`). |
| `DELETE` | `/api/watchlist/teams/{id}` | Admin JWT | Deletes the team, its aliases and article links (players keep existing, `TeamId` set null). |
| `GET` | `/api/watchlist/aliases` | Admin JWT | List search keyword aliases. |
| `POST` | `/api/watchlist/aliases` | Admin JWT | Add a keyword alias (`entityType`, `entityId`, `keyword`, `language`). `400` for unknown type/entity/language, `409` for duplicates. |
| `DELETE` | `/api/watchlist/aliases/{id}` | Admin JWT | Remove a keyword alias. |
| `GET` | `/openapi/v1.json` | Admin JWT | OpenAPI document. |
| `GET` | `/`, `/login`, `/teams`, … | Public | Admin UI (static files + SPA fallback). `index.html` is sent with `Cache-Control: no-cache`, hashed `/assets/*` as immutable. Unknown `/api/*` paths return `404`. |

---

## 8. Configuration & Environment Variables

Secrets must never be committed to `appsettings.json`. Set via Render Environment Variables:
- `ConnectionStrings__DefaultConnection` (or `DATABASE_URL`) — supports both `Host=...` and Neon's `postgresql://...` URI via `ConnectionStringHelper.Normalize`. Required outside Development (startup fails without it); in Development an InMemory database is used when it is empty.
- `PORT` — automatically bound by `Program.cs` when set by Render.
- `Cron__SecretKey` — static secret for cron-job.org (`X-Cron-Secret`), only for `POST /api/jobs/run`.
- `Auth__GoogleClientId` (OAuth 2.0 *Web application* client ID from Google Cloud Console, ending with `.apps.googleusercontent.com`; *Authorized JavaScript origins* must include the site origin) and `Auth__AllowedGoogleEmails` (comma-separated) — Google sign-in.
- `Auth__AdminUsername` (default `admin`), `Auth__AdminPasswordHash` — fallback password sign-in; leave the hash empty to disable it.
- `Auth__JwtSigningKey` (≥ 32 chars) — required. Outside Development startup fails unless the key and at least one sign-in method are configured. Generate the hash with `dotnet run --project src/PlayerInfo.Api -- hash-password "<password>"`. Changing `Auth__JwtSigningKey` logs every admin out. In Development, missing values fall back to `admin`/`admin` and a per-process random key.
- `ApiKeys__ApiFootball`
- `ApiKeys__NewsData` (optional)
- `ApiKeys__Gemini` (optional; if omitted, articles are marked `IsAiProcessed = true` without AI summary)
- `Telegram__BotToken` & `Telegram__ChatId` (optional)

`Collector:TrackedLeagueIds` and `Collector:SportsRssFeeds` live in `appsettings.json`. Their C# defaults are applied only when the configured list is empty (`CollectorOptions.ApplyDefaults`), because the configuration binder appends to initialized lists. `System.Net.Http.HttpClient` logging is set to `Warning` so request URLs (the Telegram bot token is in the path) are not logged.

---

## 9. Guidelines for AI Coding Assistants

1. **Preserve Quota Guards:** Any new endpoint call to `API-Football` or `NewsData.io` must check `QuotaManager.CanConsumeAsync` before sending and call `QuotaManager.IncrementAsync` afterward. Never loop per-player over `API-Football`.
2. **Preserve Cold-Start Resilience:** Do not make `POST /api/jobs/run` synchronous; keep `JobQueue` fire-and-forget (`202 Accepted`) and keep `EnableRetryOnFailure` on `Npgsql`.
3. **Keep DB Footprint Lean:** Do not persist full article HTML or verbose debug logs in PostgreSQL. Application logs go to `ILogger` (`stdout`).
4. **No Retries on Quota-Guarded Clients:** Keep automatic retries disabled for API-Football/NewsData (each attempt costs quota) and for Telegram (a retried POST can duplicate a digest).
5. **Keep Pipeline Steps Isolated:** New pipeline work goes in its own step in `CollectorPipeline.RunAsync`; clients return empty/failed results instead of throwing for expected provider errors.
6. **Keep Endpoints Behind Auth:** New controllers are protected by the fallback policy automatically; only add `[AllowAnonymous]` for endpoints that must be public (like `/api/health`). Never accept the cron secret outside `POST /api/jobs/run`.
7. **Schema Changes Need a Migration:** After changing entities or `AppDbContext`, run `dotnet ef migrations add <Name> -p src/PlayerInfo.Api -o Data/Migrations`.
8. **Build & Test Command:**
   ```bash
   dotnet test --no-restore -p:UseSharedCompilation=false -m:1
   cd src/PlayerInfo.Web && npm ci && npm run build   # type-checks and builds the UI into ../PlayerInfo.Api/wwwroot
   ```
   *(All unit tests use `EF Core InMemory` and `StubHttpMessageHandler` — no live DB or external API keys required. Migrations themselves are not covered by tests.)*
