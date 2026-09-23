# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A WhatsApp order-tracking assistant for small non-technical sellers, built as a .NET 8 Web
API. The **merchant** (never the end buyer) chats with the bot on WhatsApp to log orders,
track status, manage a product catalog, run discounts/loyalty, and get daily sales
summaries — in mixed Roman Urdu / Urdu script / English.

## Commands

```bash
dotnet restore
dotnet build
dotnet test

# single test
dotnet test --filter "FullyQualifiedName~CommandParserTests.Parses_MarkShipped"

cd src/OrderTrackerBot.Api
dotnet run   # ASPNETCORE_ENVIRONMENT defaults to Development -> Sqlite, no setup needed
```

The dev DB (`ordertrackerbot.dev.db`) is auto-created via `EnsureCreated()` on startup —
no migrations needed for Sqlite. Swagger UI at `/swagger`, health check at `/health`.

Without `OpenAi:ApiKey` / `WhatsApp:AccessToken` configured, the bot still runs end-to-end:
outbound messages are logged instead of sent, and free-form (non-deterministic-command)
messages fall back to a clarification prompt instead of calling OpenAI.

Simulate an inbound WhatsApp webhook locally:

```bash
curl -X POST http://localhost:5299/webhook/whatsapp \
  -H "Content-Type: application/json" \
  -d '{"entry":[{"changes":[{"value":{"messages":[
        {"from":"923001234567","type":"text","text":{"body":"start"}}
      ]}}]}]}'
```

Generate a new EF Core migration after a model change (SQL Server is the versioned
provider; Sqlite dev uses `EnsureCreated` and never gets migrations):

```bash
dotnet ef migrations add <Name> \
  --project src/OrderTrackerBot.Infrastructure \
  --startup-project src/OrderTrackerBot.Api \
  --context AppDbContext \
  --output-dir Persistence/Migrations
```

## Architecture

```
src/
  OrderTrackerBot.Domain          entities + enums, no dependencies
  OrderTrackerBot.Application     ConversationEngine (message router / state machine),
                                   deterministic CommandParser, AI abstractions
  OrderTrackerBot.Infrastructure  EF Core (SQL Server/Sqlite), WhatsApp Cloud API client,
                                   OpenAI client, DI wiring (DependencyInjection.cs)
  OrderTrackerBot.Api             ASP.NET Core webhook controller, Program.cs
tests/
  OrderTrackerBot.Tests           xUnit + Moq, EF Core Sqlite in-memory
```

**Message routing** (`ConversationEngine`, split across partial-class files by concern —
`.Commands.cs`, `.Commands2.cs`, `.Flows.cs`, `.Onboarding.cs`, `.OrderFlow.cs`): every
inbound WhatsApp message is handled in one of three ways, matching the "typing dots = AI,
no dots = instant" distinction from the original UX spec, checked in this priority order:

1. **Mid-flow state** — the seller's `ConversationSession.State` (`ConversationState` enum:
   `AwaitingOrderConfirmation`, `AwaitingOrderMissingFields`, `AwaitingOrderGroupingChoice`,
   `AwaitingClarificationChoice`, `AwaitingCancelConfirmation`, `AwaitingBulkStatusConfirmation`,
   `AwaitingDuplicateOrderConfirmation`, `AwaitingCodCollectedConfirmation`,
   `AwaitingRuntimeFilterChoice`, `AwaitingCustomDateRange`, `AwaitingBroadcastAudienceChoice`,
   plus onboarding states) takes priority over everything else — it's checked before
   `OnboardingComplete` handling and before command parsing.
2. **Deterministic command** (`CommandParser`) — fixed/near-fixed syntax like
   `"orders today"`, `"mark 3 shipped"`, `"[name] ka order"`, `"create discount: ..."`.
   Answered directly from the database. No LLM call.
3. **AI order extraction** (`IAiOrderAssistant`, implemented by `OpenAiOrderAssistant`) —
   only reached when nothing above matches. One OpenAI call does intent classification +
   order-field extraction + confidence-based clarification together (kept to a single
   per-message AI call on the happy path for latency/cost). A second, best-effort AI call
   adds a one-line insight to trending/slow-mover reports and fails silently if OpenAI is
   unavailable.

This mirrors the spec's "Where AI Actually Lives" rule: AI is used only for order-text
parsing, intent classification, clarification, catalog fuzzy-matching, and report insight
lines. Status tracking, loyalty math, discount math, undo, and all report SQL are plain
deterministic code — never route these through the AI assistant.

**Provider switching**: `Database:Provider` (`SqlServer` or `Sqlite`) picks the EF Core
provider in `DependencyInjection.AddInfrastructure`; `Program.cs` branches on
`db.Database.IsSqlite()` to choose `EnsureCreated()` (dev) vs `Database.Migrate()` (prod,
gated by `Database:AutoMigrate`).

**Config-optional infrastructure**: `WhatsAppSender`, `OpenAiOrderAssistant`, and
`FounderAlertNotifier` are all designed to degrade gracefully when their config
(`WhatsApp:AccessToken`, `OpenAi:ApiKey`, `FounderAlerts:WebhookUrl`) is absent, rather
than throwing — this is what makes the credential-free local dev loop possible.

## Deployment

Full steps are in `README.md` ("Deployment"). Two paths: (A) `dotnet publish` + systemd
service + nginx reverse proxy to `127.0.0.1:5001`, using `deploy/*.example` templates
(secrets go in the unit's `Environment=` lines, never committed); (B) `docker compose up -d --build`
via `Dockerfile` + `docker-compose.yml` (recently switched to Sqlite — README's mention of
a bundled SQL Server container may be stale). Pushing to or restarting the server is a
shared-state action: confirm with the user and get SSH/host details first.

## Known gaps (intentionally out of scope for this pass)

- Safepay payment gateway integration is a placeholder message only (the merchant ID is saved;
  "payment link" still shows manual numbers).
- Broadcasts send via Meta's template API only when `WhatsApp:BroadcastTemplateName` points at an
  approved template; SMS has no provider — SMS sends are recorded as `not_configured`.
- Subscription "paid" is trusted on the seller's word and fires a founder alert to verify manually;
  there is no payment verification.
- Proactive messages (weekly summary Sunday 9:00 PKT, trial-ending reminder) come from
  `ScheduledMessagesService`; outside WhatsApp's 24h window Meta rejects free-form text, so these
  only reach sellers who messaged the bot in the last 24h until a template is used.
- EF migrations (`InitialCreate`, `MockupParity`) were generated with the Sqlite provider; a real
  SQL Server deploy would need them regenerated.
- An order naming two unmatched catalog products only walks through add-new/map-existing
  for the first one.
- Day-boundary calculations (`orders today`, `today's summary`) use UTC, not per-seller
  timezone.
