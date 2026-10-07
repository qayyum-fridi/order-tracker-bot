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
   `AwaitingSupportReplyConfirmation`/`Edit`, `AwaitingSupportQueryPick`, plus onboarding states) takes priority over everything else — it's checked before
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
- Broadcasts send via Meta's template API only when `WhatsAppTemplates:broadcast:Name` (in `src/OrderTrackerBot.Api/whatsapp-templates.json`, hot-reloaded) is set to an
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
- Day boundaries for `orders today` / `today's summary` use `Seller.TimeZoneId` (default
  `Asia/Karachi`) via `SellerClock`; other "last N days"/weekly windows are still UTC-based, and there is
  no command to change a seller's timezone yet. `TimeZoneId` has no EF migration yet (generate one).
- Instagram comment leads / support queries (`ConversationEngine.Support.cs`, `InstagramController`,
  `InstagramClient`) need `Instagram:*` config; `LoginMode` must match which permission Meta approved
  (`facebook` = `instagram_manage_comments` via a linked Page, `instagram` = Instagram Login). The bot never
  messages buyers on WhatsApp — support replies are drafted for the seller to forward; only public IG comment
  replies are posted directly. Comment notifications are free-form WhatsApp text, so they share the 24h-window
  limitation above.
- PDF receipts (`receipt`, `receipt 12`, `Ayesha ki receipt`, `رسید 12` -> `ConversationEngine.Receipts.cs`, QuestPDF in
  `Infrastructure/Pdf`) are sent as a WhatsApp document to the *seller's* chat, who forwards them to the buyer (the bot never
  messages buyers). QuestPDF runs under its Community licence (free below US$1M revenue); Linux hosts need `libfontconfig1`
  (in the Dockerfile); the Urdu-script font (Noto Naskh Arabic, OFL) is embedded. The receipt is English-labelled only.
- Receipt logo/banner: the seller sends a picture captioned `logo` / `banner` (`ConversationEngine.Branding.cs`); stored as bytes in
  `SellerBrandings` (own table; created on Sqlite by `SqliteSchemaPatcher`, **no SQL Server migration yet — generate one**). Images
  are validated with QuestPDF only (no resize/re-encode; QuestPDF downsamples when rendering) and capped at 5 MB; a stored image
  the PDF engine can't draw falls back to a plain receipt. `reset account` deletes them.
- Export (`export`, `export orders customers`, `export all`, `export orders 30 days|last month|today|yesterday`, `ایکسپورٹ` -> `ConversationEngine.Export.cs`,
  MiniExcel in `Infrastructure/Export`): one .xlsx (sheets Orders, Order Items, Customers, Catalog, Discounts, Loyalty Rules) sent to the seller's own chat.
  Excel only, no CSV (CSV loses leading-zero phone numbers and Urdu text unless handled, and can't hold several sheets); strings are written as
  string cells so buyer-supplied text can't become formulas. Order period filters use the seller's timezone; customers/spend are aggregated in memory
  (Sqlite can't SUM decimal). Expired-trial sellers are blocked by billing like every other command.
- Shortcuts (`ConversationEngine.Shortcuts.cs`): after a turn that replied, ended in `Idle` and sent no interactive message, the engine adds a 3-button
  quick bar (Menu / Naya order / Orders today; labels are parseable commands). Per-seller `shortcut off|on` lives in `SessionContextData`;
  global flag `Features:ShortcutButtons` (engine built without `FeatureOptions` = off, so unit tests stay quiet). "/" commands: `CommandParser.SlashCommands`,
  registered with Meta via `deploy/whatsapp-conversational-components.json` (**schema from third-party docs; how Meta delivers a picked command was not
  verified** — the engine just accepts `/name` as a typed alias).
- Delivery charges (`ConversationEngine.Delivery.cs`): `Seller.DefaultDeliveryCharge` ("delivery 200" / "free delivery") is applied to new
  drafts; "delivery 300" while confirming changes only that draft; "order 12 delivery 300" changes a saved order. A charge written inside the order itself ("..., delivery 300", "+250 delivery",
  "free delivery") is picked up: deterministically by `CommandParser.TryFindDeliveryInOrderText` for text (wins over the model; single-customer
  messages only; bare numbers under 50 are ignored as likely dates unless "Rs" is written), and via the model's `delivery_charge` field for screenshots. Always
  `Total = max(0, Subtotal - DiscountAmount) + DeliveryCharge` (`OrderTotal`); discounts (codes and loyalty) never touch delivery. Sales totals include delivery.
- `OrderStatus.Returned` (+ `Order.ReturnedAt`): "mark 3 returned/wapas", "Ayesha ka order wapas aa gaya". Only from Shipped/Delivered (a pending order is
  cancelled instead). Excluded everywhere Cancelled is excluded from sales/loyalty/customer spend; shown as "Returned: N" in today's/weekly summary.
  New columns (`Orders.DeliveryCharge`, `Orders.ReturnedAt`, `Sellers.DefaultDeliveryCharge`) are added on Sqlite by the patcher — **no SQL Server migration**.
- Inbound WhatsApp messages go through `WebhookMessageGate` (Infrastructure/WhatsApp): a per-sender `SemaphoreSlim` (in-process only — several app
  instances would need sticky routing or a DB lock) and a DB claim on the message id (`ProcessedWebhookMessages`, PK) made in its own scope before
  handling, so Meta redeliveries are dropped across restarts. A message whose handling throws stays claimed (the seller got the error reply).
  The scheduler purges claims older than 7 days, claims weekly/trial sends with an atomic `ExecuteUpdate` (never twice), and isolates per-seller
  failures (reported as OTB-5001). Ported from PR #9 without its voice-note part.
- Editing a saved order (`ConversationEngine.OrderEdit.cs`): "edit order 12" / "order 12 edit" / "edit order" (latest) enters
  `AwaitingOrderEdit`; one instruction per message (`CommandParser.TryParseOrderEdit`: "1 = 3", "price 1 = 1500", "remove 2", "add Kurti 2",
  "phone …", "address …", "name …", "delivery 250", "payment cod") until "done"; any other real command leaves edit mode and runs.
  The first change logs an `ActionType.OrderEdited` snapshot so one "undo" restores items, amounts, payment method and the customer's
  name/phone/address. Totals are recomputed (percent codes re-applied, flat ones capped). Cancelled/Returned orders can't be edited.
  Phone/address/name edits change the shared Customer record, not just this order.
- Part payments: `Order.AmountPaid` (money received so far). `OrderMoney.Received/Balance/State` (Formatting) are the single source — `Paid`
  status always means fully paid, including older paid orders whose AmountPaid is 0. "order 12 advance 500" / "12 paid 1000" add to it and flip
  to Paid at the total; an advance written in a new order ("…, advance 500") or typed while confirming is saved with the order. Every
  "mark paid" path goes through `MarkFullyPaid` (logs previous status + amount for undo). Unpaid/COD lists, payment link, today's summary,
  receipt and export use the balance. "order 12" / "#12" shows one order in full.
- Stock (`ConversationEngine.Stock.cs`): only products with a `StockQty` are tracked ("stock Kurti 20" / "+10" / "off", "stock" lists).
  An active order holds its items' quantities, a cancelled/returned one holds none; every lifecycle change (save, cancel, status change incl.
  returned, edit, undo of any of these) applies `StockFootprint(after) - StockFootprint(before)` — add that call around any new place that
  changes an order's items or status. Stock can go negative (oversold). Low/out-of-stock warnings (≤3) are collected per turn and sent once
  after the reply (`FlushStockWarningsAsync`).
- Voice notes (ported from PR #9): `HandleAudioMessageAsync` downloads the audio, `OpenAiAudioTranscriber` (`OpenAi:TranscriptionModel`,
  default `whisper-1`, plus `TranscriptionPrompt` steering towards Roman Urdu/English and digits) transcribes it, the bot echoes
  "🎤 Maine suna: …" and handles the text exactly like a typed message. No API key -> the old "send it as text" reply; a failed
  transcription asks to resend and reports OTB-3002.
- Customer corrections: "Sara ka phone 0300…", "Sara ka address …", "Bilal ki city …", "customer Sara name Sara Khan" (`CommandKind.CustomerUpdate`,
  `ConversationEngine.CustomerUpdate.cs`). Questions ("… kya hai") and non-numeric phones are not treated as updates; an ambiguous name
  lists the matches instead of guessing; each change is undoable (`ActionType.CustomerUpdated`).
- The WhatsApp list body (help) must stay ≤ 1024 chars — a test enforces it; keep `HelpText` curated rather than exhaustive.
- Custom fields (`ConversationEngine.CustomFields.cs`, WordPress-style attributes): the seller defines a field per entity (product / customer / order;
  max 10 per entity, name <= 30 chars): free text ("add field product Fabric"), **choice** ("…Fabric: Cotton, Lawn, Silk"; 2-10 options <= 20 chars so
  they fit a WhatsApp button; "add/remove option product Fabric: Silk" — removing keeps values already saved), **number** ("…Weight: number", stored
  normalised, exported as an Excel number) or **date** ("…Birthday: date"; "12 May" keeps day+month, "12/05/1995" the full date). A trailing "private"
  ("…Cost: number private", "hide/show field product Cost") keeps a field off the PDF receipt and "share catalog". Values are set by typing
  ("set product Kurti Fabric = Cotton"; `= -` clears; validated per type, choice fields accept only an option) or by **tapping**: "set product Kurti
  Fabric" (options as buttons <=3 / list), "set product Kurti" (fields first), "fields" (entity -> record -> field -> value, records as a list of the
  latest 10), the "🏷️ Set fields" button sent after an order is saved / the order detail / the customer profile / "fields product Kurti"
  (`ctx.FieldsTarget`), and the menu rows under Catalog and Settings. The tap flow is `ConversationState.AwaitingCustomFieldChoice`
  (`CustomFieldPickData.Stage` = entity|record|field|value|text; "cancel" ends it, any other real command leaves it and runs). Commands also accept
  Urdu-script words in the same word order ("نئی فیلڈ پروڈکٹ فیبرک", "سیٹ پروڈکٹ …", "فیلڈز"). Values show in the customer profile, order detail, catalog list,
  shareable catalog (public fields), PDF receipt (public product fields under the item + public order fields) and as extra Excel columns on the
  Orders/Customers/Catalog sheets. The draft "Confirm order" text lists the catalog products' values (all fields, private included — it is the seller's view). Values max 200 chars;
  setting/clearing a value is undoable (`ActionType.CustomFieldChanged`; not after its field was removed); "remove field" deletes its values and is not undoable;
  `reset account` deletes both tables. New tables `CustomFields`/`CustomFieldValues` are created on Sqlite by the patcher —
  **no SQL Server migration yet — generate one**.
