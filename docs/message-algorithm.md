# How a message is handled (text and voice) — the algorithm

Written from the code on `main` (after PR #98, `6e4b5e4`). File names are given so every step can be checked. Anything marked **(not verified)** was not read line by line.

## 0. Overview

```
WhatsApp ──► webhook ──► inbox (DB row) ──► per-seller queue ──► worker ──► ConversationEngine
 text / voice / image / document / button tap                                   │
                                                                                ├─ text  : HandleIncomingMessageAsync
                                                                                ├─ voice : HandleAudioMessageAsync ──► (rewrite to text) ──► the SAME text path
                                                                                ├─ image : HandleImageMessageAsync (AI vision) ──► order draft
                                                                                └─ doc   : HandleDocumentMessageAsync (import xlsx/csv)
```

Three kinds of "brains", cheapest first:
1. **State** — what the bot asked last (`ConversationSession.State` + `SessionContextData`). A reply is first read as the answer to that question.
2. **Rules** — `CommandParser` (fixed phrases and patterns, no AI, no cost).
3. **AI** — OpenAI, only when 1 and 2 do not settle it (order reader, mid-flow router, voice rewrite, translation).

## 1. Transport (never changes the meaning of a message)

1. `WhatsAppWebhookController.Receive` checks the signature, parses the payload.
2. `WebhookInbox.AcceptAsync` writes a `PendingWebhookMessages` row (PK = message id, so a Meta retry is a duplicate) and puts it on `WebhookWorkQueue` (8 lanes, keyed by sender: one seller's messages stay in order). Answers 200 at once.
3. `WebhookWorkerService` takes it, gives it a 3-minute timeout and its own scope, runs it through `WebhookMessageGate` (per-sender lock + a claim on the message id), then deletes the row. Slow messages are logged (`handled in … ms`).
4. On restart, rows never started are queued again; rows already started are NOT re-run (they may have saved an order): the seller is told the message could not be finished ("voice note" for audio), is shown the orders saved in the last 10 minutes (so nothing is saved twice) and is asked to send it again (OTB-5005).

## 2. Text path — `HandleIncomingMessageAsync` → `HandleIncomingCoreAsync`

Checked **in this order**; the first rule that handles the message ends the turn.

| # | Step | Where | What it does |
|---|---|---|---|
| 1 | Voice YES/NO | `TryHandleVoiceConfirmationAsync` | If steps from a voice note are parked, YES runs them, NO drops them |
| 2 | Clean the text | `TextHygiene.StripInvisible`, `\n` → newline | Removes zero-width marks; typed backslash-n becomes a line break |
| 3 | Log inbound | `MessageLogs` | Kept for context (last 6 messages go to the voice AI) |
| 4 | Seller status, usage caps | `TryTakeUsage` | Cancelled/suspended seller; text count and USD budget per hour |
| 5 | Pending product line | `TryHandlePendingProductAsync` | A product line waiting for YES/NO |
| 6 | Tap mapping | in `HandleIncomingCoreAsync` | Did-you-mean product buttons, "usual items" buttons, Save/Edit/Cancel buttons become typed words; a stale tap says "purane sawal ka tha" |
| 7 | Help phrases | `CommandParser.IsGuidanceRequest` | "kya karun", "?" → tip for the current step, state unchanged |
| 8 | Reset / dashboard login | `CommandKind.ResetAccount/ClientLogin` | Work in every state |
| 9 | Onboarding | `HandleOnboardingAsync` | If setup is not finished (shop name, kind of shop, products …) |
| 10 | Billing | `TryHandleBillingAsync` | Expired trial blocks everything except reset |
| 11 | **Draft expiry** | `DraftExpiry` (20 min) | An order draft waiting longer is dropped, seller told |
| 12 | Draft state without a draft | | Resets to Idle |
| 13 | **Leave the draft** | `TryLeaveOrderDraftAsync` | In the 4 draft states, "cancel" or any real command drops the draft and runs |
| 14 | **AI router** | `TryRouteMidFlowReplyAsync` | See section 4: new order / cancel / skip phone leave the question; a side question or command **pauses** the order and resumes it |
| 15 | **State switch** | `HandleIncomingCoreAsync` switch | The handler of the state the bot is waiting in (about 25 handlers, section 5) |
| 16 | **Idle** | `HandleIdleCoreAsync` | See below |

### Idle (`HandleIdleCoreAsync`)
1. `TurnGate.DecideTyped`: a "taken back" command ("order 12 cancel nahi") is refused.
2. `TryHandleOrderForCustomerItemsAsync`: after "order for Sara", items become Sara's draft.
3. `TryHandleItemsOnlyAsync` (plain code, no AI): `negative quantity` ("Kurti -2") refused; `2 pack, 1 dozen` units checked against the product's unit; `2x Kurti, 1 x Dupatta` made into a draft.
4. A bare amount while one new product waits for its price.
5. A scanned barcode.
6. **`CommandParser.TryParse`** → the rules (about 150 commands: orders today, mark 3 shipped, receipt 12, stock, expense, profit, insights …). No AI. If the rules find nothing:
   - **Learned phrase (exact)**: a sentence this seller taught (picked the same option twice) → run that command (never a risky one).
   - **Learned phrase (close)**: a taught phrase with ≥ 2 shared content words and half of all words shared → one small AI call "same request?" → yes: run it.
   - **Semantic (prototype, `Features:SemanticLearning`, OFF by default)**: embeddings find a taught phrase with the same MEANING (cosine ≥ 0.78) → the same AI "same request?" → yes: run it.
7. If a LOOSE rule matched (`Insight`, `FuzzyStatusUpdate`, `CustomerOrderLookup`, `ProductReport`, `CustomerSearch`, `HowTo`) and the message has **3+ words**: one small AI call "does the message really ask for this?"; `false` hands the message to the AI order reader instead of running the rule. Exact commands are never second-guessed. (Log: `RuleCheck`, `RuleConfirmed`, `RuleShort`, `NotUnderstood path=rule-overruled-…`.)
8. Otherwise → the **AI order reader** (`HandleFreeformMessageAsync`, below).

### Free-form order (AI) — `HandleFreeformMessageAsync` → `HandleAnalysisAsync`
1. Load the catalog. One OpenAI call (`gpt-4o-mini`, strict JSON schema, seller name and catalog at the END of the prompt so the long fixed part can be cached): intent + orders + products + clarification options.
2. Plain code overrides the model for delivery charge and advance written in the text.
3. By intent: `add_products` (save products, ask missing prices), `support_query`, `create_discount`, `off_topic`, `unclear` (clarification taps — logged as `NotUnderstood ai-clarify`).
4. `new_order`: unit mismatch check, "phone baad mein" detection, build drafts (`BuildPendingOrder`: match products to the catalog, price guard data).
5. Several customers: ask for each missing detail one after another (a reply with several phones fills them in order or by name); all complete → one multi-order confirmation.
6. `ContinueResolvingDraftAsync` (the draft pipeline, in order): customer name → phone (or `PhoneLater`) → "same customer?" (phone matches another name) → duplicate order check → unknown product (did-you-mean taps / add new / map to existing) → price guard (written price ≠ catalog) → loyalty/discount → **confirmation** (Save / Edit / Cancel buttons).
7. Save (`SaveOrderFromDraftAsync`): customer found or created by name, items with cost, stock changes, action log (for undo), then next-step buttons.

## 3. Voice path — `HandleAudioMessageAsync`

```
voice note
  │ transcriber configured? usage cap (voice count + USD)?            no ─► "send as text"
  │ (Features:VoiceAck) send "🎤 Aapka voice note mil gaya…"
  ▼
download audio ─► vocabulary (shop, 40 customers, 30 products, bot words) ─► speech-to-text (gpt-transcribe, ur+en)   [log: Voice note … ms]
  │ no text ─► "dobara bolein" (reported OTB-3002)
  │ "sizes/colours are in a sheet" ─► ask for the file
  │ bare phone number ─► used as is (no rewrite call)
  ▼
InterpretVoiceAsync  (one AI call: gpt-4o, or gpt-4o-mini when the transcript has ≤ 8 words; OpenAi:VoiceFastModel)
  context given to the AI: what the bot is waiting for (state, options), catalog, last 5 orders, 40 customers, last 6 chat messages
  output: typed STEPS or ONE question. Prompt rules in force:
    (1) keep every digit  (2) yes/no  (3) numbered choice  (4) exact catalog names  (5) products being added "Name - price, cost, stock"
    (6) a dictated order "Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore"
    (7) a chatty narrated order -> ONE compact order ("Hamira, 10 Lawn Suit, 2800 each"), never a copy/transliteration of the note;
        "phone not available now" -> ends with ", phone baad mein"
    (8) bot asked about an unknown product and the seller talks about it -> ONE product line "Chadar - 10000, cost 8500, stock 50"
    (9) several customers -> one line per customer, never merged
    (10) several separate wishes -> separate steps in order; self-correction ("pehla nahi, dusra", "Kurti nahi, Lawn Suit") keeps only the corrected version
         (last chat messages tell what "pehla/wahi" means); "cancel the old order and write a new one" = cancel step + new order
  ▼
guards (plain code)
  • CorrectMarginQuestion: "margin" question must not become "create discount"
  • Faithfulness gate (WhyUnfaithful): every 3+ digit run, spoken amount (also Urdu words), count, refusal ("mat", "nahi"), comparator ("se kam"),
    delivery word in the transcript must survive in the rewrite; a number in the rewrite must have been said OR be known
    (recent orders, catalog prices, and numbers from the last chat messages and the bot's own "Maine suna" echoes). Failure ⇒ nothing runs.
      risky steps (money, cancel, status)  ⇒ Reject  ("koi tabdeeli nahi")        [log: Voice rewrite refused: <reason>]
      harmless steps                       ⇒ Clarify ("rakam, quantity alag likh kar bhejein")
  ▼
risky steps and the bot idle ─► park steps, ask "Yeh karoon? YES/NO"   (step 1 of the text path handles the answer)
(an untrusted rewrite is NEVER offered for YES: a test requires that a rewrite which changed the meaning is not run and not suggested)
otherwise ─► echo "🎤 Maine suna / ➡️ Samjha" ─► each step is sent through HandleIncomingMessageAsync = THE TEXT PATH ABOVE
```

So a voice note is **speech-to-text, then an AI rewrite into typed text, then the ordinary text path**. Every limitation of the text path (states, rules) applies to voice too; the rewrite is where meaning can be lost.

## 4. The AI router — `TryRouteMidFlowReplyAsync`
Runs only when the bot is waiting for an answer, after the draft escape (step 13) and before the state handler.
- **States covered:** the draft states and the choice/confirmation states (19). Free-text modes (discount details, business info, payment method, support reply edit, order edit, receipt design) use a conservative version: only a reply of 4+ words that is not a command, and only "new order" / "cancel" leave the mode.
- **Not sent to the AI (obvious):** yes/no/ok, 1–3 digits, a plain phone, a button tap, and 1–2 plain words — except at the **phone** question, where a short non-phone reply is sent to the AI.
- **The AI** (`ClassifyMidFlowReplyAsync`, told the bot's question and the reply) returns one of:

| route | what the engine does |
|---|---|
| `answer` | the state's own handler, as before |
| `new_order` | leave the question, handle the message as a fresh one (Idle path) |
| `cancel` | drop the draft/question (any wording), tell the seller |
| `skip` (phone question only) | save the order without a phone (`PhoneLater`) |
| `other` (a side question or command) | **pause** the draft (confirm or phone/name question): answer the message through the Idle path; if that did not start something new, **restore** the draft, say "↩️ <name> ka order abhi baaki hai, wahin se jaari karte hain:" and ask its next question again |

- No AI (no key / failure) or `answer` ⇒ exactly the old behaviour. The router never runs a money-changing step: Save still needs YES.
- Deterministic helpers that run before it: "cancel"/leave phrases and real commands (`TryLeaveOrderDraftAsync`), "skip" / "phone abhi nahi hai, baad mein dunga" (`PhoneLaterWords`), a new order sent as the phone/name answer (letters + digits or commas), phones matched to customers by name.

## 4b. Learning (per seller) — `ConversationEngine.Learning.cs`
When the AI order reader asks "kya aap yeh kehna chahte hain?" the sentence is remembered (`ClarifiedText`). If the seller then **picks an option** (or types the command), `LearnPhrase(sentence -> command)` stores it for THIS seller: not if the rules already understand the sentence, never a risky command, 100 phrases max. A phrase is **used only after the same choice twice** (`LearnedVotes`), so a stray tap teaches nothing. Used in the Idle step 6 above. Details and the three levels: `docs/learning-loop.md`.

## 5. State handlers (what each waits for)
Draft: confirmation (yes/edit/cancel/delivery/advance), missing field (phone, name, product choice, new product price, price choice), grouping choice, multi-order confirmation. Choices and confirmations: clarification, cancel, bulk status, duplicate order, COD collected, filter, custom date, broadcast channel/audience, receipt order, delete customer, loyalty, support reply/pick, import. Free text modes: discount details, business info, payment method, support reply edit, order edit, receipt design. Not covered by the router: setup/guide, reset account and subscription payment. Each handler decides for itself what to do with a reply it does not recognise (some re-ask, some treat it as "no" and **swallow** the message) — the main source of "stuck" chats.

## 6. Other message kinds
- **Image** (`HandleImageMessageAsync`): logo/banner caption → receipt branding; a caption that is a **question** ("yeh color milega?", ends with ?, starts kya/kaun/kitna…) and does not say "order" → short reply, no draft, any draft in progress stays; otherwise AI vision reads an order or a payment receipt → draft or "mark paid". Only the caption is looked at (a question picture without a caption still goes to vision).
- **Document**: xlsx/csv/ods/xls import (customers, catalog), shown as a summary, saved after YES.
- **Button / list tap**: arrives as the button's label text and is mapped (step 6).

## 7. Where each kind of failure comes from
| Symptom | Layer | Evidence you can get |
|---|---|---|
| "Phone number sahi nahi lagta" loop | state handler took a non-answer | fixed by 13/14 |
| "samajh nahi aaya, rakam alag likh kar bhejein" (voice) | faithfulness gate | log `Voice rewrite refused: <reason>` |
| Paragraph repeated back (“Samjha: …”) | voice rewrite did not compress | log `NotUnderstood`, prompt rules 6–9 |
| "Aap kya karna chahte hain?" | AI order reader returned unclear | log `NotUnderstood path=ai-clarify` |
| Slow reply | speech-to-text, rewrite, AI call or translation | logs `Voice note … ms`, `OpenAI … took … ms`, `handled in … ms` |
| First message after idle slow | cold connections / JIT | `WarmupService` keeps them warm |

## 8. Logs to read
```
docker logs order-tracker-api 2>&1 | grep -E "NotUnderstood|Voice rewrite refused|Voice note:|took [0-9]+ ms|handled in"
```
`NotUnderstood` lines hold the seller's own words (`Features:TurnLog`; see `docs/not-understood-log.md`).

## 9. What changed since the first version of this file (PR #86–#92)
Latency: timing logs, prompt-cache friendly order prompt, 5 s translation budget, 30 s AI timeout, warm-up service, voice rewrite skipped for a bare phone, fast model for short transcripts, voice ack message.
Stuck chats: draft expiry (20 min), cancel / new order / "skip" at the phone question, AI router with pause-and-resume, a re-ask shows the draft again.
Orders: items-only text in code (`2x Kurti, 1 x Dupatta`), unit check (`2 pack` vs a per-piece product) typed and AI path, negative quantity refused, price guard unchanged, an unknown product given with details is added and the order carries on, order saved without a phone.
Voice: rules (6)–(10) above, numbers from recent chat count as heard, `Voice note … ms` log.
Ops: `NotUnderstood` log, CI workflow, compose variables for a second environment.

## 9b. Since PR #92
PR #93: loose-rule AI check; PR #94: `RuleConfirmed` / `RuleShort` logs; PR #95: check from 3 words, per-seller learning; PR #96: two identical choices teach a phrase, close sentences confirmed by the AI; PR #97–#98: semantic (embeddings) prototype, off by default, switched with `.env` `FEATURES_SEMANTIC_LEARNING`.

## 10. Honest limits of this design
- The brain is a state machine with AI at three doors (order reader, router, voice rewrite). It has no memory of "what the seller meant earlier" except the last 6 messages given to the voice AI and the draft in `SessionContextData`.
- Meaning is decided once per message; there is no plan or retry. "Cancel the old order and write a new one" is now asked of the rewrite as two steps (prompt rule 10), but whether it does so reliably is unmeasured.
- The pause-and-resume covers the order confirmation and the phone/name question only, not the other states.
- The faithfulness gate protects money, but it also causes the dead ends above; a refusal cannot be overridden by the seller.
- A different design (an AI agent that sees the whole chat and calls tools such as add_product / create_order / mark_status, with YES for money steps) would remove most state handlers; it is a rewrite of the engine and needs its own tests.
