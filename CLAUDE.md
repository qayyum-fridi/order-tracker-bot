# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A WhatsApp order-tracking assistant for small non-technical sellers, built as a .NET 8 Web API. The **merchant** (never the end
buyer) chats with the bot on WhatsApp to log orders, track status, manage a product catalog, run discounts/loyalty, and get sales
reports — in mixed Roman Urdu / Urdu script / English. The bot never messages buyers.

Per-feature detail (what each command does, its limits, what is not done) lives in `docs/feature-notes.md`; the command list in
`docs/command-dictionary.md`. Read the relevant note before changing a feature, and check it against the code.

## Commands

```bash
dotnet restore
dotnet build
dotnet test

# single test
dotnet test --filter "FullyQualifiedName~CommandParserTests.Parses_MarkShipped"

# the guard suites at once (alternation works in --filter)
dotnet test --filter "FullyQualifiedName~CommandListTests|FullyQualifiedName~IntentCatalogTests|FullyQualifiedName~CommandDictionaryTests|FullyQualifiedName~MessageTranslationsTests|FullyQualifiedName~MessageTemplatesTests|FullyQualifiedName~TemplateGuardTests"

cd src/OrderTrackerBot.Api
dotnet run   # ASPNETCORE_ENVIRONMENT defaults to Development -> Sqlite, no setup needed
```

**Test baseline:** `dotnet test` must pass fully (0 failed). As of 2026-11-19: about 4,500 passed, 8 skipped, no known flaky or
pre-existing failures across repeated full runs. The skipped tests are opt-in and need an environment variable (and usually
`OPENAI_API_KEY`): `OTB_PERF`, `OTB_SIM`, `OTB_TURN_EVAL` / `OTB_TURN_LIVE`, `OTB_AGENT_EVAL` / `OTB_AGENT_LIVE` / `OTB_AGENT_REAL`,
`OTB_VOICE_EVAL`, `VOICE_TRANSCRIPTS`. Expected noise that is not a failure: xUnit "Skipping test case with duplicate ID" for repeated
`CommandDictionaryTests` rows, and two CS8604 warnings in `MockupFeatureTests`.

The dev DB (`ordertrackerbot.dev.db`) is created by `EnsureCreated()` on startup. Swagger at `/swagger`, health at `/health`.
Without `OpenAi:ApiKey` / `WhatsApp:AccessToken` the bot still runs end-to-end: outbound messages are logged instead of sent, and
messages no deterministic command matches get a clarification prompt instead of an OpenAI call.

Simulate an inbound WhatsApp message locally:

```bash
curl -X POST http://localhost:5299/webhook/whatsapp \
  -H "Content-Type: application/json" \
  -d '{"entry":[{"changes":[{"value":{"messages":[
        {"from":"923001234567","type":"text","text":{"body":"start"}}
      ]}}]}]}'
```

## Architecture

```
src/
  OrderTrackerBot.Domain          entities + enums, no dependencies
  OrderTrackerBot.Application     ConversationEngine (router / state machine), CommandParser, AI abstractions, templates, intents
  OrderTrackerBot.Infrastructure  EF Core (SQL Server/Sqlite), WhatsApp Cloud API, OpenAI, PDF/Excel, backups, DI (DependencyInjection.cs)
  OrderTrackerBot.Api             webhook controllers, worker services, admin/client APIs, Program.cs
tests/
  OrderTrackerBot.Tests           xUnit + Moq, EF Core Sqlite in-memory
```

**Inbound path:** `WhatsAppWebhookController` validates the signature and hands each message to `WebhookInbox.AcceptAsync`, which
stores a `PendingWebhookMessages` row (PK = message id, so Meta retries are duplicates) before queueing it and returning 200.
`WebhookWorkerService` runs 8 lanes keyed by sender (one seller's messages stay in order) through `WebhookMessageGate` (per-sender
lock + claim on the message id). The lock is in-process: several app instances would need sticky routing or a DB lock.

**Message routing** (`ConversationEngine`, partial classes split by concern), in this priority order:

1. **Mid-flow state** — the seller's `ConversationSession.State` (`ConversationState`: awaiting confirmation, missing fields,
   clarification choice, onboarding steps, …) is checked first, before onboarding and before command parsing.
2. **Deterministic command** — `CommandParser` (fixed / near-fixed syntax such as `orders today`, `mark 3 shipped`, `[name] ka order`).
   Answered from the database, no LLM call.
3. **AI** — only when nothing above matches: the model-first turn interpreter (`Features:ModelFirst`, one strict-schema call returning one
   action from a closed list), then order extraction / the intent catalog. The engine runs the action with the existing handlers;
   the model never writes a command or data.

AI is used only for order-text parsing, intent classification, clarification, catalog fuzzy-matching, voice/screenshot reading and
report insight lines. Status tracking, money, loyalty and discount math, undo, stock and all report SQL are deterministic code —
never route these through the AI. For a sentence the model-first path gets wrong, add it to `TurnUnderstandingEval.Cases` and fix the
prompt / action list; do not add keyword guards.

**Provider switching:** `Database:Provider` (`SqlServer` | `Sqlite`) picks the EF provider in `DependencyInjection.AddInfrastructure`;
`Program.cs` uses `EnsureCreated()` for Sqlite and `Database.Migrate()` for SQL Server (gated by `Database:AutoMigrate`).

**Config-optional infrastructure:** `WhatsAppSender`, `OpenAiOrderAssistant`, `FounderAlertNotifier` (and email, Drive backup,
Instagram) degrade gracefully when their config is absent instead of throwing; this is what makes the credential-free dev loop work.
Feature flags (`FeatureOptions`, `Features:*`) are usually **off in code and on in `appsettings.json`**, so unit tests built without
`FeatureOptions` keep the plain behaviour.

## Rules

**Templates.** Every seller-facing message is a keyed entry in `src/OrderTrackerBot.Application/Templates/Messages/*.json`
(embedded; keys unique across files; Roman Urdu). English / Urdu-script text is hand-written in `Templates/Translations/<language>/*.json`
with the same keys. Code sends `MessageTemplates.Get("key", args...)`; the caller formats numbers and money. Never put a prose literal in
a send call (`TemplateGuardTests`). Button labels the engine matches on stay in code. Industry shop wording is in `Templates/Industries/*.json`.
- Placeholders are `{0}`, `{1}`, … numbered from zero with no gaps (`MessageTemplatesTests`). Pass each key only the args it uses.
- A translation may use only slots the Roman Urdu source fills, and must keep the same slots (`MessageTranslationsTests`;
  `MessageTranslations.TryTranslate` returns false on an unfilled slot).
- Tone: polite "Aap" form, "Maaf kijiye, …" for errors, "Barae meherbani …" for requests; every "not found" says what to type next.
  Tests match short stable fragments ("nahi mila", "samajh nahi aaya"): keep those words when rewording.

**Intents and commands.** A message the parser cannot place goes to `IAiOrderAssistant.RouteIntentAsync`, which picks one intent from
`src/OrderTrackerBot.Application/Intents/intents.json` and fills its slots; the engine builds the command from the entry's `template`
and runs it through the parser (it must parse to the entry's `kind`). A missing slot or a risky command is answered with the line to
type (`intent.howto`), never run. Add a feature as an intent entry, not a new phrase branch.
- New `CommandKind`: an `intents.json` entry (or a `NotInCatalog` exclusion in `IntentCatalogTests`), a row in
  `docs/command-dictionary.md`, and a `CommandDictionaryTests` case.
- Money-changing or hard-to-reverse commands go in `ActionRisk.RiskyCommands` (voice and AI routes then need a YES).
- Quick-bar / suggestion button labels (`SuggestNext`) must parse as commands and fit 20 characters.

**WhatsApp limits** (tests enforce them for the Roman Urdu text): interactive body ≤ 1024 chars, text ≤ 4096, button title ≤ 20,
list ≤ 10 rows with titles ≤ 24.

**Domain invariants.**
- Every seller's time is Pakistan time: use `SellerClock` (`DaysAgoUtc`, day/month ranges), never `UtcNow.AddDays(-N)`.
- `OrderMoney` is the single source for received / balance / paid state; every "mark paid" path goes through `MarkFullyPaid`.
- Any code that changes an order's items or status applies `StockFootprint(after) - StockFootprint(before)`.
- `Total = max(0, Subtotal - DiscountAmount) + DeliveryCharge`; discounts never touch delivery.
- Adding an export column needs a `GuideColumn` (a test fails otherwise).

**Schema and migrations.** SQL Server is the versioned provider; Sqlite dev uses `EnsureCreated` plus `SqliteSchemaPatcher`, which
adds new tables/columns to an existing dev DB — add to the patcher when you add to the model. Generate SQL Server migrations with
the SQL Server provider (Sqlite output would carry INTEGER/TEXT types):

```bash
Database__Provider=SqlServer ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations add <Name> \
  --project src/OrderTrackerBot.Infrastructure --startup-project src/OrderTrackerBot.Api \
  --context AppDbContext --output-dir Persistence/Migrations
# check: same env, dotnet ef migrations has-pending-model-changes
```

As of 2026-11-19 `has-pending-model-changes` reports changes with no migration: `Sellers.SalesMode`, `Products.Barcode`,
`Discounts.Category`, and the tables `Categories` and `CounterReturns`. Everything else in the model is covered by `SqlServerBaseline`
and the migrations after it. Per the earlier notes, no migration has been applied to a live SQL Server yet (not verifiable from the repo).

## Deployment

Full steps in `README.md` ("Deployment"). (A) `dotnet publish` + systemd + nginx reverse proxy to `127.0.0.1:5001`, using
`deploy/*.example` (secrets in the unit's `Environment=` lines, never committed); (B) `docker compose up -d --build`
(`Dockerfile` + `docker-compose.yml`). `docker-compose.yml` runs **Sqlite** (`Database__Provider: Sqlite`, no database container):
README's Option B text about "its own SQL Server container" is out of date. Set `GIT_SHA` before building so `version` reports the build.
Pushing to or restarting the server is a shared-state action: confirm with the user and get SSH/host details first.
