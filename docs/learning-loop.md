# How the bot learns from its users

Goal: when a user shows the bot what a sentence meant, the bot should understand the same sentence next time, without anyone having to list words. Three levels, from safest to most powerful. Only level 1 is automatic and built; level 2 is a human-in-the-loop process the logs already support; level 3 is a design, not built.

## Level 1 — per seller, automatic (built, `ConversationEngine.Learning.cs`)

```
seller says S ──► rules do not know S ──► AI order reader unsure ──► bot asks "Kya aap yeh kehna chahte hain?" with options
                                                       │  (ClarifiedText = S)
seller TAPS option O (or types the command C)  ◄───────┘
        │
        ▼
LearnPhrase(S -> command(O or C)):
   • S is normalised (lower case, letters/digits, single spaces)
   • stored only if  S is not already a command the rules know,
                     the command is NOT risky (no money, cancel, status, payout, drawer …),
                     S has 3..120 characters
   • at most 100 phrases per seller, oldest forgotten first
   • kept in the seller's own session data (no schema change, no migration)

next time the seller says S (exactly, after normalising):
   HandleIdleCoreAsync: rules say "not a command"  ──► LearnedCommandFor(S) found ──► run it, no question
```
Properties:
- **Safe:** it only repeats a choice this very seller made; a risky command is never learned; exact commands are never overridden (the rules run first).
- **Private:** one seller's phrases are never used for another seller.
- **Visible:** `Learned phrase: <Kind> (seller-confirmed)` and `Learned phrase used: <Kind>` in the server log (no text).
- **Limit:** exact sentence only (after normalising). "customer status bhejo" is learned; "customer ka status bhejo do" is a different sentence and is asked again.

## Level 2 — across sellers, human in the loop (process, uses the logs that exist)

```
every day:   NotUnderstood / RuleConfirmed / RuleShort / "Voice rewrite refused" lines  (Features:TurnLog)
review:      the maintainer reads the lines and groups them by meaning
change:      (a) add a typed phrase to the AI prompts (order reader, router, voice rewrite)
             (b) add a test case so it cannot regress
             (c) only if it is a fixed command phrase, add it to CommandParser + docs/command-dictionary.md
release:     CI (build + tests) -> deploy
```
Why not automatic across sellers: one seller's wrong choice would teach everyone, a seller's private text would leak into other sellers' prompts, and an attacker could poison the bot. A human reads, generalises and tests first.

## Level 3 — designed, NOT built

1. **Alias memory:** when the seller answers "yeh kisi existing product ka doosra naam hai" for an unknown product, remember `chadar -> Dupatta` for that seller and tell the order reader (`Dupatta [also called: chadar]`) so it matches by itself next time. Needs the order prompt to return the main name only.
2. **Few-shot memory:** keep the last N seller-confirmed pairs (sentence -> what it meant) and put the 5 most similar into the AI prompt for that seller. Needs an embedding or keyword similarity step and a per-seller store (new table, SQL Server migration).
3. **Correction memory:** when the seller says "nahi, yeh nahi, woh" right after a bot action, store (the sentence, the wrong action, the right one) as a negative example.
4. **Metrics:** share of messages that needed a clarification, per day; it should fall as levels 1-3 learn. Counted from the logs, no extra code.

## What is deliberately NOT learned
Any command that changes money or is hard to undo (save order, cancel, mark status, payments, payouts, drawer, discounts). Those always keep their exact rules and a YES.
