# Design: learned utterances (self-improving routing)

Status: draft. Nothing here is built yet except the core-intent resolver draft (`Intents/CoreIntentResolver.cs`).

## Problem

The bot fails on everyday wording that is not in its catalog or parser (for example `menu dikhao`, `stock dikhao`,
`munafa kitna hua`, `kya karun samajh nahi aa raha`). Each new phrasing is fixed by hand today, and the fix helps only the
next person who sends that exact text. We want a fix made once from real use, that then helps every seller.

## Principles

1. The learned data is a lookup of a phrase to a typed command. It is never executed as a free-form instruction.
2. A learned mapping must parse, with `CommandParser`, to a command kind the catalog allows.
3. Risky commands (`ActionRisk.RiskyCommands`) are never learned globally, and a seller's own learned risky mapping still asks for YES.
4. Learning from a seller's own actions stays on that seller first. Global use needs more evidence and a review.
5. Personal data is removed before anything is stored. Raw messages are kept for a short time only.

## Data

Table `UnplacedUtterances` (capture, short retention, 30 days):

| Field | Notes |
|---|---|
| Id, CreatedAt | |
| SellerHash | salted hash of the seller id, not the phone number |
| MaskedText | normalized text with names, phones and amounts replaced by `{customer}`, `{phone}`, `{n}` |
| Stage | `deterministic_miss`, `router_gap`, `router_low_confidence`, `router_no_route` |
| RouterIntent, RouterConfidence | nullable, set when the router ran |
| NextCommand | the typed command the seller sent within 5 minutes after the miss, nullable |
| NextCommandKind | parsed kind of NextCommand, nullable |

Table `LearnedUtterances` (the approved and candidate mappings):

| Field | Notes |
|---|---|
| Id | |
| SellerId | null for global rows |
| NormalizedText | masked, lower-cased, punctuation removed, spaces collapsed |
| Command | typed command, validated with `CommandParser` before storing |
| CommandKind | parsed kind |
| Scope | `seller` or `global` |
| Status | `candidate`, `approved`, `rejected`, `conflict` |
| SupportCount | distinct sellers (global) or confirmations (seller) |
| ApprovedBy, ApprovedAt | for global rows |
| LastUsedAt | to retire rows that nobody uses |

Unique key: (`NormalizedText`, `Scope`, `SellerId`).

## Normalization

- Lower-case, remove punctuation and emoji, collapse spaces, drop invisible characters (`TextHygiene.StripInvisible` already exists).
- Replace numbers with `{n}` (keep words such as `ek` as they are).
- Replace known customer, product and supplier names of the seller with `{customer}`, `{product}`, `{supplier}`. Replace phone numbers with `{phone}`.
- Only the masked form is stored. The same normalization is applied to incoming text before lookup.

## Learning signals

| Signal | Scope | Rule |
|---|---|---|
| Miss, then the seller types a known command within 5 minutes | seller | candidate for that seller |
| Miss, then the seller taps a clarification option that runs a command | seller | candidate for that seller |
| Same candidate confirmed twice by that seller | seller | status `approved` for that seller |
| Same normalized text mapped to the same command by 3 distinct sellers, no other mapping | global | `candidate` for review, then `approved` by an admin |
| Same normalized text mapped to two different commands | any | `conflict`. Neither is used until an admin decides |

Corrections matter: if an approved mapping is undone (`undo`) or the seller then types a different command, the support count drops. Two drops set the row to `candidate` again.

## Runtime order

1. Deterministic parser (unchanged, first).
2. `LearnedUtterances` lookup by normalized text: seller scope first, then global `approved`.
3. Router (`RouteIntentAsync`), with its existing confidence threshold.
4. Gap or fallback message, from templates.

A learned mapping that is risky asks for YES as usual. A learned mapping never skips the risky check.

## Review and admin

- Admin reports (same admin key as `PilotReport`): candidates by support count, conflicts, and the top unplaced stages.
- Admin can approve, reject, or edit a candidate's command. The command is re-validated before it is saved.
- No raw customer text appears in any admin view. Only masked text.

## Privacy

- Seller privacy notice: the bot may store masked versions of messages it could not understand, for 30 days, to improve itself.
- `reset account` deletes that seller's rows in `UnplacedUtterances` and `LearnedUtterances` (scope `seller`).
- Global rows contain masked text only.

## Evaluation

- The approved set becomes a regression test. Every approved row must still parse to its kind, and the deterministic parser must not change behaviour for them.
- Metrics per release: coverage (share of messages that were placed), precision on the approved set, and the count of `router_gap` misses per 100 messages.

## Rollout

1. **P1 capture and report:** `UnplacedUtterances` and the admin report. No behaviour change.
2. **P2 seller learning:** seller-scope candidates and approvals. No global rows.
3. **P3 global learning:** global candidates with admin approval.
4. **P4 fuzzy match:** embedding similarity against approved rows, with a threshold calibrated on the regression set. Only after P3 has data.

## Storage and migrations

- Sqlite: tables created by the existing schema patcher.
- SQL Server: a new EF migration, generated with `Database__Provider=SqlServer`, as CLAUDE.md describes. Never applied to a live database without review.

## Open questions

1. Retention: 30 days for masked captures is a proposal. Confirm.
2. Who approves global rows: the product owner, or a second reviewer?
3. Should a seller be able to see and delete what the bot learned from them?
4. Do we need a minimum number of distinct sellers (3 in this draft) before any global learning, given a small pilot?

## Success measures

- Share of messages placed (deterministic + learned + router) goes up release over release.
- `router_gap` misses per 100 messages go down for phrasings that were approved.
- Zero approved risky commands learned globally.
