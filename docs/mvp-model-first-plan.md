# MVP plan: the model understands, the code checks

Status: **plan only, no code written.** Supersedes the order of work in `dynamic-understanding-plan.md` (that plan keeps "lists first, AI on failure"; this one reverses it for the MVP jobs).

## 1. Why the current design cannot launch

Numbers from the code today:

| What | Count |
|---|---|
| Regex uses in `CommandParser` / insight parsers | 319 in `CommandParser.cs` alone (533 across Conversation + Ai, per the earlier scan) |
| Hand-kept word lists | 73 lists, about 1,200 entries |
| Catalog intents (`intents.json`) | 249 |
| Separate AI methods on `IAiOrderAssistant` | 19, each added to patch one gap |
| Conversation code | about 25,800 lines in 119 files |

How an idle message is read now (`HandleIdleCoreAsync`): five special-case checks, then `CommandParser`, then three "learned phrase" layers, then an AI second opinion on loose rules, and only then the AI order reader, then the intent router. The router's answer is turned back into typed text that must pass `CommandParser` again.

Result: the bot can never understand more than the parser. Each new seller sentence needs a new rule, and each rule can collide with an older one.

## 2. Target design

One understanding step for every text and every voice transcript:

```
message / transcript
   -> fast path (code): button tap, bare number, yes/no, exact command
   -> ONE model call: sees message + open question + catalog (top matches) + last 5 orders + last 6 chat lines
        returns: one action + typed fields + the seller's own words for every number + (optional) one question
   -> code: validate fields against the database, do all maths
   -> writes: show draft with Save / Edit / Cancel       reads: answer at once
```

Rules:

1. **The model chooses an action and fills fields. It never writes a command string.** The engine builds the `ParsedCommand` / order draft object directly, so the parser is no longer the ceiling.
2. **Code owns facts.** Product, customer and order ids are resolved in the database. Totals, discounts, stock and reports stay plain code (unchanged).
3. **Every number carries its quote.** Amount, quantity, phone and order id come with the exact words they were read from; code checks the quote is in the message (already done for product prices: `PriceWords`, `NumberHasSupport`).
4. **Doubt means a tap, not a refusal.** A failed check shows what was understood with buttons. "Dobara bhejein" is only for an empty or unusable transcript.
5. **Every write is confirmed** (Save / Edit / Cancel, or Yes / No). This replaces the voice amount guard as the safety net.
6. **The fast path is small and exact:** button labels, digits, yes / no words, and the fixed commands (`orders today`, `mark 3 shipped`, `undo`, `menu`). Loose keyword rules (`Insight`, `FuzzyStatusUpdate`, `CustomerOrderLookup`, `ProductReport`, `CustomerSearch`, `OrderSearch`, `HowTo`) leave the front of the line.

## 3. MVP scope: five jobs, twelve actions

| Job | Actions the model may pick |
|---|---|
| Save an order | `create_order` (customer, items, phone, address, payment, delivery, advance) |
| Order status | `update_status` (order or customer, status), `cancel_order` |
| Payment | `record_payment` (order or customer, amount or full) |
| See the day | `show_orders` (period, status, customer), `show_order`, `show_summary` (period) |
| Catalog | `add_product` (name, price, cost, stock), `show_catalog` |
| Conversation | `answer` (reply to the open question), `undo`, `help_or_chat` |

Everything else (discounts, loyalty, payouts, drawer, labels, insights, import, Instagram) stays behind `Features:MvpOnly` and answers "abhi band hai". No new regex, phrase or intent is added while this plan runs.

## 4. Phases

Each phase ends with a check. Stop if the check fails.

### Phase 0: measure first (no behaviour change)

- Build `tests/.../Fixtures/real-messages.json`: 300 messages with the correct action and fields. **Decision (2026-11-10): test docs only, no production data.** Sources: `docs/complex-order-test-cases.md` and `-part-2.md` (about 200 rows, with catalog and expected result), `docs/real-world-test-scenario.md`, `docs/industry-test-cases.md` (about 530 rows, only the five MVP jobs are taken), `docs/live-test-cases.csv`, `docs/test-run/system-test-master.csv` (voice rows). Rows for features outside the MVP are left out.
- **Limit of this choice:** these messages were written by us, not by sellers, so they are cleaner than real speech and the score will read high. Treat the Phase 1 pass mark as necessary, not sufficient; the real test is the Phase 4 pilot, and every pilot miss is added to the file. Voice rows are typed transcripts, so speech-to-text errors are not measured.
- Mix: about 40% orders, 20% status / payment, 20% questions, 10% catalog, 10% chat or unclear; half typed, half voice transcripts; Roman Urdu, Urdu script, English.
- Add `UnderstandingEval` (skipped unless `OPENAI_API_KEY` is set, like `VoiceRewriteEval`): runs a pipeline over the file and prints pass rate per action, wrong-action rate, wrong-number rate, calls and cost per message.
- Run it on **today's pipeline**. That number is the baseline.

Check: a baseline percent exists. Needs an `OPENAI_API_KEY` to run; nothing else from you.

### Phase 1: the one call, in shadow

- New `ITurnInterpreter.UnderstandAsync(context, text)` returning `TurnAction` (action, fields, quotes, question, options). Strict JSON schema output; an unknown action or a field of the wrong type is discarded.
- Context builder reuses what exists: `DescribeVoiceSituation`, `BuildRouteContextAsync`, recent orders, known customers, catalog cut to the nearest names.
- Config `OpenAi:UnderstandModel`. Run the eval on two or three models and pick by score and cost; do not assume `gpt-4o-mini` is enough.
- Flag `Features:ModelFirst` = `off | shadow | on`. In `shadow` the old pipeline answers the seller and the new decision is only logged next to it.

Check: eval score on the 300 messages at or above 90% right action, under 2% wrong number, zero writes without confirmation.

### Phase 2: execution for the five jobs

- `TurnActionExecutor`: maps each action to the existing handlers (`BuildPendingOrder`, status change, `MarkFullyPaid`, `HandleAddProductAsync`, report handlers) by building their inputs in code.
- Mid-flow states use the same call with `answer` plus the open question, replacing `ClassifyMidFlowReplyAsync`, `ReadYesNoWithQuoteAsync`, `ReadChoiceWithQuoteAsync`, `ClassifyYesNoAsync`.
- Engine tests for each action with a fake interpreter (no API key needed in CI).

Check: `Features:ModelFirst=on` passes the existing order, status, payment and summary engine tests.

### Phase 3: voice on the same path

- Transcript goes to the same call. `InterpretVoiceAsync` rewrite, `ClassifyVoiceKindAsync` and the `IsFaithfulRewrite` refusal are bypassed under the flag.
- Echo stays ("🎤 Maine suna: ..."), followed by the draft or answer.
- A number whose quote is missing from the transcript is shown highlighted in the draft for the seller to confirm, not refused.

Check: voice half of the eval at or above 85%; refusal rate under 5%.

### Phase 4: pilot

- Turn on for the 20 free sellers (`Billing:FreeSellerLimit`).
- Watch weekly with the existing pilot report: not-understood replies, orders edited or cancelled within an hour, undo count, voice-review verdicts.
- Every miss goes into `real-messages.json` as a new row, then the prompt is adjusted and the eval re-run. Never a new regex.

Check: two weeks with under 10% not-understood and under 3% wrong orders.

### Phase 5: clean up (after the pilot holds)

- Delete the loose-rule parsers, learned-phrase layers, word lists that only fed them, and the unused AI methods. Keep the exact-command fast path and all report code.

## 5. What stays untouched

Order, payment, stock, discount and report logic; templates and translations; webhook inbox and gate; draft confirmation buttons; undo; billing; the media rate limiter.

## 6. Cost and speed

Today a hard message can cost up to four model calls (rule check, order reader, router, translation) and a voice note up to four more. The target is one call per message that misses the fast path. A stronger model costs more per call; the eval prints cost per message so the choice is made on numbers. The hourly USD cap per seller already exists.

## 7. Risks

| Risk | Answer |
|---|---|
| Model picks the wrong action | Writes always confirm; reads are harmless; wrong-action rate is measured before launch |
| Speech-to-text hears a number wrong | The quote will also be wrong, so the draft shows the number for confirmation |
| Model outage | Fast path still works; other messages get "thori dair baad" instead of a guess |
| Eval set too small or too clean | It grows from every pilot miss |
| Deployment database | `appsettings.json` says SQL Server with auto-migrate while several tables have no SQL Server migration: settle Sqlite vs SQL Server before the pilot |

## 8. Decisions needed from you

1. ~~Agree the five jobs and that nothing else is worked on until the pilot.~~ **Agreed (2026-11-10).** Scope is the five jobs (order, status, payment, day summary, catalog) plus the supporting actions they need (`answer`, `undo`, `help_or_chat`). Nothing else is worked on until the pilot (Phase 4) holds.
2. ~~Provide real messages for Phase 0~~ Decided: test docs only (see Phase 0 limit).
3. Budget ceiling per seller per month for the model, so the model choice has a limit.
