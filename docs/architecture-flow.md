# Message routing: architecture flow

How an inbound WhatsApp message becomes an action. Describes the code on `claude/sweet-hypatia-1xvity` (PR #39).
Line numbers refer to `src/OrderTrackerBot.Application/Conversation/ConversationEngine.cs` at that commit and will drift.

## 1. Text message

```
WhatsApp webhook
  └─ WebhookInbox → WebhookWorkQueue (per-sender lane) → WebhookMessageGate
       └─ HandleIncomingMessageAsync                          (ConversationEngine.cs:71)
            ├─ TryHandleVoiceConfirmationAsync                 parked voice steps?  YES → run / NO → cancel
            │     (only in AwaitingVoiceConfirmation; expires after 10 min; log-id check)
            └─ HandleIncomingCoreAsync                         (:83)
                 ├─ empty / seller inactive                    → reply, stop
                 ├─ guidance request ("kya karun", "?")        → contextual tip, state unchanged
                 ├─ "reset account" (not already pending)      → StartResetAsync (prompt, ResetAskedAt, ResetGenerationId)
                 ├─ onboarding states / incomplete seller      → HandleOnboardingAsync
                 ├─ billing (not for reset YES/NO)             → paywall / subscribe
                 ├─ mid-flow state switch (~:235)              → one handler per ConversationState
                 │     order draft states                       → TryLeaveOrderDraftAsync first
                 │     AwaitingOrderEdit                        → HandleOrderEditAsync (ack / nahi / cancel / corrections)
                 │     AwaitingResetConfirmation (YES)          → HandleResetConfirmationAsync
                 │         IsAffirmative?                         no  → cancel, nothing changes
                 │         ResetConfirmation.IsValid             age in [0, 10 min), generation present
                 │         ResetConfirmation.TryClaimAsync       conditional update, state → Idle
                 │         SellerAccountReset.ResetAsync         one SaveChanges
                 │     other confirmation states                 → act only on the stored id
                 └─ Idle → HandleIdleAsync (~:285)
                       ├─ CommandParser.TryParse               deterministic command, answered from the DB, no AI
                       └─ IAiOrderAssistant                    one AI call: intent + order fields (fallback: clarification)
  then: FlushStockWarningsAsync → TrySendShortcutBarAsync
```

## 2. Voice note

```
HandleAudioMessageAsync
  ├─ rate limit (MediaRateLimiter), download, transcribe (OpenAiAudioTranscriber)
  └─ InterpretVoiceAsync → TurnDecision
       ├─ AI returns a question only              → Clarify(question, options)
       ├─ AI returns steps
       │     └─ DecideRewrite (WhyUnfaithful)
       │           faithful                         → Execute(steps)
       │           unfaithful, touches money/state  → Reject(reply, no tap options)
       │           unfaithful, harmless             → Clarify(UnfaithfulQuestion)
       ├─ AI unavailable                           → Execute([transcript])
       └─ AI returned no usable step
             plain command / yes / no              → Execute([transcript])
             otherwise                             → Clarify(ParserFailureQuestion)

  Clarify → SendChoices (tap buttons when the options are short)
  Reject  → text reply, nothing runs
  Execute → ShouldConfirmVoiceSteps?
              risky step, idle seller  → park steps (+ time, + log id) → wait for YES (section 1)
              otherwise                → RunVoiceStepsAsync → HandleIncomingMessageAsync per step (text path)
```

## 3. What a YES can act on

| Stored in session context | A YES acts on |
|---|---|
| `PendingOrder`, `MultiOrders` | the draft that was shown |
| `CancelOrderId`, `CodCollectedOrderId`, `LoyaltyOrderId`, `DeleteCustomerId`, `BulkStatusOrderIds`, `PendingImportMediaId` | only the stored id |
| `PendingVoiceSteps`, `PendingVoiceParkedAt`, `PendingVoiceLogId` | parked steps, if under 10 min old and no action logged since |
| `ResetAskedAt`, `ResetGenerationId` | the reset, if under 10 min old, generation present, and the claim matches |

## 4. Known gaps

- **Reset claim across instances:** the claim is a conditional update, but it is not verified with two app instances. The per-sender lock is in-process only. Tracked in issue #40.
- **Order confirmation:** the draft has no expiry, so a later "ok" can still save it.
- **Text commands:** the safety gate (`TurnDecision`) applies to voice only. Text goes straight to the parser or the AI.
