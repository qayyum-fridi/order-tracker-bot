# order-tracker-bot

A WhatsApp order-tracking assistant for small non-technical sellers, built as a .NET 8
Web API. The **merchant** (never the end buyer) chats with the bot on WhatsApp to log
orders, track status, manage a product catalog, run discounts/loyalty, and get daily
sales summaries — in mixed Roman Urdu / Urdu script / English.

This implements the flows from the WhatsApp UX mockup (`docs/` if attached separately):
onboarding, order capture with AI extraction + confirmation, status queries and updates,
catalog management, manual payment methods, discounts, loyalty, tracking numbers, COD
reconciliation, and the various edge-case/clarification flows (ambiguous grouping,
unknown product, duplicate order, mid-flow interruption, Urdu script input).

## Architecture

```
src/
  OrderTrackerBot.Domain          entities + enums, no dependencies
  OrderTrackerBot.Application     ConversationEngine (message router / state machine),
                                   deterministic CommandParser, AI abstractions
  OrderTrackerBot.Infrastructure  EF Core (SQL Server/Sqlite), WhatsApp Cloud API client,
                                   OpenAI client, DI wiring
  OrderTrackerBot.Api             ASP.NET Core webhook controller, Program.cs
tests/
  OrderTrackerBot.Tests           xUnit + Moq, EF Core Sqlite in-memory
```

**Message routing** (`ConversationEngine`): every inbound WhatsApp message is handled in
one of three ways, matching the "typing dots = AI, no dots = instant" distinction from the
UX spec:

1. **Mid-flow state** (onboarding, awaiting a confirmation, awaiting a missing field, ...)
   — the seller's `ConversationSession.State` takes priority over everything else.
2. **Deterministic command** (`CommandParser`) — fixed/near-fixed syntax like
   `"orders today"`, `"mark 3 shipped"`, `"[name] ka order"`, `"create discount: ..."`.
   Answered directly from the database. No LLM call.
3. **AI order extraction** (`IAiOrderAssistant`) — only reached when nothing above
   matches. One OpenAI call does intent classification + order-field extraction +
   confidence-based clarification together (keeps latency and cost down — this is the
   only per-message AI call on the happy path). A second, best-effort AI call adds a
   one-line insight to trending/slow-mover reports; it fails silently if OpenAI is
   unavailable.

This mirrors the "Where AI Actually Lives" table in the spec: AI is used for order-text
parsing, intent classification, clarification, catalog fuzzy-matching, and report
insight lines — nothing else. Status tracking, loyalty math, discount math, undo, and all
report SQL are plain deterministic code.

## Requirements

- .NET 8 SDK
- SQL Server (production) — Sqlite is used automatically in `Development` for a
  zero-install local loop (see `appsettings.Development.json`)
- A Meta WhatsApp Business/Cloud API app (phone number, access token, app secret)
- An OpenAI API key

## Local development

```bash
dotnet restore
dotnet build
dotnet test

cd src/OrderTrackerBot.Api
dotnet run   # ASPNETCORE_ENVIRONMENT defaults to Development -> Sqlite, no setup needed
```

The dev DB (`ordertrackerbot.dev.db`) is created automatically via `EnsureCreated()` on
startup — no migrations needed for Sqlite. Swagger UI is available at `/swagger`, and a
plain health check at `/health`.

Without `OpenAi:ApiKey` and `WhatsApp:AccessToken` configured, the bot still runs: it logs
outbound messages instead of sending them, and free-form (non-deterministic-command)
messages fall back to a clarification prompt instead of calling OpenAI. This is enough to
exercise onboarding, catalog, status queries/updates, discounts, loyalty, tracking, etc.
end-to-end without any external credentials.

### Simulating a WhatsApp message locally

```bash
curl -X POST http://localhost:5299/webhook/whatsapp \
  -H "Content-Type: application/json" \
  -d '{"entry":[{"changes":[{"value":{"messages":[
        {"from":"923001234567","type":"text","text":{"body":"start"}}
      ]}}]}]}'
```

## Configuration

All config lives under `appsettings.json` / environment variables (see
`docker-compose.yml` and `.env.example` for the production shape):

| Key | Purpose |
|---|---|
| `ConnectionStrings:Default` | SQL Server (or Sqlite) connection string |
| `Database:Provider` | `SqlServer` (prod) or `Sqlite` (dev) |
| `WhatsApp:PhoneNumberId` / `AccessToken` | Meta Graph API send credentials |
| `WhatsApp:VerifyToken` | matches the value you enter in Meta's webhook setup |
| `WhatsApp:AppSecret` | verifies `X-Hub-Signature-256` on inbound webhooks |
| `OpenAi:ApiKey` / `Model` | order extraction + insight generation |
| `FounderAlerts:WebhookUrl` | optional n8n/webhook target for merchant `feedback:` messages |
| `Instagram:LoginMode` | `facebook` (Facebook Login, `instagram_manage_comments`) or `instagram` (Instagram Login, `instagram_business_manage_comments`) |
| `Instagram:AppId` / `AppSecret` | the Meta app used for IG comment leads; the secret also verifies IG webhooks |
| `Instagram:PublicBaseUrl` | public https URL of this API (connect link + OAuth redirect `<url>/instagram/callback`) |
| `Instagram:VerifyToken` | IG webhook verify token (empty = reuse `WhatsApp:VerifyToken`) |

## Instagram comment leads setup (optional)

Without these settings, "connect instagram" just tells the seller it isn't active yet; everything else works.

1. Use the Meta app that has the approved comment permission. Set `Instagram:LoginMode` to match what was
   approved: `facebook` for `instagram_manage_comments` (the seller's IG must be Business/Creator **and linked
   to a Facebook Page**), or `instagram` for `instagram_business_manage_comments` (no Page needed).
2. Add the OAuth redirect URI `https://your-domain/instagram/callback` (Facebook Login → Settings →
   Valid OAuth Redirect URIs, or Instagram → Business login settings).
3. Webhooks → object **Instagram** → callback `https://your-domain/webhook/instagram`, verify token as
   configured → subscribe to the `comments` field.
4. The app must be in **Live** mode for comment webhooks from real sellers (in Development mode only app
   role users' accounts send events).
5. A seller types `connect instagram` in WhatsApp, opens the link, approves — the bot confirms in chat.
   New comments then arrive as leads (`comment leads`, `lead 1 converted`) or support queries with a
   one-tap public reply.

## WhatsApp Cloud API setup

1. Create a Meta App → add the **WhatsApp** product → get a test phone number (or
   attach your own business number).
2. Deploy this API somewhere reachable over HTTPS (see Deployment below).
3. In the Meta App Dashboard, configure the webhook:
   - Callback URL: `https://your-domain/webhook/whatsapp`
   - Verify token: same value as `WhatsApp:VerifyToken`
   - Subscribe to the `messages` field.
4. Copy the phone number ID and a permanent access token into
   `WhatsApp:PhoneNumberId` / `WhatsApp:AccessToken`.

## Deployment (VPS, alongside an existing app e.g. "ai-tutor")

Two options — pick whichever matches how the rest of your VPS is set up. If you already
run another .NET app there as a plain systemd service (not Dockerized), option A keeps
this one consistent with it and avoids running a second database container.

### Option A — systemd + shared SQL Server (matches an ai-tutor-style deployment)

If SQL Server is already running in Docker on the VPS (bound to `127.0.0.1:1433`), reuse
that instance — just add a new database for this app instead of standing up a second
SQL Server container:

```bash
docker exec -it <sqlserver-container> /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P '<sa-password>' \
  -Q "CREATE DATABASE OrderTrackerBot"
```

Publish and install as a systemd service:

```bash
dotnet publish src/OrderTrackerBot.Api -c Release -o /opt/order-tracker-bot/publish
sudo cp deploy/order-tracker-bot.service.example /etc/systemd/system/order-tracker-bot.service
sudo nano /etc/systemd/system/order-tracker-bot.service   # fill in the Environment= secrets
sudo systemctl daemon-reload
sudo systemctl enable --now order-tracker-bot
```

Then nginx (see `deploy/nginx.order-tracker-bot.conf.example`, proxies to `127.0.0.1:5001`
— one port per app, same pattern as an existing app on `:5000`):

```bash
sudo cp deploy/nginx.order-tracker-bot.conf.example /etc/nginx/sites-available/order-tracker-bot.conf
sudo nano /etc/nginx/sites-available/order-tracker-bot.conf   # set your real subdomain
sudo ln -s /etc/nginx/sites-available/order-tracker-bot.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d order-tracker.yourdomain.com
```

EF Core migrations run automatically on startup (`Database:AutoMigrate`, default `true`).

### Option B — fully Dockerized (self-contained, own SQL Server container)

Simpler if this is the only app on the box, or you'd rather not touch the host's existing
SQL Server: `Dockerfile` + `docker-compose.yml` in this repo run the API and its own SQL
Server container, bound to `127.0.0.1` only.

```bash
cp .env.example .env   # fill in WhatsApp + OpenAI credentials and a strong SQL password
docker compose up -d --build
```

Then the same nginx step as above, but proxying to `127.0.0.1:8091` (the port
`docker-compose.yml` publishes) instead of `:5001`.

### Generating a new migration after a model change

```bash
dotnet tool install --global dotnet-ef   # once
dotnet ef migrations add <Name> \
  --project src/OrderTrackerBot.Infrastructure \
  --startup-project src/OrderTrackerBot.Api \
  --context AppDbContext \
  --output-dir Persistence/Migrations
```

## Known limitations / roadmap

These match the spec's own version tags (V2/V3/V4) and are intentionally out of scope
for this first pass:

- **Weekly summary** (proactive, scheduled) — not wired to a scheduler yet; the query
  logic would reuse `SendTodaysSummaryAsync`-style aggregation, triggered by a hosted
  `BackgroundService`/cron instead of an inbound message.
- **Safepay payment gateway** — `payment link` degrades gracefully to a placeholder
  message when a seller has a Safepay payment method; wiring the real hosted-checkout
  API call and webhook-driven auto-confirmation is a follow-up.
- **WhatsApp broadcast** — `broadcast: ...` records the campaign intent but does not
  call Meta's template-message API; Meta requires a pre-approved message template for
  any send outside the 24h customer-service window, which needs to be set up per seller
  first.
- **Customer sentiment logging** (screen "10d" in the spec) — schema (`CustomerFeedback`)
  is in place; no command wires it up yet.
- **Multiple unresolved catalog products in one order** — if an order names two products
  neither of which matches the catalog, only the first is walked through the
  add-new/map-existing flow; this is a rare case for the typical 1–2 item order.
- Day-boundary calculations (`orders today`, `today's summary`) use UTC, not a
  per-seller timezone.
