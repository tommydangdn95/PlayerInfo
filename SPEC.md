# SPEC.md — PlayerInfo Project Specification

> **Purpose of this file:** Machine- and human-readable specification describing what this repository does, its architecture, constraints, API contracts, data model, and rules for any AI coding assistant working in this codebase.

---

## 1. Project Overview

**PlayerInfo** is a zero-cost, automated football (soccer) data and news aggregation backend built on **.NET 9 (ASP.NET Core Web API)**.

It runs as a Dockerized web service on **Render (Free Tier)** backed by **Neon Serverless PostgreSQL (Free Tier)** and is triggered 5 times daily via **`cron-job.org`** at peak European/global football hours (UTC+7).

### Core Goals
1. Sync structured football data without paid APIs: fixtures/results, team rosters and injuries from the **ESPN public API** (no key, no quota); transfers from **API-Football** (free plan, quota-guarded). API-Football's free plan blocks the current season for injuries/fixtures ("Free plans do not have access to this season"), so it is only used for transfers.
2. Aggregate daily football news for tracked teams and players from ESPN team news, free RSS feeds (Google News RSS, BBC Sport, ESPN, VnExpress) and NewsData.io.
3. Deduplicate articles using normalized URL SHA-256 hashes plus a normalized-title key (catches the same story from Google News and the publisher's own feed).
4. Batch unprocessed articles to **Gemini API** (JSON mode) to filter clickbait/spam, tag each article (topic, claim type: official/report/rumour/opinion, importance 1-5), link entities, write Vietnamese summaries (`SummaryVi`), and **group articles reporting the same news into stories** (one Vietnamese headline + summary per story, with source count and confidence).
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
│   ├── Logging/
│   │   ├── LiveLogBuffer.cs            # In-memory ring buffer (last 1000 entries) + live subscribers
│   │   ├── LiveLogLoggerProvider.cs    # Copies PlayerInfo.* logs (Info+) into the buffer, tagged with the running job (JobLogContext)
│   │   └── HttpTraceHandler.cs         # Traces every outbound HTTP call (method, redacted URL, status, duration, bodies) into the buffer
│   ├── Background/
│   │   └── JobRunnerService.cs         # Bounded Channel<JobRequest> + BackgroundService worker
│   ├── Controllers/
│   │   ├── AuthController.cs           # GET /api/auth/config, POST /api/auth/google, POST /api/auth/login, GET /api/auth/me
│   │   ├── HealthController.cs         # GET /api/health
│   │   ├── JobsController.cs           # POST /api/jobs/run, GET /api/jobs/quota
│   │   ├── LogsController.cs           # GET /api/logs, GET /api/logs/stream (SSE)
│   │   ├── NewsController.cs           # GET /api/news, /api/news/injuries, /api/news/transfers
│   │   ├── StoriesController.cs        # GET /api/stories/by-team, /api/stories/briefing/{teamId}
│   │   ├── SourcesController.cs        # Source credibility rules (CRUD, unclassified sources)
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
│   │   ├── EspnClient.cs               # site.api.espn.com: league teams, scoreboard, roster (+injuries), team news
│   │   ├── ApiFootballClient.cs        # v3.api-sports.io client (quota-guarded): transfers + team search only
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
| `full` | `CollectorMode.Full` | Injuries (ESPN roster of each ESPN-linked tracked team) + fixtures (ESPN scoreboard, configured leagues, yesterday..tomorrow) + transfers (API-Football, teams with an API-Football id) + news ingestion + Gemini AI batching + Telegram digest |
| `transfers_news` | `CollectorMode.TransfersNews` | Transfers (API-Football, last 7 days, teams with an API-Football id) + news ingestion + Gemini AI batching + Telegram digest |
| `news` | `CollectorMode.News` | News ingestion (Google News RSS + ESPN team news + Sports RSS + NewsData.io) + Gemini AI batching + Telegram digest |

**Cleanup:** every run with news ends with `CleanupOldNews`: articles older than `ArticleRetentionDays` (default 60, by both publish and fetch date) are deleted with their team links, then stories left without articles. With ~20 tracked teams this keeps news data around 100-150 MB, under the 1 GB Neon free tier. `ApiCalls` are kept 20 days.

**Telegram digest:** with Gemini configured it lists stories (importance >= 3, new or updated in the last 24h, max 10, most important first) with headline, teams, confidence, source count, summary and the most reliable source link; a story is pushed again only after it is updated (e.g. rumour -> official). Without Gemini it lists the newest articles.
| `roster` | `CollectorMode.Roster` | Manual/on-demand sync of `Players` squads (ESPN roster) for ESPN-linked tracked `Teams` |

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
| **ESPN public API** | Unofficial, no key, no quota | Standard resilience (retries) | Per run: 3 scoreboard calls per configured league (`full`), 1 roster call per ESPN-linked tracked team (`full`, `roster`), 1 news call per ESPN-linked tracked team (news modes). May change or block without notice: every call returns empty on failure/unexpected JSON. Never send a User-Agent containing "bot" (ESPN's CDN returns 403). |
| **API-Football** | 100 req/day | `90/day` (`ApiFootballDailyLimit`) | Only transfers (`transfers?team=`, teams with an API-Football id) and the admin's team search. `QuotaManager` blocks calls once `>= 90` in `ApiQuotaLogs`; each request is counted *before* sending and the HTTP client has no automatic retries. Daily budget about `4N` (N teams with an API-Football id; 2 `full` + 2 `transfers_news` runs), so ~19 teams max. HTTP-200 responses with an `errors` field (plan/season limits) are logged. |
| **NewsData.io** | 200 credits/day | `180/day` (`NewsDataDailyLimit`) | Single combined query per run (max 100 chars, multi-word keywords quoted); counted before sending, no retries; `QuotaManager` blocks calls once `>= 180`. |
| **Google News RSS** | Unofficial / IP throttle risk | 1.2–2.5s random jitter | Group aliases per entity with `OR` + `when:1d`; delay between requests. |
| **Gemini API** | Free tier, per model (Google AI Studio "Rate limits by model"): Gemini 4 Hydrogen and the Flash models (3.8, 3.6, 3, 2.5) 5 RPM / 250K TPM / 20 RPD; 3.5 and 3.1 Flash Lite 15 RPM / 250K TPM / 500 RPD; 2.5 Flash Lite 10 RPM / 20 RPD. Failed requests count too. | Batch size `15`; per-model RPD and RPM checked from `ApiCalls` (RPD per Google quota day, midnight Pacific; RPM over the last 60s) | Chain: `gemini-4-hydrogen` (the most capable free model) -> `gemini-3.8-flash` -> `gemini-3.6-flash` -> `gemini-3.5-flash-lite` -> `gemini-3.1-flash-lite` -> `gemini-3-flash-preview` -> `gemini-2.5-flash` -> `gemini-2.5-flash-lite` (best quality first; the Flash Lite pair carries the volume, 1,120 requests/day in total). A model at its RPD/RPM limit is skipped; 503/404/5xx/timeouts try the next model; a 429 skips that model for the rest of the run; 400/401/403 stop. No automatic retries. Up to 45 pending articles per run (3 batches). Articles pending > 48h are given up. Junk-source articles are never sent. |
| **Neon Postgres** | 1 GB storage, 100 CU-hrs/mo | Store metadata + short `SummaryVi` only | Never store full article HTML bodies or binary images. No `JobExecutionLogs` table (detailed logs go to `stdout` for Render Logs). |

---

## 6. Database Schema (`AppDbContext`)

Schema is managed by **EF Core migrations** (`Data/Migrations`), applied on startup by `DatabaseInitializer` (`Database.MigrateAsync()`). A database created by the earlier `EnsureCreated()` startup is detected and `InitialCreate` is recorded as applied. The InMemory provider (Development without a connection string) uses `EnsureCreated()`. Tables:

- **`Teams`**: `Id`, `EspnId` (unique, nullable for teams not yet linked), `LeagueSlug` (ESPN league, e.g. `eng.1`), `ApiFootballId` (unique, nullable; transfers only), `Name`, `Country`, `LogoUrl`, `IsTracked`, `UpdatedAtUtc`
- **`Players`**: `Id`, `EspnId` (unique), `TeamId` (FK nullable), `FullName`, `Position`, `Number`, `PhotoUrl`, `IsTracked`, `UpdatedAtUtc`
- **`WatchlistAliases`**: `Id`, `EntityType` (`Team` | `Player`), `EntityId` (internal `Teams.Id` / `Players.Id`), `Keyword`, `Language` (`en` | `vi`)
- **`NewsArticles`**: `Id`, `UrlHash` (SHA-256 unique), `Url`, `Title`, `Snippet`, `SourceName`, `Language`, `SummaryVi`, `Category` (topic: `Transfer` | `Injury` | `Match` | `PreMatch` | `Manager` | `Turmoil` | `Discipline` | `Club` | `Other`), `ClaimType` (`Official` | `Report` | `Rumour` | `Opinion`), `Importance` (1-5, 0 = not AI-processed), `IsHot` (importance >= 4), `SourceTier` (1-3, 0 = junk), `StoryId`, `IsRelevant`, `IsAiProcessed`, `IsNotified`, `PublishedAtUtc`, `FetchedAtUtc`
- **`Stories`**: one piece of news reported by one or more articles. `HeadlineVi`, `SummaryVi`, `Topic`, `Importance`, `Confidence` (`Official` = official claim from a tier 1-2 source; `Confirmed` = reported by a tier-1 source or by 2+ distinct tier 1-2 sources; `Opinion` = only opinion pieces; else `Rumour`), `SourceCount` (distinct sources), `BestTier`, `FirstSeenUtc`, `LastUpdatedUtc`, `NotifiedAtUtc`. Gemini sees the stories updated in the last 48h and attaches new articles to them (`storyRef` "S{id}") or opens new ones ("N1", ...); confidence and counts are recomputed in code.
- **`NewsSources`**: source credibility rules `Pattern` -> `Tier` (1 best .. 3, 0 junk), matched against source name or URL host, longest pattern first; unknown sources are tier 3. Seeded with defaults on first start; every admin change is re-applied to the last 30 days (junk is hidden; un-junked articles go back to Gemini).
- **`ApiCalls`**: one row per outbound HTTP request of every provider (ESPN, RSS, API-Football, NewsData, Gemini, Telegram), recorded by the HTTP trace handler for every attempt: `AtUtc`, `Provider`, `Model` (Gemini), `Endpoint` (host + path, no query string), `StatusCode`, `Outcome` (`Success` | `RateLimited` | `Overloaded` | `NotFound` | `ClientError` | `ServerError` | `Timeout` | `NetworkError`), `DurationMs`, `ResponseBytes`, Gemini token counts (`PromptTokens`, `OutputTokens`, `ThoughtTokens`, `TotalTokens` from usageMetadata), `ArticleCount`, `JobId`. Kept 20 days (the usage pages show at most 20 days); feeds the usage pages and Gemini per-model limits.
- **`LeagueTeams`**: cache of ESPN team lists per league (`LeagueSlug`, `EspnId`, `Name`, `ShortName`, `LogoUrl`, `UpdatedAtUtc`; unique on league + ESPN id), so the admin "Đội bóng" page does not call ESPN on every visit. Teams are fixed for a season: the list is fetched again when a new season starts (August) or on demand.
- **`ArticleEntities`**: Composite PK `(ArticleId, EntityType, EntityId)` linking articles to `Teams` or `Players`
- **`PlayerInjuries`** (ESPN roster): `Id`, `PlayerEspnId`, `PlayerName`, `TeamEspnId`, `TeamName`, `Reason`, `Type` (status, e.g. `Out`), `ReportedDateUtc`, `FetchedAtUtc`. An undated injury with the same reason within 30 days is not saved again.
- **`PlayerTransfers`**: `Id`, `PlayerExternalId`, `PlayerName`, `FromTeamExternalId`, `FromTeamName`, `ToTeamExternalId`, `ToTeamName`, `TransferType`, `TransferDateUtc`, `FetchedAtUtc`
- **`Fixtures`** (ESPN scoreboard): `Id`, `EspnEventId` (unique), `LeagueSlug`, `LeagueName`, `HomeTeamEspnId`, `HomeTeamName`, `AwayTeamEspnId`, `AwayTeamName`, `HomeGoals`, `AwayGoals` (null before kick-off), `StatusShort` (ESPN status name), `KickoffAtUtc`, `UpdatedAtUtc`
- **`PlayerTransfers`** keep API-Football player/team ids.
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
| `POST` | `/api/jobs/run?mode={mode}` | Admin JWT **or** `X-Cron-Secret` | Enqueues pipeline job and returns `202 Accepted` with a short `jobId` (used to tag its log lines). |
| `GET` | `/api/logs?after=&limit=` | Admin JWT | Recent live-log entries after an id (oldest first). In memory only: last 1000 entries of `PlayerInfo.*` categories at Information+, lost on restart. |
| `GET` | `/api/logs/stream?after=` | Admin JWT | Server-Sent Events: backlog after `after` (or `Last-Event-ID`), then each new entry (`id:` + JSON `data:`), `: ping` heartbeat every 15 s. The UI reads it with `fetch()` to send the JWT. |
| `GET` | `/api/jobs/quota` | Admin JWT | Today's UTC usage and limits for `ApiFootball` and `NewsData`. |
| `GET` | `/api/news?category=&teamId=&fromUtc=&toUtc=&hot=false&page=1&pageSize=20` | Admin JWT | Relevant news, newest first, with `total`. Each item has `teams` (linked directly or through a linked player). `teamId` filters by team (directly or via its players); `fromUtc`/`toUtc` is a half-open published-at range. `hot=true` returns only Gemini-flagged hot news (`isHot`). `pageSize` max 200. The admin UI "Tin tức" page groups results by Vietnam day or by team. |
| `GET` | `/api/stories/by-team?day=yyyy-MM-dd&teamId=&hot=false&perTeam=3` | Admin JWT | Per tracked team for one Vietnam day: story count, hot count, articles not yet grouped (`pendingCount`) and the most important stories (dashboard cards). |
| `GET` | `/api/stories/briefing/{teamId}?day=yyyy-MM-dd` | Admin JWT | Team briefing for one Vietnam day: stories (most important first) with their source articles (most reliable first), last/next match (ESPN fixtures), injuries (ESPN, latest per player, 30 days), transfers (API-Football, 90 days) and not-yet-grouped articles. The admin UI renders it at `/briefing/{teamId}` with a "Copy để đăng" share text. |
| `GET` | `/api/usage?day=yyyy-MM-dd&days=14` | Admin JWT | Every external API for one Vietnam day: totals, calls by hour, per provider (calls, errors, avg/max duration, bytes, Gemini tokens, API-Football/NewsData quota), 14-day history per provider and the latest 100 calls. Admin UI page "Thong ke API". |
| `GET` | `/api/gemini/usage?days=14` | Admin JWT | Gemini per Google quota day: each model of the fallback chain (and other known models) with its RPM/TPM/RPD limits against today's busiest minute (requests, tokens) and requests, outcomes and tokens; daily history; last 50 requests. |
| `GET` | `/api/database` | Admin JWT | Database health (read-only PostgreSQL statistics): size against the Neon free tier (`Database__StorageLimitMb`, default 1024), per-table rows, data/index sizes, dead rows and last vacuum, applied/pending migrations, news growth (articles per day over 7 days) and the size it settles at under `ArticleRetentionDays`. Falls back to row counts if the statistics cannot be read; InMemory shows row counts only. Admin UI page "Cơ sở dữ liệu". |
| `GET` | `/api/sources` | Admin JWT | Source credibility rules. |
| `GET` | `/api/sources/unclassified` | Admin JWT | Sources of the last 7 days matched by no rule, most frequent first. |
| `POST` / `PUT` / `DELETE` | `/api/sources`, `/api/sources/{id}` | Admin JWT | Add / change / remove a rule (`{pattern, tier}`); returns how many recent articles were re-tiered. |
| `GET` | `/api/news/injuries?limit=30` | Admin JWT | Latest tracked player injuries. |
| `GET` | `/api/news/transfers?limit=30` | Admin JWT | Latest tracked player transfers. |
| `GET` | `/api/watchlist/teams` | Admin JWT | All teams, each with its keyword aliases. |
| `GET` | `/api/watchlist/teams/{id}` | Admin JWT | One team with its aliases. |
| `GET` | `/api/watchlist/league-teams?league=&refresh=false` | Admin JWT | Teams of a configured league, marking those already tracked. Served from the `LeagueTeams` cache; ESPN is called only when the cache is empty, was fetched in an earlier season (seasons start in August), or `refresh=true` ("Cập nhật từ ESPN" button). If ESPN fails the cached list is kept. |
| `GET` | `/api/watchlist/search-teams?q=` | Admin JWT | Searches API-Football teams by name to find an `apiFootballId` for transfers (≥ 3 letters/digits/spaces; costs 1 quota request); marks ids already linked. |
| `GET` | `/api/watchlist/leagues` | Admin JWT | Configured ESPN leagues `[{ slug, name }]` (`Collector:Leagues`). |
| `POST` | `/api/watchlist/teams` | Admin JWT | Add a team (`espnId`, `leagueSlug`, `name`, optional `country`, `logoUrl`, `apiFootballId`). `400` for a non-numeric ESPN id or unknown league, `409` if the ESPN or API-Football id is already used. |
| `PATCH` | `/api/watchlist/teams/{id}` | Admin JWT | Partial update (`name`, `country`, `espnId` + `leagueSlug` to link ESPN, `apiFootballId` (0 clears), `isTracked`). `409` on id conflicts. |
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
- `ApiKeys__ApiFootball` (optional; only transfers and the API-Football team search need it)
- `ApiKeys__NewsData` (optional)
- `Collector__ArticleRetentionDays` (default 60; 0 = keep forever)
- `ApiKeys__Gemini` (optional; if omitted, articles are marked `IsAiProcessed = true` without AI summary, no stories are built and Telegram gets the article digest)
- `Collector__GeminiModel` (default `gemini-4-hydrogen`), `Collector__GeminiFallbackModels__0..n` (default chain above), `Collector__GeminiModelLimits__N__Model` / `__RequestsPerDay` / `__RequestsPerMinute` / `__TokensPerMinute` (defaults: the free-tier table above) and `Collector__GeminiDailyRequestLimit` (20 requests/day for models not listed; 0 = no limit)
- `Telegram__BotToken` & `Telegram__ChatId` (optional)

`Collector:Leagues` (ESPN slugs + names; default 8: `eng.1`, `esp.1`, `ita.1`, `ger.1`, `fra.1`, `uefa.champions`, `uefa.europa`, `uefa.europa.conf`) and `Collector:SportsRssFeeds` live in `appsettings.json`. Their C# defaults are applied only when the configured list is empty (`CollectorOptions.ApplyDefaults`), because the configuration binder appends to initialized lists. `System.Net.Http.HttpClient` logging is set to `Warning` so request URLs (the Telegram bot token is in the path) are not logged.

---

## 9. Guidelines for AI Coding Assistants

1. **Preserve Quota Guards:** Any new endpoint call to `API-Football` or `NewsData.io` must check `QuotaManager.CanConsumeAsync` before sending and call `QuotaManager.IncrementAsync` afterward. Never loop per-player over `API-Football`. Prefer ESPN for anything it provides.
2. **Preserve Cold-Start Resilience:** Do not make `POST /api/jobs/run` synchronous; keep `JobQueue` fire-and-forget (`202 Accepted`) and keep `EnableRetryOnFailure` on `Npgsql`.
3. **Keep DB Footprint Lean:** Do not persist full article HTML or verbose debug logs in PostgreSQL. Application logs go to `ILogger` (`stdout`) and the in-memory live log (`LiveLogBuffer`), never to the database. Do not log secrets: the live log shows every `PlayerInfo.*` message to the admin. Outbound HTTP calls of every typed client are traced by `HttpTraceHandler` (registered after the resilience handler, so each attempt is one entry): headers are never recorded, secret query values (`apikey`, `key`, `token`, …) and the Telegram bot token in the path are masked, request bodies are kept up to 8 KB and response bodies up to 16 KB.
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
