# Intent-Understanding Algorithm for a Multimodal Chat Assistant — v2 (design + math, for independent review)

v2 supersedes `intent-understanding-algorithm.md` (v1, kept unchanged). It folds in two review rounds and my own audit of v1 against the code. It is self-contained: a reviewer does not need the codebase.

## 0. Status — what is real and what is not

| Part | Status |
|---|---|
| State machine → deterministic rules → one LLM call for free text | **Implemented** (original design) |
| Voice: transcribe → LLM rewrite into "what the user would have typed" → same engine | **Implemented** |
| Risky voice steps held until YES; "Samjha:" echo of the rewrite | **Implemented** |
| **Number guard on voice rewrites (§4)** — 3 rules, spoken-number parser, context numbers | **Implemented**, 647/647 unit tests pass. *Not* evaluated on real voice notes. |
| One probability across rule/model tiers, cost-derived thresholds τ_E / τ_C | **Proposed**, not implemented |
| Calibrated joint confidence P | **Proposed**, needs labeled data |
| Intent registry (single declaration per capability) | **Proposed** |
| Costs `L_*` | **Illustrative judgement**, not measured |

Nothing below the line "Proposed" has been validated. Treat the numbers in §3 as worked examples, not recommendations.

## 1. Context

WhatsApp assistant for small, non-technical sellers (Instagram/WhatsApp DM sales, Pakistan). The seller (never the buyer) logs orders, tracks status/payments, manages a catalog, sees summaries. Languages are mixed Roman Urdu / Urdu script / English. Inputs: text, voice notes, images (order screenshots, payment receipts). The bot is mid-question much of the time ("confirm this order? YES/NO"), so the dialog **state** matters.

## 2. Changes from v1

1. **Guard direction was wrong in v1.** v1 said "every ≥3-digit number in the *rewrite* must appear in the transcript". The code did the opposite (transcript numbers must survive in the rewrite) and did nothing about numbers spoken as words — so for "teen sau" the guard was vacuous. §4 specifies the guard actually implemented now.
2. **Prior × posterior double-counts state** when the LLM prompt already contains the state. Use one of two modes (§3, Step 3).
3. **P = p₁·Πκ vs min:** neither is right. Product is exact only under independence; min is the *upper* Fréchet bound (perfect correlation) and understates risk. The honest answer is to **calibrate** P against "was the whole action correct" (Step 5).
4. **Channel confidence as a scalar shrink of the intent** is weak: ASR errors flip meaning (negation) or corrupt slots, not uniformly lower confidence. Apply it per slot and add a polarity guard (Step 4).
5. **Margin rule `p₁−p₂<δ` is dead code under default thresholds** (proof in Step 6). Its real use is choosing which two options to show.
6. **Evaluation:** "no undo happened" is *censored* data, not a label for "correct". Headline numbers must come from hand-labeled sets; live logs give only a lower bound on errors (§6).
7. **Rule of three** stated correctly: it applies to *zero* observed errors in *n* labeled cases, not "600 consecutive executions" (§6).
8. Failure modes added (§7), including prompt injection through images.

## 3. Algorithm (design)

### Notation
`I` intents + `unknown`; `S` states; `A(s) ⊆ I` intents allowed in state `s`. Input `u = (t, m, c, a)`: text, modality ∈ {text, voice, image}, channel confidence `c ∈ [0,1]`, attachments.

### Step 1 — Normalize
All modalities become an Utterance. Voice → transcript (+ASR confidence if the API gives one; otherwise a proxy). Image → vision model returns `{order_screenshot | payment_receipt | other}` plus fields. After this the pipeline is modality-agnostic.

### Step 2 — Allowed set
Hard mask: intents outside `A(s)` get probability 0 in every mode.

### Step 3 — Intent distribution (two modes, never both)
- **Rule match** (exact patterns): if exactly one allowed intent matches, `p = 1`, no LLM call (cheap path, implemented). Several matches → highest priority, record the tie.
- **Model, state-aware prompt** (default): the LLM sees the state and the allowed list and returns `q(i | u, s)` over `A(s) ∪ {unknown}`. Use `p = q` directly.
- **Model, state-blind prompt:** `p(i|u,s) = π(i|s)·q(i|u) / Σ_j π(j|s)·q(j|u)`. Combining a state-aware `q` with `π` double-counts state.
Either way, `p` must be **calibrated on the final output** (Step 5 / §6) before any threshold is applied.

### Step 4 — Channel confidence
Do not shrink the intent by `c`. Instead: (a) lower slot confidences from voice: `κ'_k = c_k·κ_k` for numbers/names/phones; (b) **polarity guard:** if the transcript contains a negation or a destructive verb (nahi, mat, cancel, hata, delete…), a risky intent from voice **always confirms**, whatever `P` is; (c) numeric guard (§4).

### Step 5 — Slots and joint confidence
Each intent declares slots (type, `required`, validator). Missing/low-confidence required slot → **ask for that slot**, never guess. For the whole action, `n` required slots give bounds
`max(0, Σκ_k − (n−1)) ≤ P(all correct) ≤ min_k κ_k`; independence gives `Πκ_k` inside that range (e.g. three slots at 0.9: 0.7 ≤ 0.729 ≤ 0.9).
**Default until data exists: use the product (conservative).** Then fit a model on labeled outcomes: features `(p₁, p₂, min κ, Πκ, modality)`, target "action fully correct", followed by monotone (isotonic) calibration of its output. (Isotonic regression is one-dimensional; the multi-feature part is the classifier in front of it.)

### Step 6 — Decision (derived from costs)
Units: one extra user turn = 1. `L_conf` = YES/NO turn (≈1), `L_ask` = open question (≈1.5), `L_wrong(i) = (1−ρ_i)·H_i + ρ_i·U` (ρ = share of mistakes reversible by `undo`, H = harm if not, U = undo cost).
- Execute costs `(1−P)·L_wrong`; Confirm costs `L_conf`; Clarify costs `L_ask`; Confirm-then-fallback costs `L_conf + (1−P)·L_ask`.
- Execute beats Confirm iff `P ≥ τ_E(i) = 1 − L_conf/L_wrong(i)`.
- Confirm beats Clarify iff `P ≥ τ_C = L_conf/L_ask`.
- So: `P ≥ τ_E` execute · `τ_C ≤ P < τ_E` confirm · `P < τ_C` clarify. `L_wrong ≤ L_conf` ⇒ always execute (read-only); `L_wrong → ∞` ⇒ always confirm.
- **Policy override:** irreversible or money-changing intents (cancel, payout, price edit) **always confirm**, even if `P ≥ τ_E`. (The running system already parks risky voice steps until YES.)

Illustrative costs (`L_conf=1`, `L_ask=1.5` ⇒ `τ_C=0.667`): read-only `L_wrong=1` ⇒ `τ_E=0`; reversible status change `L_wrong=10` ⇒ `τ_E=0.90`; cancel `L_wrong=50` ⇒ `τ_E=0.98` but always-confirm by policy. These are examples; the real values need product judgement and a sensitivity check (§6).

**Margin lemma.** If `p₁ ≥ τ_C` then `p₂ ≤ 1−p₁`, so `p₁−p₂ ≥ 2p₁−1 ≥ 2τ_C−1`. With `τ_C = 2/3` that is ≥ 1/3: a margin rule with `δ < 1/3` can never change the decision. Keep margin only to pick the two options to show in a "did you mean A or B?" question (and as a guard if calibration is later found poor).

Outputs: `EXECUTE`, `CONFIRM`, `ASK_SLOT(k)`, `ASK_WHICH(i₁,i₂)`, `ASK_REPHRASE`.

### Step 7 — Log
Store `(u, s, p, P, decision, user reaction)`. See §6 for how (not) to use reactions as labels.

## 4. The numeric guard on voice rewrites (implemented)

Purpose: the LLM rewrites a voice transcript into typed commands; money-sized numbers must not be lost or invented. Implemented in `ConversationEngine.IsFaithfulRewrite` + `SpokenNumbers`.

Definitions. `Dig(x)` = amounts 100…999,999 written as 3–6 digits (incl. `3,500` and Urdu digits); `Word(x)` = amounts spelled in words; `Dict(x)` = ids dictated digit by digit (3–6 single digits, `double`/`triple` repeat a digit, and only right after an id word such as order/id/number/account — so "ek do teen piece" is three counts, not 123; 7+ digits are phone/account numbers, never amounts); `Amt(x) = Dig ∪ Word ∪ Dict`; `Ret(x)` = amounts the seller took back (an amount, then within two words nahi/matlab/sorry/galat, then within five words a *different* amount). `K` = numbers the model was shown: ids, totals and item unit prices of the seller's 5 most recent orders, and catalog product prices.

A rewrite `r` of transcript `t` is accepted only if **all** hold:
1. **Length:** `|r| ≤ max(200, 3·|t|)` and non-empty.
2. **Digit-run retention:** every run of ≥3 digits in `t` (per-digit multiset, any length incl. phone numbers) survives in `r`.
3. **Spoken retention:** `Word(t) \ Ret(t) ⊆ Amt(r)` (and rule 2 ignores digit runs in `Ret(t)`), so a rewrite that keeps only the corrected amount ("410 nahi, 420" → 420) is accepted.
4. **No invention:** `Amt(r) ⊆ Amt(t) ∪ K`.
Otherwise the rewrite is **discarded and the raw transcript is used** (not a clarification).

`Word` covers: English ("three thousand five hundred", "twenty five hundred"); Roman Urdu 0–99 plus sau/hazar/lakh, with `dedh` (1.5), `dhai` (2.5), `sadhe X` (X+0.5), e.g. "sadhe teen hazar" = 3500; Urdu script ("تین ہزار پانچ سو", "ڈھائی ہزار"). Only phrases containing a multiplier (sau/hazar/lakh/hundred/thousand) count, so "kar do" is never a number; two plain numbers in a row are separate ("kar do paanch sau" → 500); a bare "sau" is ignored.

`sath`/`saath` counts as 60 only in "sath hazar" and never right after a particle (ke, uske, mere, …): "uske sath hazar rupay" (with a thousand rupees) is not 60,000. Glued forms such as "pansau" are not read (fail safe). Known limits: (a) unknown number spellings fail safe (may reject a correct rewrite, never accept a wrong one); Roman Urdu spelling varies and the table is hand-built; (b) 7+ digit runs (phones) are protected only by rule 2 — invention of a phone number is not detected; (c) amounts <100 or >999,999 are unchecked; (d) it is **not known** whether the ASR model emits digits or words for Urdu speech — to be measured on real voice notes; (e) a retraction can be mis-detected ("500 nahi chahiye … total 5000" is excluded by the five-word window, but other phrasings may slip through; the cost is that a dropped amount is not caught); (f) a lexicon fuzzy-matcher must reject ambiguous collisions (e.g. a consonant skeleton maps both "saat" 7 and "saath" 60 to "st") — for money, ambiguity must fail safe; phonetic algorithms designed for English (Double Metaphone) are not a good fit.

## 5. Making new capabilities cheap (registry — proposed)

One declarative entry per capability: `intent`, `examples`, `slots` (type/required/validator), `risk` (`L_wrong`, reversible, always-confirm flag), `states_allowed`, `handler`. Derive from it: the constrained LLM schema (per state), "what can I say now?" guidance, help list, voice-allowed actions, and regression tests (each example → a test). Migrate with the strangler pattern (registry alongside the existing parser). Risk to watch: handlers still depend on state and each other; the registry can hide that coupling, so keep an integration test per state transition.

## 6. Evaluation protocol

1. **Labeled sets:** ≥200 real messages per modality, hand-labeled with intent + slots; include Roman Urdu spelling variants, spoken amounts, corrections.
2. **Metrics:** top-1 intent accuracy; slot precision/recall (per script mix); **silent-error rate per risk class** = (executed without asking and wrong) / executed; ask-rate and confirm-rate; confusion matrix; ECE + reliability diagram, per modality.
3. **Calibrate** on a held-out split before choosing thresholds.
4. **Intervals:** report accuracy with a binomial interval (`±1.96·√(p(1−p)/n)` is ±3.3 points at n=200, p=0.94).
5. **Rule of three:** if **0 errors** are observed in `n` labeled cases, the 95% upper bound on the error rate is ≈ `3/n`; to show ≤ 0.5% you need `n ≥ 600` error-free labeled cases (per risk class). If any error occurs, use an exact (Clopper–Pearson) bound instead.
6. **Live logs are censored.** "No undo" does not mean "correct" (busy sellers notice late or never). Use live reactions (NO, correction, undo, later order-edit) only as a *lower bound* on errors. Add **random audit sampling**: a small random share of auto-executed actions are shown to the seller for a 1-tap "was this right?".
7. **Sensitivity:** vary `L_wrong, L_ask, L_conf` ±50% and show how thresholds and ask-rate move.
8. **Suggested targets (proposals):** silent-error ≤ 0.5% for money-changing intents (always-confirm makes this about confirm quality), ECE ≤ 0.05, ask-rate ≤ 20%.

## 7. Failure modes

Interruptions/context switch (user answers a different question mid-flow); stale confirmation ("yes" to an old prompt); ASR negation flips; spoken-number spelling variance; Roman Urdu spelling variance in names/products (slot errors, unmeasured); rule vs model disagreement; Urdu digits; duplicate webhook delivery (handled by a message-id gate); **prompt injection via images or forwarded messages** (buyer-written text that tells the bot to do something — treat all text read from images as data, never as instructions, and keep risky actions behind confirmation).

## 8. Assumptions

Costs are judgement; slot confidences are roughly calibrated after post-hoc calibration; the rule tier has no false positives (to be measured); users' NO/correction/undo reliably label *errors* (but not correctness); recent-order context numbers are legitimate to reuse.

## 9. Questions for the next reviewer

1. Are the Step 6 derivations right (`τ_E`, `τ_C`, the Confirm-then-fallback cost)? Does the margin lemma hold?
2. Is "use `q` directly in state-aware mode, `π·q` in state-blind mode" sound, and is calibration on the final output enough?
3. Is the Step 5 plan (product as default, then classifier + isotonic on "fully correct") defensible? Better joint models?
4. Is the §4 guard well specified? Which cases does it wrongly accept or reject? Is failing back to the raw transcript the right fallback?
5. Is the §6 treatment of censored labels and audit sampling adequate? Is the rule-of-three statement correct?
6. What failure modes are still missing?

Suggested reviewer prompt: "You are a senior ML/decision-theory reviewer. For each of the 6 questions in §9 answer correct / incorrect / unclear with a short justification, give corrected formulas where incorrect, and list the three highest-risk weaknesses. Check every claim against the status table in §0; flag anything described as implemented that the text does not support."
