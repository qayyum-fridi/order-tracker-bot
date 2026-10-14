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

# large-account latency/accuracy benchmark (skipped unless OTB_PERF=1; OTB_PERF_ORDERS=30000 default)
OTB_PERF=1 dotnet test --filter "FullyQualifiedName~LargeAccountPerfTests" --logger "console;verbosity=detailed"

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
- Parser overlaps fixed: "bill kitna hua order id 501 ka?" (a receipt word plus a question word) shows that order (`OrderDetail`) or goes to the AI when the number is spoken in words — it is never a receipt for "kitna hua…"; a sentence that starts "purana data/system …" and says it wants to bring data in (system/aana/lekar/excel/file/import…, no delete/hata/reset) opens the import help.
- Import from an old system (`ConversationEngine.Import.cs`, `DataExchangeGuide.cs`, `Infrastructure/Export/ImportXlsxReader.cs`): `import` / "Purana data" (the third start-of-onboarding button, replacing
  "Language badlein" — typed "change language" still works) offers three template files (`import template customers|catalog|all`); each is an .xlsx whose first tab is **Guide**
  (what each column is, REQUIRED/Optional, example; bilingual, text pre-wrapped into short rows because cells don't wrap). **Every `export` file also ends with a Guide tab** built by the same
  `DataExchangeGuide.BuildGuideSheet` from the sheets it holds — a test fails if any exported column has no guide row, so add a `GuideColumn` there when adding an export column. The seller sends the
  filled file back as a WhatsApp *document* (`WhatsAppWebhookPayload.ExtractDocumentMessages` -> `HandleDocumentMessageAsync`; allowed when idle or at onboarding start/catalog steps; .xlsx only, 5 MB, 5000 rows). Only the
  `Customers` and `Catalog` sheets are read (so an export file from another account imports as is; calculated columns are ignored). The bot shows a summary (new / already there / bad rows with row numbers), and saves after YES
  (`AwaitingImportConfirmation`; the media id is re-downloaded on YES instead of storing rows). **New rows only**: customers match on the last 10 digits of the phone (name when there is no phone; Excel's lost leading 0 and +92/0092/92
  forms are normalised), products on name + unit + pack size; existing ones are skipped, never overwritten, so re-sending a file is safe. One `ActionType.DataImported` log makes "undo" remove the new rows (ones that got orders meanwhile are
  hidden/deactivated instead). Orders, discounts and loyalty rules are export-only for now (their Guide says so) — importing open orders is the next step. No schema change, so no migration.
- Shortcuts (`ConversationEngine.Shortcuts.cs`): after a turn that replied, ended in `Idle` and sent no interactive message, the engine adds a 3-button
  quick bar (Guide / Naya order / Menu; labels are parseable commands). Per-seller `shortcut off|on` lives in `SessionContextData`;
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
- Expenses (`ConversationEngine.Expenses.cs`): "expense 500 packaging" / "kharcha 800 petrol" / "expense packaging 500" saves an `Expense`
  (category = first word of the note, lower-cased; `ActionType.ExpenseAdded`, so one "undo" removes it); "expenses [today|last month]" lists them;
  "monthly net" / "net" / "last month net" = sales (non-cancelled, non-returned orders created in the seller's calendar month, delivery included)
  minus expenses, plus the top 3 categories. Net does NOT subtract product cost — that is the separate "profit" report (hint line points to it).
  No edit/delete of a single expense yet (undo only, while it is the last action); not in `export` yet. `Expenses` is its own table, created on
  Sqlite by the patcher — **no SQL Server migration yet, generate one**. `reset account` deletes expenses.
- Voice notes (ported from PR #9): `HandleAudioMessageAsync` downloads the audio, `OpenAiAudioTranscriber` (`OpenAi:TranscriptionModel`,
  default `gpt-transcribe`, which gets the seller's names as `keywords[]` and `OpenAi:TranscriptionLanguages` (`ur`,`en`) as `languages[]`, retrying without them if the API answers 400; `whisper-1` is shut down by OpenAI on 2027-02-26 and still works via config with the names appended to the prompt, plus `TranscriptionPrompt` steering towards Roman Urdu/English and digits) transcribes it, the bot echoes
  "🎤 Maine suna: …" and handles the text exactly like a typed message. No API key -> the old "send it as text" reply; a failed
  transcription asks to resend and reports OTB-3002. Before handling, `IAiOrderAssistant.InterpretVoiceAsync` rewrites the transcript into what the
  seller would have typed for the bot's current question (`DescribeVoiceSituation`: state, offered options, catalog) — "pehla wala" -> `1`, "haan kar do" -> `yes`,
  "mere paas 4 lawn suit 3500" -> `Lawn Suit - 3500` — and the bot echoes "➡️ Samjha: …". The rewrite is discarded (transcript used) when the AI is
  unavailable or `IsFaithfulRewrite` finds its amounts differ from what was said, both ways: a 3+ digit run is dropped, an amount spoken in words ("teen sau", "three thousand", "تین ہزار"; `SpokenNumbers`) is missing, or an amount (100–999,999) appears that the seller never said — except ids/totals/prices the model was shown (`KnownVoiceNumbers`) and ids dictated digit by digit right after an id word ("order nau nau nau" = 999; `double`/`triple` repeat a digit; "ek do teen piece" stays three counts); an amount the seller took back ("410 nahi, 420", "204 cancel mat karna… sorry 105": a nahi/mat/matlab/sorry/galat within five words, then a different amount within five more) may be left out of the rewrite (`SpokenNumbers.RetractedAmounts`); "sath/saath" is 60 only in "sath hazar" and never after ke/uske/… ("with a thousand"); unknown number words fail safe (transcript used); the rewritten text then goes through the normal deterministic engine. The rewrite uses `OpenAi:VoiceModel` (default `gpt-4o`) and is told to
  understand meaning rather than transliterate sounds. The order analysis also has an `add_products` intent (`AiMessageAnalysis.NewProducts`): a seller naming
  products they sell ("teen chadar aur do dupatte naye products hain") is not an order — priced ones are saved, the rest get a "send the price" prompt
  (`HandleNewProductsAsync`; also reached from the onboarding add-product step when the text has no buyer phone).
  The voice step sees the seller's last 5 orders, saved customers with order history and the last 6 chat messages, and returns typed *steps* (e.g.
  `edit order 12`, `price 1 = 1500`, `done`) or one follow-up *question* when a detail wasn't said. The transcription call gets the seller's customer/product/shop
  names as a hint (`BuildVoiceVocabularyAsync`). For an idle seller, risky steps (`RiskyVoiceCommands`, or edits inside `edit order`) are parked in
  `PendingVoiceSteps` / `AwaitingVoiceConfirmation` until YES (`ConversationEngine.VoiceConfirm.cs`, checked at the top of `HandleIncomingMessageAsync`);
  To see how the real speech model writes numbers (digits vs words, multiplier spellings, number words missing from `SpokenNumbers`) run the local-only `VoiceTranscriptReport`: `VOICE_TRANSCRIPTS=<folder of .txt | text file | app .db> dotnet test --filter VoiceTranscriptReport --logger "console;verbosity=detailed"` (reads `MessageLogs` read-only for a .db; skipped when the variable is unset; phone-length numbers are masked in the output; optional `VOICE_TRANSCRIPTS_REPORT=<file>`). Unknown spellings are added to `SpokenNumbers` by hand from that report — no phonetic mapping (e.g. "saat" 7 vs "saath" 60 would collide).
  a sequence stops if `edit order N` did not open edit mode.
  "Ask for help" phrases ("kya karun", "kaise karun", "kya bolun", "samajh nahi aa raha", "?" — `CommandParser.IsGuidanceRequest`) are checked at the top of
  `HandleIncomingCoreAsync` in every state and answer with a tip for the current step (`ConversationEngine.Guidance.cs`) without changing state; an idle seller gets
  the normal help list. The voice AI maps spoken help requests to the typed phrase "kya karun". The plain words help / guide / menu are unchanged.
- Customer corrections: "Sara ka phone 0300…", "Sara ka address …", "Bilal ki city …", "customer Sara name Sara Khan" (`CommandKind.CustomerUpdate`,
  `ConversationEngine.CustomerUpdate.cs`). Questions ("… kya hai") and non-numeric phones are not treated as updates; an ambiguous name
  lists the matches instead of guessing; each change is undoable (`ActionType.CustomerUpdated`).
- Product details: `Polo Shirt - 500, cost 300, stock 10, color white, fabric: cotton` (`CommandParser.TryParseProductLine`; the first number is the sale price, parts are
  comma/`;`/`|` separated; only cost/stock/color/size/category/sku keywords and `name: value` attributes are accepted, anything else is not a product line).
  Saved on `Product.CostPrice` (seller-only: never on receipts or the shared catalog), `StockQty` (starts the stock tracking), and `AttributesJson` (custom attributes);
  the confirmation echoes what was saved. The catalog export has a "Cost price" column. New columns (`Products.CostPrice`, `Products.AttributesJson`) are added on Sqlite by the
  patcher — **no SQL Server migration yet, generate one**. Profit (`ConversationEngine.Profit.cs`): "profit" (30 days) / "profit today|yesterday|week|last month" / "munafa" = sales (after discount, no delivery) minus cost for non-cancelled/returned orders. Cost is copied to `OrderItem.UnitCost` when an order is saved or an item added in edit (undo restores it); older items fall back to the product's current cost; an order with any item still lacking a cost is excluded and counted, never guessed. `OrderItems.UnitCost` is added on Sqlite by the patcher — **no SQL Server migration yet, generate one**. No per-product profit ranking.
- Webhook acknowledges first, durably: `WhatsAppWebhookController.Receive` validates the signature, parses and gives each message to `WebhookInbox.AcceptAsync`, which writes a `PendingWebhookMessages` row (PK = message id, so Meta
  retries are `Duplicate`) BEFORE putting it on `WebhookWorkQueue` (8 lanes keyed by sender so one seller's messages stay in order, 1000 per lane; a full lane forgets the row and the webhook answers 503 so Meta retries) and returns 200 at once.
  `Api/WebhookWorkerService` handles them with its own scope, a 3-minute timeout per message and the same `WebhookMessageGate` (per-sender lock + id claim), then deletes the pending row; failures are reported/replied as before.
  At start `WebhookInbox.RecoverAsync` re-queues rows whose id was never claimed, and for rows already claimed (handling had begun, so it might have saved an order) does NOT re-run: it asks the seller to resend (OTB-5005) and deletes the row.
  `PendingWebhookMessages` is created on Sqlite by the patcher — **no SQL Server migration yet**.
- Free first sellers + media caps: `Billing:FreeSellerLimit` (appsettings/compose default 100, code default 0 = off): sellers with `Id <= limit` are free for good — `BillingApplies(seller)` (Billing.cs) is false for them, so
  no trial, plan/subscribe text, trial reminder, Pro-only block or "subscribe" menu row (typed "subscribe" answers that the account is free). Seller 101+ follow the normal 14-day trial. `MediaRateLimiter` (singleton, in-memory,
  rolling hour per seller; `MediaLimits:VoicePerHour` (default 30) / `ImagePerHour` (default 10), 0 = unlimited) is checked before the paid transcription / vision call; logo/banner pictures are not counted; a restart resets it.
- Backups (`Infrastructure/Backup`, `Api/BackupService.cs`, `Api/BackupStartup.cs`; README "Backups"): Sqlite only, off unless `Backup:Enabled` + all of FolderId/ClientId/ClientSecret/RefreshToken.
  `BackupService` snapshots with SQLite's online backup API (safe while writing), integrity-checks it (a corrupt snapshot is never uploaded), gzips and uploads to Drive (`drive.file` scope, plain
  HTTP in `GoogleDriveBackupStorage`, OAuth refresh token) when the newest Drive backup is older than `IntervalHours`; prunes to `KeepCount`. `BackupStartup.RestoreIfNeededAsync` runs in `Program.cs` before
  the DB is opened: corrupt/missing file -> newest healthy backup (damaged file kept as `.corrupt-*`), alerts OTB-5002/5003/5004. Data written after the last backup is lost on restore. Runtime corruption
  is only caught at the next start. WAL is enabled at startup (`Database:SqliteWal`). `Orders(SellerId, CreatedAt)` index added — Sqlite gets it from the patcher, **no SQL Server migration yet**.
- Error log (`Infrastructure/Alerts/ErrorLog.cs`, `ErrorLogWriter.cs`, `ErrorMailer.cs`, `Api/ErrorLogService.cs`; README "Error log"): `IssueReporter` adds every issue (not subject to the webhook cooldown) to
  `ErrorLogBuffer`; `ErrorLogService` flushes every `ErrorLog:FlushMinutes` via `ErrorLogWriter` -> daily CSV in `ErrorLog:Directory` (`/data/logs`), mirrored to the Drive subfolder `error-logs`
  (`IDriveLogStore`, implemented by `GoogleDriveBackupStorage`, needs the Backup credentials), and Errors (not warnings) emailed as a digest (min 10 min apart) over SMTP when `ErrorLog:SmtpUser/SmtpPassword/EmailTo`
  are set. The same SMTP settings power `NewSellerEmailNotifier` (`INewSellerNotifier`, called once from `LoadOrCreateSellerAsync` when a seller row is first created; background send, never awaited by the engine).
  `POST /internal/test-error` (header `X-Test-Token`, only mapped when `ErrorLog:TestToken` is set) raises one dummy OTB-1001 for end-to-end checks.
  Failures there are logged only, never reported through `IIssueReporter`. CSV cells are always quoted and a leading `= + - @` is prefixed with `'`.
- Receipt settings / printing / email (`ConversationEngine.ReceiptSettings.cs`, `Infrastructure/Email/MerchantEmailSender.cs`, `Infrastructure/Pdf/ReceiptPdfGenerator.cs`): `receipt size 80mm|58mm|A5|A4`
  (`Seller.ReceiptSize`, null = A5; 80/58mm use `RenderThermal`, a continuous-height one-column layout), `receipt header <line>` (`Seller.ReceiptHeader`, under the shop name) and `receipt contact <line>` (`Seller.ReceiptContact`: email / phones / address, printed under the name; `off` clears, `reset account` clears; the `email` command only sends receipts and is not printed), `receipt footer`, `note 12 <text>` (`Order.Notes`, printed on the receipt), `email <addr>`
  (`Seller.Email`), `email receipt [12] [to addr]`, `printer` (guide), `receipt settings`. `HandleReceiptAsync` and the email path share `RenderReceiptAsync` (serial receipt number given back when the
  send fails). The bot never prints itself: the seller prints the PDF from their phone/computer. Email needs `MerchantEmail:SmtpUser/SmtpPassword` (separate from the founder's `ErrorLog:*` SMTP; the
  service is only registered when set, engine param `emailSender` is optional), 20 emails/seller/hour in memory, default recipient is the seller's own address. No email for exports/summaries yet, no buyer
  email field. New `Sellers` columns: Sqlite patcher adds them, **no SQL Server migration yet**. The thermal PDF layout was written without being rendered: check an actual 58mm/80mm print.
- Labels (`ConversationEngine.Labels.cs`, `ReceiptPdfGenerator.GenerateShippingLabel/GenerateBusinessLabel`): `label 12` / `shipping label 12` / `label Ayesha` = parcel label PDF (from/to, order id, COD amount to collect = unpaid COD balance,
  items, tracking, order note) to the seller's chat; bare `label` asks which label (parcel for the latest order / business sticker / size) as taps; `business label` = shop sticker (logo, name, phone, Instagram, receipt footer as tagline);
  `label size 4x6|a6|50x30` (`Seller.LabelSize`; 50x30 is business-label only, parcel labels fall back to 4x6). Cancelled/returned orders get no parcel label. "mark N shipped" now offers `label N`. Written without being rendered: check a real print.
- Barcode scanner (`ConversationEngine.Scan.cs`): a USB/Bluetooth scanner types the code as a message. `barcode <product> <code>` stores it in `Product.Sku` (unique per seller; several matching products -> tap list), `barcode remove <product>`.
  In `HandleIdleCoreAsync` a message that `LooksLikeScannedCode` AND equals some product's Sku (case-insensitive) is added to `SessionContextData.ScanCart` (one piece per scan); anything else (a phone number, an unknown number) is untouched.
  `scan cart` / `scan clear` / `scan done` (-> `PendingOrder` via `BuildPendingOrder`, then the usual name/phone/confirm steps) / `scan <code>`. Cart prices are list prices (tiers applied when the draft is built). No barcode reading from photos (would need ZXing).
- Cash drawer (`ConversationEngine.CashDrawer.cs`, entities `CashDrawerSession` / `CashMovement`, new tables created on Sqlite by the patcher, **no SQL Server migration yet**): `drawer open 5000`, `cash in|out|sale <amount> [note]`, `cash undo`, `drawer`/`drawer status`,
  `drawer close <counted>`, `drawer history` (also `galla ...`, `cash open|close`). Expected cash = opening + COD orders marked paid since it opened (`PaidAt`, not cancelled/returned; bank/wallet are not cash) + cash in/sales - cash out; stored with the counted amount at close.
  Marking a COD order collected tells the seller what the drawer should hold. `CashDrawer`/`CashMovement` are in `ActionRisk.RiskyCommands` (voice needs YES). No per-cashier users, no float counts by denomination.
- Ambiguity: `CommandKeywordAreas` (Keywords.cs) has areas for galla/drawer, label, barcode/scanner and printer/email, so a message naming a feature that matched no command gets tap options (each a runnable command; a test enforces it). The OpenAI text and voice prompts now say to
  offer every plausible bot action as a typed-command option (<= 20 chars) when a message could mean several things, and list the receipt/label/drawer/scan commands. Follow-ups are pre-empted in the replies themselves (label -> mark shipped / add tracking, scan cart -> scan done, cash entry -> expected total).
- Found by the randomized shop simulation (`RealShopSimulationTests`, `OTB_SIM=1`, pinned in `SimulationFindingsTests`): `cancel 1` / `cancel #1` = cancel order; a typo'd one-word status ("mark 1 shiped") is read as the nearest status
  (`TextDistance`, max 2 edits, never for two words or a short word); a command sent instead of the COD "Cash collect ho gaya?" answer now runs instead of vanishing; product lines accept `Rs.` / `PKR` and thousands commas
  (`CommandParser.NormalizeProductPriceText`) and, during setup, a price in number words ("Shawl - teen hazar do sau"); `create discount: EID10 10%` (`NormalizeDiscountSpec`); an unmatched product name that is a near miss of a catalog
  name gets a "Kya aap \"Polo Shirt\" keh rahe hain?" hint; an unreadable setup price ("Handbag - abc") says which part was not understood. `InternalsVisibleTo` lets tests reach internal helpers.
- Payouts (`ConversationEngine.Payouts.cs`, entity `Payout`, `Order.PayoutId` / `PayoutSettledAt`; new table + 2 Order columns: Sqlite patcher, **no SQL Server migration yet**): money a courier / gateway pays out for COD orders. `payout pending` (= `payout` / `payouts` / `awaiting payout`) lists
  delivered, unpaid COD orders not yet settled, grouped by `TrackingCourier`, with what each should bring (`PayoutReceivable` = total - `TaxWithheld` - advance). `payout 11700 TCS orders 1 2 3 fee 300 [ref X]` records the money, links the orders (all-or-nothing: unknown, cancelled/returned, non-COD or already
  settled orders refuse the whole payout), marks them PAID and says "Barabar" or how much KAM/ZYADA arrived against fee + total; without orders it hints which pending orders of that courier add up to the amount. `payout skip 12 13` = cash came directly (no payout expected), `payout list`, `payout undo`
  (restores each order's payment status / amount / paid date from `Payout.UndoJson`). Courier-settled orders (`PayoutId` set) never count as cash-drawer money. In `ActionRisk.RiskyCommands` (voice needs YES). Paying staff/suppliers is not payouts: use wages / expense / cash out.
- Offline sales (`ConversationEngine.WalkIn.cs`): `walkin Kurti 2, Dupatta` / `counter sale 1500` / `offline sale Chai - 200 bank kal` (also `dukan par ...`) makes an order draft for the shared customer "Walk-in" (no phone asked, no duplicate warning),
  `OrderSource = "offline"`, `PendingOrderData.PaidInFull` -> saved after YES as Delivered + Paid (`PaidAt = CreatedAt`); cash = `OrderPaymentMethod.Cod` so it counts as cash-drawer money, bank/easypaisa/jazzcash/card = `Manual`; `kal`/`parso` backdate (`SoldAt`). Items: catalog names (unmatched ones use the usual add/map flow),
  `name - price` for ad-hoc items, a bare amount = "Counter sale". In `ActionRisk.RiskyCommands`. Delayed delivery: Meta's message `timestamp` is carried as `WebhookMessage.SentAt` (inside the stored JSON, no schema change) into `HandleIncomingMessageAsync(..., sentAtUtc)`; `RecordTimeUtc()` stamps orders and expenses with the
  real send time when the phone was offline (ignored if in the future or older than 3 days). Not yet applied to cash movements, payments, status changes, voice notes or images. WhatsApp itself queues messages while the phone is offline; the bot cannot record anything the seller never sent.
- Next-step suggestions (`SuggestNext` in `ConversationEngine.Shortcuts.cs`): after a turn, the quick-action bar shows up to 2 buttons that fit what was just done plus Menu, instead of the generic Guide / Naya order / Menu (still gated by `Features:ShortcutButtons` and `shortcut off`, Idle state, text-only reply).
  Product saved -> `walkin <name> 1` / `catalog`; customer corrected -> `order for <name>` / `<name> ka order`. A button title is max 20 chars: a longer command is dropped, never cut; every label must parse as a command (`SuggestNext` checks).
  New command `order for Sara` / `Sara ke liye order` (`CommandKind.OrderForCustomer`): arms `SessionContextData.OrderForCustomer` (10 min); the next idle message that is items ("Kurti 2, Dupatta", "Lawn Suit - 3200") becomes that saved customer's draft with phone/address already filled (a real command, yes/no or anything else drops it and runs as usual).
  Add `SuggestNext(...)` after any other handler whose natural follow-up is predictable.
- Menu (`MenuText`, `MainMenuSections`, `MenuCategories` in `ConversationEngine.Commands.cs`): 8 categories (orders, reports, catalog, payments, discounts, customers, **shop** = counter & dukaan, settings); a WhatsApp list allows max 10 rows, titles <= 24 chars, body <= 1024 UTF-16 chars (tests enforce all three, and that every row id parses as a command). The main-menu body is a one-line-per-category overview (it was 1073 chars before, over the limit): put details in the category body, not in `MenuText`. Add new features to the matching category's body + a row, and to `shop` if it is counter/hardware related.
- Stuck-draft escape + menu wording: while an order draft waits on a product question (`ProductChoice` / `MappedProductName` / `NewProductPrice`), a message that is a real command (and not a `1`/`2`/price/catalog-name answer) drops the draft and runs the command (`HandleOrderMissingFieldAsync`) — before this, every message was searched as a catalog name ("... catalog mein nahi mila" forever). `naya|nayi|new product: <line>` (colon required) is read as a product line (`CommandParser.NewProductPrefix`), since the Catalog menu advertises it.
- A product line that ends in `, stock N` ("Kurti - 1800, stock 10") is a product, not the "Kurti stock 10" stock command: `StockSet` is skipped when `TryParseProductLine` accepts the message (before this, the bot answered `"Kurti - 1800," catalog mein nahi mila`).
- Adding a product that already exists (same name + pack) says so (`HandleAddProductAsync`, `_upsertBefore`): "pehle se catalog mein hai — koi tabdeeli nahi" when nothing changes, "price updated: old → new" when the price moves, "details update" when only cost/stock/etc. were sent, "wapas chalu" for a switched-off product. It used to answer "price updated: Rs.1,800" even when nothing changed.
- `TryParseProductLine` refuses a name with no letters or ending in `-`/`–`/`=` ("Kurti - -5" used to save a product called "Kurti -" at Rs.5). A price of 0 is allowed on purpose (free sample products).
- Price for a waiting new product: when exactly one new product waits for its price (`PendingPriceProducts`, set by the `add_products` flow) a message that is only an amount (`9000`, `Rs. 9,000`, `9000 rupay`; `BareAmount` in `HandleIdleCoreAsync`) saves it through `HandleNewProductsAsync` — plain code, no AI guess. Before, the bare number went to the AI, which answered "Aap kya kehna chahte hain? 1 create discount …". With several products waiting, the AI path still decides.
- New-products reply (`HandleNewProductsAsync`): says "yeh naye products hain:" (no "order nahi" disclaimer) and lists names that were already in the catalog ("Pehle se catalog mein hain: Dupatta (Rs.600)") instead of silently skipping them.
- Bad-input guards found by a stress run (`BadInputGuardTests`): incoming text goes through `TextHygiene.StripInvisible` (zero-width space, LRM/RLM, bidi controls, BOM removed — they made "\u200bKurti" a second, look-alike product; ZWNJ/ZWJ are kept for Urdu spelling); `create discount` refuses percent outside 1-100, flat <= 0 and a code that already exists; the walk-in parser drops "Kurti -2", names ending in a dash, quantities > 999 and amounts > Rs.10,000,000; the stock regexes no longer split a long number ("stock Kurti 99999999" used to become product "Kurti 99" + 999999).
- Message tone (seller-facing Roman Urdu): polite and plain. "Aap" form; "Maaf kijiye, ..." when the bot did not understand or cannot do something; "Barae meherbani ..." before a request; one standard spelling ("sakte", not "saktay"); an icon for the kind of message (✅ done, ℹ️ information, ⚠️ problem or warning, 🙏 apology); every "not found" says what to type next ("Order #12 nahi mila. Apne orders dekhne ke liye \"orders today\" likhein."); no slang for system actions ("mansookh kar diya", not "chhod diya"). Tests assert short stable fragments ("nahi mila", "samajh nahi aaya"), so keep those words when polishing a message.
- A counter sale can carry its own confirmation (`walkin Kurti 1 then yes (cash)`, `... yes`, `..., haan`): `WalkInSale.Confirm` saves the draft at once when it is ready (a draft with an unknown product still asks its question and is not saved). Parentheses around a payment word are ignored.
- Fixes from a live test chat: the AI `add_products` path never silently overwrites the price of an existing product (it keeps the price and shows `edit product: Name - N`); a trailing bracketed remark `(confirm|yes|ok|haan|done)` is ignored by the parser (`cancel 3 (confirm)` = `cancel 3`, which still asks before cancelling); a phone read out of an order message is checked with `IsPlausiblePhone` (10-13 digits, no letters) and asked again instead of saved ("030011122" used to be accepted); `create discount: SAVE200 200 off` = Rs.200 flat. Manual-test-plan rows that meant "send as separate messages" now say so.
- The WhatsApp list body (help) must stay ≤ 1024 chars — a test enforces it; keep `HelpText` curated rather than exhaustive.
- Receipt design (`ConversationEngine.ReceiptDesign.cs`, `Application/Abstractions/ReceiptDesign.cs`, state `AwaitingReceiptDesign`): `receipt design` opens a live design mode — one instruction per message (`color blue|#RRGGBB`, `title Invoice`, `layout center|classic`, `logo|phone|buyer|status on|off`, `text small|normal|large`, `size 80mm`, `footer ...`, `preview`, `reset`, `done`),
  each saved at once on `Seller.ReceiptDesignJson` (null = standard look; damaged JSON falls back to standard) and answered with a fresh preview PDF of the latest order (or a sample when there is none; a preview never takes a receipt serial number). A real command leaves the mode and runs; the design stays saved.
  Applies to A5/A4 and thermal receipts (`ReceiptData.Design`; thermal ignores colour/layout, tax invoices keep the "TAX INVOICE" title). Column is added on Sqlite by the patcher — **no SQL Server migration yet, generate one**. `reset account` clears it. Labels are not affected.
- Wording pass (menus/guide): menu categories use plain English names (Orders, Reports, Catalog, Payments, Discounts, Customers, Counter Sales, Settings, Business Setup, Language); descriptions are polite Roman Urdu ("Barae meherbani", "likhein", no slang like Maal/Paisay/Chhoot/Gahak/galla/bikri). Tests pin "Main Menu", "Zaroori Madad", "Step 1/4", "FREE trial", "account free hai".
- Receipt footer: free text up to 200 characters and 4 lines (`receipt footer ...`, or `footer ...` inside `receipt design`); line breaks in the message are kept and printed as separate lines, blank lines dropped; longer/more lines are refused, never cut.
- Draft confirmation buttons (`SendDraftConfirmationAsync`, `ConfirmButtonLabels` in `ConversationEngine.OrderFlow.cs`): the order draft ("Confirm order") is sent with `✅ Save` / `✏️ Edit` / `❌ Cancel` reply buttons instead of the typed "Reply YES to save, ya EDIT to fix." line (flag `Features:ConfirmButtons`, default on; an engine built without `FeatureOptions` keeps the typed prompt). A tap arrives as its label and is mapped to `yes` / `edit` / `cancel` at the top of `HandleIncomingMessageAsync` (translated labels are mapped back by `TranslatingSender`); typed yes / edit / cancel still work. A tap with no draft waiting says the button was for an old order and saves nothing. A draft body over 1000 chars falls back to the typed prompt. "Edit" discards the draft (the seller resends corrected details); it does not open an editor.
- `version` (also `build`, `bot version`; `BuildInfo` in `Application/Conversation`, `CommandKind.Version`): replies with the deployed build and when the process started. The value comes from `Build:Version` / env `Build__Version` (fallback config key `GIT_SHA`), set at image build from the Docker build argument `GIT_SHA` (`docker-compose.yml` passes `${GIT_SHA:-unknown}`; run `export GIT_SHA=$(git rev-parse HEAD)` before `docker compose up -d --build`). A 12+ hex SHA is shortened to 7 characters; empty, "unknown" or any value with spaces/odd characters reads as "unknown". It names the deployed artifact, never a git checkout. Not part of the quick bar; listed under Settings.
- Did-you-mean taps (`BuildSuggestions`, `TextDistance.Nearest`, `ProductSuggestion`, `ContinueResolvingDraftAsync`): when an order names a product that is not in the catalog and 1–3 catalog names are near misses (≤2 edits and ≤ a third of the length; same rule as the old single hint), the "nahi mila" question is sent with one button per product, titled "Name Rs.1800" (cut with … to the 20 characters a button holds), plus "➕ Naya product" when there is room. A tap is mapped at the top of `HandleIncomingMessageAsync` to the typed answer "2 <catalog label>" / "1" via `SessionContextData.ProductSuggestions` (kept after use so a later stale tap says "purane sawal ka tha" instead of being read as a message). Flag `Features:SuggestionButtons` (default on; engines built without `FeatureOptions` keep the typed hint "Kya aap \"Kurti\" keh rahe hain? ... 2 Kurti"). Deterministic: no AI call.
- Usual items after `order for Sara` (`UsualItemsAsync`, `HandleOrderForCustomerAsync` in `ConversationEngine.WalkIn.cs`): the "items likhein" prompt gets up to 3 buttons titled "Name Rs.price" — the products most often bought in the customer's last 3 orders (cancelled/returned ignored, ties to the more recent order, switched-off products skipped; plain database history, no AI). A tap is mapped at the top of `HandleIncomingMessageAsync` to the product label (quantity 1) and goes through `TryHandleOrderForCustomerItemsAsync` like typed items, ending at the usual Save / Edit / Cancel draft. The titles are stored in `SessionContextData.ProductSuggestions` (shared with the did-you-mean taps), so a tap after the request is used or expired says "purane sawal ka tha". Same flag as did-you-mean: `Features:SuggestionButtons`; no history or flag off = the plain prompt. No hit-rate counters yet.
- Aaj ka hisaab: `aaj ka hisaab` / `hisaab kitab` / `آج کا حساب` / `daily account` (all `CommandKind.TodaysSummary` for today) now also shows the product sold the most (units, from the day's counted orders), the day's expenses (total + the biggest note) and the cash drawer's expected cash (`DayAccountLinesAsync` in `ConversationEngine.Commands.cs`; "drawer band hai" when none is open). Same plain SQL as the other reports: no AI computes a number. Counted = saved, non-cancelled, non-returned orders (pending ones included); an unconfirmed draft is not an order and is not counted. "kal ka hisaab" and other periods keep the plain summary. The reply carries a follow-up bar `orders today` + `drawer` (or `expenses today` when no drawer is open) + Menu via `SuggestNext`. The voice prompt maps "aaj kitna bika / aaj ki report / aaj kitna kaam hua" to the typed phrase "aaj ka hisaab" (the model only chooses the command). Reply is text only (no text-to-speech).
- Command dictionary: `docs/command-dictionary.md` lists every command phrase (and the yes / no / cancel / done words) in seller language; `CommandDictionaryTests` parses each documented phrase and fails if it stops meaning what the dictionary says. Add a phrase to both when you add or rename a command.
- Outbound size limits (OTB-2001 "Body text length invalid. Min 1, Max 1024", seen live): WhatsApp refuses an interactive body (buttons / list / flow) over 1024 characters or empty, and a text over 4096. `WhatsAppSender` now sends a buttons/list message whose body does not fit as a plain text (buttons listed after it) without a failed call first, never sends an empty text, and splits a text over 4096 at line breaks (nothing dropped). `TranslatingSender` keeps the original (Roman Urdu) body when the translation of a buttons / list / flow body is longer than 1024 — the likely cause of the live warning (an English / Urdu-script translation of a ~1000-char menu or help body). Tests: `OutboundLimitsTests`. Keep new interactive bodies short anyway; the list/menu tests still enforce ≤ 1024 for the Roman Urdu text.
- Margin report (`margin`, `margin list`, `profit margin`, `discount room`; `ConversationEngine.Margin.cs`, `CommandKind.Margin`): for every active product with a saved cost, margin = (price − cost) / price, a "munasib discount" = half the margin (a guide, said so in the reply), the break-even discount ("is se zyada nahi" = the margin itself), and units sold in the last 30 days (cancelled/returned excluded). Best margin first, top 10; products without a cost are counted and told how to add one, never guessed; a product at or below cost is flagged. Plain arithmetic, no AI. Why it exists: a live voice note asked "which products leave me margin if I discount, and how much discount should I give" and the voice rewrite turned it into `create discount`; the voice prompt now maps that analysis question to `margin` and says never `create discount` unless a code is clearly wanted. Not covered: delivery/expenses per product, per-period margins, a minimum-margin setting.
- Voice guard for the margin question (`ConversationEngine.CorrectMarginQuestion`, applied in `InterpretVoiceAsync` before the faithfulness gate): if the rewrite is a bare `create discount` step, the spoken text has a margin / profit word (margin, profit, munafa, مارجن, پرافٹ, منافع …) and nothing that makes a code (a code, coupon, expiry, percent, a digit), the step becomes `margin`. A plain text rule, so it holds whatever the model returned; other steps are never touched. Deliberately NOT redirected to `aaj ka hisaab` (that report has no per-product margin).
- Custom date question (`AwaitingCustomDateRange`, `HandleCustomDateRangeAsync`): besides "1 May se 15 May" it reads open ranges "1 January se aaj / ab / abhi tak", "since 1 January", "… to now" (`TryParseOpenRange`; a day said "yakum" / "یکم" is 1 — also added to `SpokenNumbers`). A reply that is a real command ("summary last week", "orders today") leaves the question and runs; "cancel" leaves it; anything else keeps asking with a clear example (it used to drop the state and then reject the next command as "Date range samajh nahi aayi"). In that state a voice rewrite skips the money-faithfulness gate (dates add digits legitimately).
- Category sales (`sales by category [period]`, `category wise sales`, `category sales`, `category ki sale`; `ConversationEngine.CategorySales.cs`, `CommandKind.CategorySales`): item price × quantity of active orders per `Product.Category` (case-insensitive; products without one and items with no product go under "Bina category" with a hint to set it), biggest first, default window the last 30 days, any `ReportPeriods` phrase after the words ("this week", "last month"). Discount and delivery are not spread over items, so the total can differ from the summary (the reply says so). The voice prompt maps "kapron se kitni sale hui aur grocery se" to it instead of asking which category.
- Product report (`<product> ka report`) now compares this month so far with the same number of days of last month (verdict behtar / kam / barabar / milajula, only "behtar"/"kam" when units and amount agree) and shows last month in full. The voice prompt maps "polo shirt ki sale is mahine behtar hai ya pichle se kam" to `Polo Shirt ka report`.
- Business questions in the seller's own words (`InsightParser`, `ConversationEngine.Insights.cs` / `Insights2.cs`, `CommandKind.Insight`; list in `docs/command-dictionary.md`): a topic is recognised from two or more distinctive words in any order (English / Roman Urdu / Urdu script), checked after every exact command (`CommandParser.ParseCore`, right after `TryParsePeriodReport`), and the period is the longest run of words `ReportPeriods` understands (future periods are ignored: "aane wale customers" is not "tomorrow"). `ParsedCommand`: `Text` = topic, `Text2` = period key, `Text3` = the sentence (or the place / expense category), `Amount` = percent or days. Topics: `avg`, `pricey`/`cheap`, `priceup` (margin < 20% flagged, price for 30% suggested), `whatif` (discount % on one product), `discgiven`, `inactive`, `topcustomers`, `repeat`, `newcustomers`, `balance` (unpaid / part-paid orders, named customer or everyone), `lastbought`, `paysplit` (COD / manual / gateway), `area` (customer City or Address contains the place; no place = per city), `returns`, `deliverytime` (ShippedAt -> DeliveredAt per courier, only orders marked both ways), `stockending` / `stockdays` / `stocklevel` / `reorder` / `untracked` (pace = units sold in the last 30 days, or since the product was added, at least 7 days; guide only), `expcat`, `exptop`, `vsyesterday` (same time of day), `vsprev` (week or month), `pnl` (product profit minus expenses; orders lacking a cost are left out and counted), `productcompare` (runs the product report). Questions that existing reports already answer are mapped to them (`Margin` low-first, `SlowMovers`, `TrendingProducts` incl. winter/summer ranges, `TodaysSummary` with a period, `Profit`, `ExpenseList`, `CategorySales`, `CashDrawer`). Product, customer and courier names are found in the database by the engine (whole words, near misses for products), not by the parser. All figures are plain SQL / arithmetic; the voice prompt only tells the model to return the question as plain words. Supplier lead time: `maal aane mein 5 din lagte hain` (`leadtime`) is kept in `SessionContextData.SupplierLeadDays` (no schema change) and the re-order advice / `stockending` default use it. Delivery time falls back to order date -> delivered when "shipped" was never marked (the reply says how many). Area match (`PlaceMatches`) ignores spacing/case and allows one letter off for places of 6+ letters (two for 10+); short names match whole words only. Not done: seasonal comparison needs a year of data, "kal ka mukabla" for profit, per-product delivery time. Add a phrase to `InsightParserTests` and the dictionary when you extend a topic.
- Advanced business questions (`AdvancedInsightParser` is tried first inside `InsightParser.TryParse`; handlers in `ConversationEngine.Insights3.cs` / `Insights4.cs`): `pairs` (products sharing an order; one named product = its partners, two = both / only-A / only-B, none = best pairs; window default 180 days), `nosolo` (3+ orders, 80%+ shared), `bundle` (most frequent pair, 10% off as a worked example, margin from `UnitCost`/`Cost`), `churnrisk` (3+ orders, silent >= N days (default 60) AND >= 2x their own average gap; wa.me link for the first), `aovdrop` (average order of the last 30 days <= half of the 30 before), `winback` (arithmetic + how often 60+ day silences ended in an order before; says it is not a forecast), `pattern` (gap, week of month, usual items, next expected date), `costup` (same margin % / same profit per piece), `priceeffect` (profit per piece before/after and how far sales may fall), `costlylow`, `suppliers` (groups by the `supplier:` / `vendor:` product attribute in `AttributesJson`), `courierreturns`, `returnloss` (delivery charges on returned / shipped-then-cancelled orders — the real courier bill is not stored), `citydelivery` (needs `Seller.City`), `cancelbymethod`, `drawershort` (closed drawers: counted - expected), `uncollected` (delivered, balance > 0, not settled by a payout), `stuckmoney`, `expenseratio`, `lockedcapital` (tracked stock x cost for products with no sale in N days), `overstock` (90+ days of stock), `seasonal` (Ramadan / Eid ul-Fitr window from the Umm al-Qura calendar — Pakistan's moon sighting can differ by a day; compares with the 30 days before), `categoryshift` (category share of sales in two months), Eid-aware `stockending` (days until the next Eid). `ProductsInTextAsync` also finds a misspelt product next to an exact one. Not done: supplier on orders (only the product attribute), a real courier-cost field, predicting how buyers react to prices or discounts (the replies give the break-even instead).
- Hyper-complex business questions (`AdvancedInsightParser` first block "hyper-complex scenarios", handlers in `ConversationEngine.Insights5.cs` / `Insights6.cs`; all tested in `HyperInsightParserTests` / `HyperInsightReportTests`): `audit` (= `business audit`: locked capital 90d, stock ending within 7 days, money owed incl. 30d+, products below 20% margin, expense share, quiet regular customers; the first two flagged lines become the quick-bar buttons), `custprofit` (per customer: profit after discount minus delivery charges on returned parcels; one named customer = cumulative, none = big spenders with the lowest net), `retention`, `vipshare`, `velocitydrop` (units of last D days vs the D before, drop >= N%, prior >= 4), `fastmoving` / `daysonhand` / `outofstockhot` (pace from `StockRowsAsync`; the bot cannot see buyers' chats, the reply says so), `margindrop` (average margin from each order's saved `UnitCost`, 30d vs the 30 before), `costshock` (cost +N% -> margin, break-even = 30-day expenses / margin, optional category), `costdrop`, `failcity`, `rtostock` (returned units go back to tracked stock automatically; damaged / lost goods come from `Loss` entries), `chargeeffect` (history of orders by delivery charge — never a forecast), `cashflow` (orders marked paid in the period minus expenses), `cashplan` (expected drawer cash vs a supplier payment the seller states), `promoroi` / `promonew` / `promoloyalty` (discount codes: customers with an order in the previous 60 days are flagged as "would likely have bought anyway" — an estimate), `eidstock` (last Eid window vs the 30 days before; Umm al-Qura dates), `breakevendaily` (rent / bills over the margin), `declining` (three falling 30-day windows), `pricemix` (premium = catalog median price or more). `uncollected` also takes a courier name ("TCS ke paas"). `drawershort` takes "N din" (also "دنوں"). Category words are matched to product categories with a small synonym list (`CategoriesInTextAsync`). Not possible: counting buyers who asked about an item (their chats never reach the bot), predicting how buyers react to a price or charge.
