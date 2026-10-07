# Real-world test scenario — "Ayesha Collections"

A mid-size Lahore shop that sells **clothes** (lawn suits, kurtis, dupattas) and **dry fruit / grocery by weight** (kaju, sugar), takes orders from Instagram DMs and WhatsApp, ships by courier and COD, runs discounts and a loyalty rule, and has repeat customers. One run touches every part of the bot.

Use a **fresh WhatsApp number** (or `reset account` first). Send each message exactly as written (copy/paste). "Expect" is what should come back; if something differs, note the phase + step number and report it.
Phones used: Ayesha `03001234567`, Bilal `03211234567`, Hassan `03331234567`, Sara `03451234567`, Zainab `03009998888`.

---

## Phase 1 — Onboarding (first-time seller)

| # | Send | Expect |
|---|------|--------|
| 1 | `start` | Welcome + language buttons |
| 2 | `Roman Urdu` | Language saved, "Setup shuru karein" choices |
| 3 | `Setup shuru karein` | Asks business name |
| 4 | `Ayesha Collections` | Asks city / type / Instagram |
| 5 | `Lahore, Clothing, @ayesha.collections` | Saved, asks how many products |
| 6 | `10 ke qareeb` | Moves to adding products |
| 7 | `Lawn Suit - 3500` | Added |
| 8 | `Kurti - 1800` + new line `Dupatta - 800` + new line `Shalwar - 900` (one message, 3 lines) | "3 products add ho gaye" |
| 9 | `Sugar 5 kg - 500` | Added as a 5kg pack |
| 10 | `mere paas 4 khaddar chadar aur 2 wool shawl hain` (no prices, no phone) | **NOT an order.** "yeh naye products hain, order nahi" + asks for prices |
| 11 | `Khaddar Chadar - 2200` | Added |
| 12 | `done` | Setup complete, **14-day trial** starts, quick buttons appear |

**Tests:** onboarding state machine, multi-line add, weight packs, the "product ≠ order" intent, trial start.

## Phase 2 — Catalog, stock, wholesale, setup

| # | Send | Expect |
|---|------|--------|
| 13 | `catalog` | Numbered list of all products |
| 14 | `Kaju - 320` | Added |
| 15 | `Kaju - price tiers: 1kg=320, 10kg=300` | Wholesale tiers saved |
| 16 | `stock Kurti 20` | Stock tracked: 20 |
| 17 | `stock Lawn Suit 3` | Tracked: 3 (will trigger low-stock warnings) |
| 18 | `stock` | Lists tracked stock |
| 19 | `stock Kurti +10` | 30 |
| 20 | `Kurti - 1900` | "price updated" (edit product) |
| 21 | `add payment: jazzcash, 0300-1234567` | Payment method saved |
| 22 | `delivery 200` | Default delivery charge Rs.200 |
| 23 | `create discount: EID10, 10 percent, expires 15 days` | Code created |
| 24 | `create discount: WELCOME50, Rs.50 flat` | Code created |
| 25 | `create loyalty: 3 orders = 10 percent off` | Loyalty rule created |
| 26 | `share catalog` | Shareable catalog text |
| 27 | `update business info` | Asks for details; send `Ayesha Collections, Lahore, Clothing, @ayesha.collections` |

**Tests:** catalog CRUD, price tiers, stock tracking, payments, delivery default, discounts, loyalty, business info.

## Phase 3 — Orders: every shape

| # | Send | Expect |
|---|------|--------|
| 28 | `Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore` | Draft summary (2 × 3500 + delivery 200 = Rs.7,200) → reply `yes` → **Order #1** saved. **Low-stock warning** (Lawn Suit left 1) |
| 29 | `Bilal, 1 kurti, 03211234567, DHA Karachi, code EID10` | Draft with 10% off → `yes` → Order #2 |
| 30 | `Hassan, 1 lawn suit` (no phone) | **Follow-up asks for phone** → send `03331234567` → draft → `yes` → #3. Lawn Suit now 0 → "out of stock" warning |
| 31 | `Sara 2 kurti aur Zainab 1 dupatta, Sara 03451234567, Zainab 03009998888` | **Two orders in one message** → both confirmed with one `yes` |
| 32 | `Ayesha, 3 kaju, 03001234567, delivery 300` | Kaju 3 kg; delivery charge picked up from the text (300, not 200) |
| 33 | `Hina, 12 kaju, 03111234567, Islamabad, advance 2000` | Wholesale tier (10kg+ rate) applied; advance 2000 saved |
| 34 | `Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore` again within minutes | **Duplicate-order warning** → answer `no` |
| 35 | `Rabia, 1 cotton suit, 03221234567` (product not in catalog) | Asks: **1** new product / **2** another name for an existing one → reply `1` → asks price → `2500` → continues the order |
| 36 | `Nida, 1 kurti aur 1 dupatta, 03401234567` | If it asks "separate orders or one?" → reply `2` (one order) |
| 37 | `order 1` (also `#1`) | Full order detail: items, total, payment, status |

**Tests:** AI order parsing, missing-field follow-up, multi-customer, discount codes, delivery-in-text, tier pricing, advance, duplicate guard, unmatched-product flow, grouping choice, stock warnings.

## Phase 4 — Fulfilment, payment, edits, undo

| # | Send | Expect |
|---|------|--------|
| 38 | `mark 1 shipped` | Status → Shipped |
| 39 | `add tracking: Leopards, LC998877` | Tracking saved on the latest order |
| 40 | `Ayesha ka tracking` | Shows courier + number |
| 41 | `mark 1 delivered` | Delivered; for COD asks "cash collected?" → `yes` |
| 42 | `order 2 advance 500` | Part payment recorded, balance shown |
| 43 | `mark 2 paid` | Fully paid |
| 44 | `mark 3 shipped`, then `mark 3 returned` | Returned only works after shipping; stock is restored |
| 45 | `cancel order 4` → `yes` | Cancelled; stock restored |
| 46 | `undo` | Last action reversed |
| 47 | `edit order 2` → `price 1 = 1500` → `qty 1 = 2` → `phone 03219998888` → `delivery 250` → `done` | Totals recomputed each step; **catalog price unchanged** |
| 48 | `undo` | Whole edit restored in one go |
| 49 | `Bilal ka order` | Bilal's orders list |
| 50 | `receipt 1` | PDF receipt arrives as a document |
| 51 | `mark all pending as shipped` | Bulk confirmation → `yes` |
| 52 | `Sara ka phone 03459998888` | Customer phone updated (undoable) |

**Tests:** whole lifecycle, COD, part payments, returns, cancel, undo, edit mode, tracking, PDF, bulk status, customer correction.

## Phase 5 — Reports & customers

Send each; expect a sensible, non-empty answer (numbers must match your orders):
`orders today` · `pending orders` · `today's summary` · `unpaid orders` · `cod pending` · `trending products` · `slow movers` · `Lawn Suit ka report` · `loyal customers` · `discount performance` · `weekly summary` · `payment link`

| # | Send | Expect |
|---|------|--------|
| 53 | `customer list` | Customers with order counts |
| 54 | `search customer: Ayesha` | Matches |
| 55 | `delete customer Bilal` → `yes`, then `restore customer Bilal` | Hidden, then restored |
| 56 | `ayesha bahut khush thi order se` | Feedback saved (not an order) |
| 57 | `mera order kab aayega? — Bilal ne poocha` | Support query created, AI drafts a reply you can forward |
| 58 | `support queries`, then `mark 1 resolved` | Lists it; closes it |
| 59 | `broadcast: naya stock aa gaya hai, DM karein` | Audience/channel choice, then send/record |
| 60 | `export` (also `export orders 30 days`) | One Excel file with Orders, Customers, Catalog… |

## Phase 6 — Language, help, edge cases

| # | Send | Expect |
|---|------|--------|
| 61 | `change language` → English | Replies switch to English (numbers/phones unchanged); buttons still work |
| 62 | `change language` → اردو, then Roman Urdu again | Same |
| 63 | `menu`, `help`, `guide` | Menus / guide steps; guide mid-flow doesn't lose state |
| 64 | `aaj bohat thak gayi hoon` | Polite off-topic reply, no order |
| 65 | `asdfgh 123` | Clarification question with options, never a crash |
| 66 | Send an Instagram DM **screenshot** of an order | AI reads it into a draft |
| 67 | Send a JazzCash payment **screenshot** | Recognised as a receipt → asks which order |
| 68 | Send a picture captioned `logo` | Saved; next `receipt` shows it |
| 69 | `subscribe` | Plan options (payment is trust-based + founder alert) |

## Phase 7 — Voice (speak, don't type)

Speak each in your normal Urdu/Roman-Urdu. Check the `🎤 Maine suna` / `➡️ Samjha` echo.

| Say | Expect |
|-----|--------|
| "Ayesha ka order, do lawn suit, phone zero teen zero zero ek do teen char paanch chhe saat" | Same order draft as typed |
| "Aaj ke orders dikhao" | `orders today` runs **immediately** (no confirmation) |
| "Mere paas teen khaddar chadar aur do wool dupatte naye products hain" | Treated as products, asks prices — not an order |
| "Hassan ke order mein suit ki price pandrah sau kar do" | Shows `edit order N → price 1 = 1500 → done` and asks **YES/NO**; `yes` applies, catalog price untouched |
| "Hassan ke order mein suit ki price kam kar do" (no number) | Bot asks **"kitni rakhni hai?"**; answer by voice "pandrah sau" |
| "Order char shipped kar do" | Asks YES/NO (status change) |
| Order draft showing → say "haan" | Confirms the order |
| At "1 ya 2" → say "pehla wala" | Picks option 1 |
| Say a customer name that is close to two saved ones | Asks which one |

---

## Final checks (accounting for the whole day)

1. `today's summary` totals = sum of non-cancelled, non-returned orders **including delivery**; Cancelled/Returned shown separately.
2. `unpaid orders` balance = total − advances/payments.
3. `stock` matches: sold items deducted, cancelled/returned restored, undo reverses it.
4. `export all` — open the Excel: order totals, items, customers, loyalty rule all consistent with the chat.
5. After 3 orders from the same customer, the loyalty discount offer appears on their next order.

## Optional destructive finish (throwaway number only)

`reset account` → `yes` → everything wiped, onboarding restarts.

## What to send me if anything fails

The **phase + step number**, the exact message you sent, and a screenshot of the bot's reply. For voice, also the `🎤 Maine suna` line.
