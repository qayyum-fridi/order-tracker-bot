# Intent-Understanding Algorithm for a Multimodal Chat Assistant (design + math, for independent review)

Status: **design proposal, not implemented as one unit.** Written to be self-contained so it can be handed to a reviewer (person or AI) who has not seen the codebase. Section 10 lists exactly what I want checked.

---

## 1. Context

- Product: a WhatsApp assistant for small, non-technical sellers who sell through Instagram/WhatsApp DMs. The *seller* (never the buyer) chats with the bot to log orders, track status/payments, manage a catalog, and see sales summaries. Languages are mixed: Roman Urdu, Urdu script, English.
- Inputs arrive in three modalities: **text**, **voice notes**, **images** (order screenshots, payment receipts).
- The bot is a state machine (it is often mid-question: "confirm this order? YES/NO") plus a set of capabilities (log order, mark shipped, cancel, edit order, export, import, ...).

## 2. Problem

1. For every input, decide what the user wants (**intent**), extract its parameters (**slots**), and decide whether to **act**, **confirm**, or **ask** — without being wrong silently on actions that cost money or are hard to undo.
2. Adding a new capability must be cheap: it must not require editing the understanding logic in many places.
3. The system's confidence must mean something (calibrated), so thresholds are set from data, not guesses.

Current implementation (summarised, for contrast): priority order is (1) mid-flow state handler, (2) deterministic regex parser for fixed command syntax, (3) one LLM call for free text that classifies intent and extracts order fields. Voice is transcribed, then an LLM rewrites the transcript into what the user would have *typed* for the bot's current question, then it goes through the same engine. Risky voice steps are held until YES. What is **missing**: one probability model across the three tiers, explicit action thresholds, a single registry of capabilities, and calibration measurement. Adding one capability recently touched ~15 files.

## 3. Goals and non-goals

Goals: one pipeline for all modalities; decision rule derived from costs; capabilities declared in one place; measurable correctness.
Non-goals: formal proof of the language model (it is probabilistic; we measure it); replacing the deterministic rule tier (it is cheap and exact — keep it).

## 4. Notation

- `I` = set of intents, plus a special `unknown`. `S` = dialog states.
- Input `u = (t, m, c, a)`: text `t` (after transcription/OCR if needed), modality `m ∈ {text, voice, image}`, channel confidence `c ∈ [0,1]` (1 for typed text; ASR confidence or a proxy for voice; vision-extraction confidence for images), attachments `a`.
- `state s ∈ S` is the current dialog state.
- `A(s) ⊆ I` = intents allowed in state `s` (declared per intent in the registry).

## 5. Algorithm

### Step 1 — Normalize
Every modality becomes an `Utterance` `(t, m, c, a)`. Voice: transcribe → `t`, `c` from ASR. Image: a vision model returns a structured guess (`order_screenshot | payment_receipt | other`) plus fields; treat the guess as candidate intent + slots with per-field confidences. After this step the pipeline is modality-agnostic.

### Step 2 — State prior
```
π(i | s) = 0                       if i ∉ A(s)
π(i | s) ∝ w(i, s) > 0             otherwise (default: uniform over A(s); in a question state the "answer to the question" intent gets the largest weight)
```
Normalize so `Σ_i π(i|s) = 1`.

### Step 3 — Likelihood from two sources
- **Rules.** `r(i,u) ∈ {0,1}` from deterministic patterns. If exactly one allowed intent matches, set `p(i*) = 1` and skip the model (cheap path, no LLM call). If several match, take the highest-priority rule; record the tie.
- **Model.** Otherwise a language model is asked to choose **only among intents in `A(s)`** (constrained output: intent id + slot values), returning a distribution `q(i | u)` over `A(s) ∪ {unknown}`.

Combine with the prior:
```
p(i | u, s) = π(i | s) · q(i | u) / Σ_j π(j | s) · q(j | u)
```
(Caveat in §10: if the prompt already shows the state to the model, the prior is partly double-counted.)

### Step 4 — Channel confidence
For non-text input, shrink toward `unknown`:
```
p̃(i) = c · p(i)                      for i ≠ unknown
p̃(unknown) = 1 − Σ_{i≠unknown} p̃(i)
```
Let `i* = argmax p̃`, `p₁ = p̃(i*)`, `p₂` = second-largest.

### Step 5 — Slots
Each intent declares slots `k` with type, `required` flag and a validator. Extraction returns value `v_k` and confidence `κ_k`. Hard guards (not probabilistic): any number of ≥3 digits in the rewritten text must appear in the original transcript (else reject the rewrite); phone/amount must pass validators.
- If a **required slot is missing or `κ_k < κ_min`** → **ask for that slot** (never guess), regardless of `p₁`.
- Overall confidence of the proposed action (independence assumed — see §10):
```
P = p₁ · Π_{k required} κ_k
```

### Step 6 — Decision (derived, not hand-tuned)
Costs per intent `i`, in a common unit "one extra user turn = 1":
- `L_conf` = cost of a YES/NO confirmation turn (≈ 1).
- `L_ask` = cost of an open clarification question (≈ 1.5, it takes typing).
- `L_wrong(i)` = expected cost of executing the wrong action = `(1 − ρ_i)·H_i + ρ_i·U`, where `ρ_i` = fraction of mistakes that are reversible by `undo`, `H_i` = harm of an irreversible mistake, `U` = cost of undoing.

Expected cost of each option:
```
Execute  :  (1 − P) · L_wrong(i)
Confirm  :  L_conf                      (the user answers YES/NO either way)
Clarify  :  L_ask
Confirm-then-fallback when wrong: L_conf + (1 − P) · L_ask
```
Execute beats Confirm when `(1−P)·L_wrong < L_conf`, i.e.
```
τ_E(i) = 1 − L_conf / L_wrong(i)         → execute silently iff P ≥ τ_E(i)
```
Confirm beats Clarify when `L_conf + (1−P)·L_ask < L_ask`, i.e.
```
τ_C = L_conf / L_ask                     → confirm iff τ_C ≤ P < τ_E(i); otherwise clarify
```
Limits behave sensibly: `L_wrong ≤ L_conf` (read-only commands) → `τ_E ≤ 0` → always execute; `L_wrong → ∞` → `τ_E → 1` → always confirm. A policy override can force "always confirm" for irreversible money actions.

**Ambiguity guard:** if `p₁ − p₂ < δ`, do not execute; ask "did you mean A or B?" with the two top intents as buttons. (Redundant if `p` were perfectly calibrated; kept as protection against miscalibration.)

Output of the decision: one of `EXECUTE(i, slots)`, `CONFIRM(i, slots)`, `ASK_SLOT(k)`, `ASK_WHICH(i₁, i₂)`, `ASK_REPHRASE` (when `unknown` wins).

### Step 7 — Log
Store `(u, s, p̃, P, decision, user reaction)`. Reaction labels the outcome: NO, a correction, or `undo` within N minutes ⇒ the decision was wrong. This is the data for §8.

## 6. Worked numeric example

Take `L_conf = 1`, `L_ask = 1.5` ⇒ `τ_C = 0.667`.

| Intent | `L_wrong` | `τ_E = 1 − 1/L_wrong` | Behaviour |
|---|---|---|---|
| "orders today" (read-only) | 1 | 0 | always execute |
| "mark 3 shipped" (reversible) | 10 | 0.90 | `P≥0.90` execute; `0.667≤P<0.90` confirm; else clarify |
| "cancel order 12" | 50 | 0.98 | almost always confirm (policy: always) |

Voice "mark 3 shipped": rules miss, model gives `q = 0.97`, state prior neutral ⇒ `p = 0.97`; ASR `c = 0.90` ⇒ `p̃ = 0.873`; slot "order 3" `κ = 1` ⇒ `P = 0.873` ⇒ in [0.667, 0.90) ⇒ **CONFIRM** "Mark order #3 shipped? YES/NO". The same sentence typed (`c = 1`, rule matches, `P = 1`) ⇒ **EXECUTE**.

## 7. Making new capabilities cheap (the registry)

Every capability is one declarative entry; everything else is derived:
```
intent:        import_data
examples:      ["import", "purana data", "old system se data aana hai"]
slots:         [ { name: file, type: document, required: false } ]
risk:          { L_wrong: 3, reversible: true }       # → τ_E
states_allowed:[Idle, OnboardingStart, OnboardingCatalog]
handler:       HandleImport
```
Derived automatically from the registry: the constrained schema given to the LLM (and its allowed-intent list per state), the "what can I say now?" guidance text, the help list, the voice-note allowed actions, the regression test cases (each `examples` line becomes a test). Migration strategy: run the registry **alongside** the existing parser (strangler pattern); new capabilities go in the registry first; old ones move over gradually.

## 8. Calibration and evaluation

1. **Dataset:** ≥200 real messages per modality (text, voice transcripts, image-derived), labeled by hand with intent + slots. Include the hard cases (Roman Urdu spellings, numbers, "pehla wala", corrections).
2. **Metrics:** top-1 intent accuracy; slot-level precision/recall; **silent-error rate** = (executed-without-asking and wrong) / executed — the number that matters most; ask-rate and confirm-rate per intent; confusion matrix; **ECE** (expected calibration error) and a reliability diagram.
3. **Calibrate** `p̃` (temperature scaling or isotonic regression on a held-out split) until the reliability curve is near the diagonal; only then are `τ_E, τ_C` meaningful.
4. **Uncertainty:** report accuracy with a confidence interval, e.g. normal approximation `±1.96·√(p(1−p)/n)`; at `n=200`, `p=0.94` this is ±3.3 points — so claim "about 94% ± 3%", not "94%".
5. **Sensitivity:** vary `L_wrong, L_ask, L_conf` ±50% and show how thresholds and ask-rate move; costs are judgement, so show the decision is stable.
6. **Suggested acceptance targets (proposals, not measured):** silent-error rate ≤ 0.5% for money-changing intents; ECE ≤ 0.05; ask-rate ≤ 20% overall.

## 9. Assumptions (explicit)

- Costs `L_*` are elicited from product judgement, not measured.
- Slot confidences are treated as independent.
- The LLM's `q(i|u)` can be made roughly calibrated by post-hoc calibration; raw LLM "confidence" is not trusted.
- The user's reaction (NO/correction/undo) is a reliable label for "wrong".
- The rule tier is exact when it fires (no false positives); this must also be measured.

## 10. What I want a reviewer to verify

1. Is the derivation in Step 6 correct? (`τ_E = 1 − L_conf/L_wrong`, `τ_C = L_conf/L_ask`; cost model of Confirm vs Clarify.)
2. Is `p ∝ π·q` valid, or does it double-count state when the LLM prompt already includes the state?
3. Is scaling by channel confidence `c` (Step 4) principled, or should ASR error be modelled differently (e.g. a noisy-channel model over transcripts)?
4. Is `P = p₁ · Π κ_k` acceptable given dependence between slots? What would be better (joint model, min, calibrated product)?
5. Is the margin rule `p₁ − p₂ < δ` justified given calibration, or redundant?
6. Are the evaluation metrics sufficient to show the system is safe (esp. silent-error rate per risk class)? What is missing?
7. Failure modes I may have missed (adversarial input, state confusion after interruptions, rule/model disagreement).
8. Is the registry approach sound for keeping new-capability cost low, or does it hide coupling?

Suggested prompt for a reviewing AI: "You are a senior ML/decision-theory reviewer. Read the document. For each of the 8 questions in §10, answer *correct / incorrect / unclear* with a one-paragraph justification and, where incorrect, give the corrected formula. Then list the three highest-risk weaknesses."
