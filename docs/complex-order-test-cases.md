# Complex order test cases (multi-product, real-world)

Goal: try to break the order path with messages a busy seller really sends: several products per message, several customers per message, phone/address/advance/delivery mixed into item lists, half-finished drafts, and voice notes.

Each case lists the message(s) in the order the seller sends them, the expected result, and a **Risk** note. Risk notes say where the code path is known to be weak (from `CLAUDE.md` or the code); "unverified" means the case is a guess that needs a run.

Assumed catalog for most cases (seller "Zara Boutique"):

| Product | Price | Category | Stock |
|---|---|---|---|
| Lawn Suit | 3500 | Lawn | tracked: 10 |
| Kurti | 1800 | Kurti | not tracked |
| Dupatta | 600 | Lawn | not tracked |
| Sharara Set | 4200 | Bridal | not tracked |
| Shawl | 2500 | Winter | not tracked |
| Polo Shirt | 500 | Men | not tracked |

Unknown to the catalog unless stated: Bag, Chadar, Cap.

---

## A. Several products in one message (single customer)

| ID | Messages (in order) | Expected | Risk |
|---|---|---|---|
| A1 | `Ayesha 2 suit, 03001234567, advance 500` | Customer Ayesha, phone 03001234567, item "suit" x2 matched to Lawn Suit (only if unique), advance 500 saved. Not a product named "Ayesha 2 suit". | Open bug in `CLAUDE.md`/last session: product name swallowed the customer name. |
| A2 | `Sara 0321 1112233, Lahore\nKurti 2\nDupatta 1\nSharara Set 1\nLawn Suit 1` | One order, four lines, phone normalized to 03211112233, City Lahore. Total = 2×1800 + 600 + 4200 + 3500 = 12,900. | Multi-line with name line first. |
| A3 | `Kurti 2, Dupatta 1, Shawl 1, Lawn Suit 1, 03001112222` | Shawl and Lawn Suit priced from catalog; the phone is not a product. Order saved after the customer name question, if no name, asked once. | Phone after the item list. |
| A4 | `kurti 2 dupatta 1 shawl 1 ayesha 03001234567` (no punctuation, lower case) | Items split correctly with no commas; customer Ayesha. | Without separators the AI must guess where names end. Risk: "shawl 1 ayesha" read as one name. |
| A5 | `2 kurti aur 1 dupatta Sara ko, 0300-1234567` | Kurti x2, Dupatta x1, customer Sara, phone 03001234567. | Urdu connector "aur"; hyphenated phone. |
| A6 | `Kurti 2, Dupatta 1, Bag 1, Cap 2, Chadar 1` (three unknown) | Each unknown gets an add-or-map question in turn. Bot must not drop any of them silently. The order is saved only after all are resolved or the seller cancels. | Documented: only the first unmatched product walks through add/map. Expect the 2nd and 3rd to be lost. |
| A7 | Same as A6, but the seller answers `1` (add) to Bag, then `2 Kurti` to a did-you-mean on Cap | Bag saved with a price question, Cap mapped to Kurti; Chadar still asked. Total recomputed. | Mixed add and map answers in one draft. |
| A8 | `Kurti 1, Kurti 1, Kurti 1` | Either one line Kurti x3 or three lines; total 5400. Must not lose any line. | Repeated item names may be merged or deduplicated. Expect merge to x3 and the total to match. |
| A9 | `3 piece lawn suit 2, kurti 1` | "3 piece" is a pack/name word, not qty 3. Lawn Suit x2 (if "3 piece lawn suit" matches catalog) or asks. | Digits inside names (`BareProductLineDigits`); qty vs pack ambiguity. |
| A10 | `Polo Shirt 500 x 3, Polo Shirt 500 x 2` | Polo Shirt x5 merged, or two lines; total 2500 (not 4000). | Price 500 read as qty or as a second product. |
| A11 | 15 items in one message (mix of catalog and unknown) | All 15 lines appear in the draft or the bot asks to split; none silently dropped. Reply stays under WhatsApp body limits. | Length and body limits (1024 chars for interactive). |

## B. Several customers in one message

| ID | Messages | Expected | Risk |
|---|---|---|---|
| B1 | `Ayesha 2 suit, Bilal 1 kurti, Sara 1 dupatta` | Three customers. Either three drafts in sequence or a grouping choice. No item attached to the wrong buyer. | `AdditionalOrders` path; wrong attribution is the worst failure. |
| B2 | `Ayesha 2 suit 03001111111\nBilal 1 kurti 03002222222\nSara 1 dupatta 03003333333` | Three separate orders, each with its own phone. Reply lists all three, and the customers are not merged by the 10-digit phone match. | Phone-based customer matching must not merge them. |
| B3 | `Ayesha 2 suit, Ayesha 1 kurti` (same name, no phone) | One customer Ayesha, two items. Or the bot asks which Ayesha. Not two Ayesha customers. | Duplicate names. |
| B4 | `Ayesha 2 suit, Bilal 1 kurti` then `ek order mein dono` (one order) | Both items in one order for Ayesha? or Bilal? Bot must ask whose; not guess. | Grouping choice with no customer. |
| B5 | `Ayesha 2 suit 03001234567, Bilal 1 kurti 03001234567` (same phone, two names) | Phone is shared; expected the bot asks whether it is one customer or two. | Shared phone (family or a courier number). |

## C. Quantities and units

| ID | Message | Expected | Risk |
|---|---|---|---|
| C1 | `2x Kurti, 1 x Dupatta` | Kurti x2, Dupatta x1. | "2x" and "1 x" spacing. |
| C2 | `Kurti x2 Dupatta x1` | Same. | No comma. |
| C3 | `do kurti aur ek dupatta` | Kurti x2, Dupatta x1 (Urdu number words). | Number words in Roman Urdu. |
| C4 | `Kurti 0, Dupatta 1` | Kurti refused (quantity 0), Dupatta kept, bot says which line was refused. | Zero quantity must not save a Rs.0 line silently. |
| C5 | `Kurti 1000` | Read as price 1000 for a product, or qty 1000? Must ask, not create 1000 units. | Qty over 999 is refused by the walk-in parser; main order path unverified. |
| C6 | `Lawn Suit 2 pack, Kurti 1 dozen` | Pack and dozen are units. Qty and unit kept; no silent x12. | Units not in catalog. |
| C7 | `Kurti -2, Dupatta 1` | Negative qty refused. Bot says the line is not valid. | Minus sign read as a dash separator. |

## D. Phone, address and name traps inside item lists

| ID | Message | Expected | Risk |
|---|---|---|---|
| D1 | `Sara 0300 1234567\nKurti 2` | Phone 03001234567. | Space inside phone. |
| D2 | `Sara +92 300 1234567, Kurti 2` | Phone normalized to 03001234567. | +92 form. |
| D3 | `Sara 030011122, Kurti 2` | Phone asked again (only 9 digits, documented guard). Not saved. | Documented guard; test it stays. |
| D4 | `Sara, House 12 Street 4 Model Town Lahore, Kurti 2` | Address kept; "12" and "4" are not quantities or products. | Numbers in address vs qty. |
| D5 | `Kurti 2 (Model Town, Lahore) 03001234567` | Kurti x2, city Lahore, phone saved. | Parentheses. |
| D6 | `Kurti 2. Dupatta 3. Phone 03001234567.` | Kurti x2, Dupatta x3 (not 2.3 or "3. Phone"). | Dots as sentence breaks vs decimals. |
| D7 | `Kurti 2, Dupatta 1, Ayesha ka phone 0300 1234567 aur address Gulberg` | Phone and address for Ayesha; items kept. | Free-form field labels. |
| D8 | `Kurti 2, Dupatta 1, cod` | Payment COD; not a product named "cod". | Payment words. |
| D9 | `Kurti 2, Dupatta 1, jazzcash` | Payment method JazzCash (or Manual per spec). Not a product. | Payment word vs product. |

## E. Price guard (stated price differs from catalog)

| ID | Messages | Expected | Risk |
|---|---|---|---|
| E1 | `Kurti 3000 x2, Dupatta 600` | Price question for Kurti only (3000 vs 1800): 1 = 3000, 2 = catalog 1800, 3 = update catalog. Dupatta not questioned. | Only one price question at a time. |
| E2 | `Kurti 3000, Lawn Suit 4000` (both differ) | Two questions in sequence, not both answered by one tap. Each choice applies to its own line. | Answer routing across two questions. |
| E3 | `Kurti 1800, Dupatta 600, Lawn Suit 3500` (all match) | No price question. Total 5900. | False positives. |
| E4 | `Kurti 1800 per kg x2` (kg listing) | Not compared (documented). No question. | Per kg wording. |
| E5 | `Lawn Suit 3500, delivery 250, Kurti 2` | Delivery 250 is not a product price; no price question for it. | Delivery number taken as a price for the previous product. |

## F. Discount, delivery and advance inside a multi-item order

| ID | Message | Expected | Risk |
|---|---|---|---|
| F1 | `Ayesha 2 suit, Kurti 1, delivery 250, advance 1000, code EID10` | Subtotal 2×3500 + 1800 = 8800. EID10 (10%) on subtotal = 880 off. Total = 8800 − 880 + 250 = 8170. Advance 1000; balance 7170 COD. | Discount must not touch delivery; advance must not be a product. |
| F2 | `Ayesha 2 suit, free delivery, advance 500` | Delivery 0. Total 7000, balance 6500. | "free delivery" with advance. |
| F3 | `Kurti 1, advance 1800` (advance = exact total) | Order Paid, not partly paid. | Advance equal to total. |
| F4 | `Kurti 1, advance 2000` (advance > total) | Refused or asks; balance negative must not be saved silently. | Overpayment. |
| F5 | `Lawn Suit 1, advance 500, Kurti 1, advance 300` (two advances) | One advance asked, or summed to 800 with a note. Not both silently kept. | Two advance numbers. |
| F6 | `Lawn Suit 1, Kurti 1, code WRONG` | Unknown code: bot says so and keeps the order without discount. | Bad code in multi-item. |
| F7 | `Ayesha 2 suit, delivery 300, delivery 500` | Conflict asked, or last one wins with a note. | Conflicting delivery values. |

## G. Editing a multi-item saved order

Setup for G cases: saved order 12 = Ayesha: Lawn Suit x2, Kurti x1, Dupatta x1.

| ID | Messages | Expected | Risk |
|---|---|---|---|
| G1 | `edit order 12` then `1 = 3` then `remove 2` then `add Sharara Set 1` then `done` | Lawn Suit x3, Kurti removed, Sharara Set added. Totals recomputed. One undo restores all. | Sequence of single instructions. |
| G2 | `edit order 12` then `1 = 3, remove 2, add Sharara Set 1` (one message) | Documented: one instruction per message. Expect a clear "one change at a time" reply, not a partial apply. | Partial apply is the failure to look for. |
| G3 | `edit order 12` then `price 1 = 1500` then `Lawn Suit 3` | Price and qty change for Lawn Suit; "Lawn Suit 3" is an item line in edit mode, qty or new? Expect either rejected or treated as a change to the existing line. | Edit mode and new-item ambiguity. |
| G4 | `edit order 12` then `remove 1` where line 1 is Lawn Suit and stock is tracked | Stock returned to 10 (plus 2 from the order). | Stock footprint on edit. |
| G5 | `edit order 12` then `phone 03009998888` | Customer phone changed everywhere (shared Customer record). Another saved order for Ayesha also shows the new phone. | Shared record side effect. |
| G6 | `edit order 12` then `Dupatta 1` where Dupatta is not in order and catalog has it | Added, not a duplicate line; the order shows Dupatta x2 or one line x2. | Duplicate line handling. |
| G7 | `edit order 12` then `1 = 0` | Refused or removes line 1. Must not save a Rs.0 line. | Zero qty in edit. |

## H. Interruptions in the middle of a multi-item draft

| ID | Messages | Expected | Risk |
|---|---|---|---|
| H1 | `Ayesha 2 suit, Kurti 1` then (question about product) `Bilal ka order` | Draft dropped or kept per escape rule; the lookup runs. No half-saved order. | Stuck-draft escape. |
| H2 | `Ayesha 2 suit, Kurti 1, Dupatta` (Dupatta unknown) then `Dupatta 600` | Dupatta priced 600; draft completes with Ayesha; total = 7000+1800+600 = 9400 minus nothing. | Price answer during draft. |
| H3 | Draft at Save/Edit/Cancel buttons, then seller types `Kurti 1` | Not yes/no. Expect the draft stays or a clear reply; Kurti not silently added. | Confirm state vs new item. |
| H4 | Draft at confirm, then `yes` then immediately `Kurti 1` | Order saved; Kurti starts a new draft. Two orders, not one with three items. | Message ordering. |
| H5 | Draft, then `cancel`, then `yes` | Cancel wins; the "yes" does nothing (no draft). | Yes after cancel. |
| H6 | Draft with two unknown products, seller types `cancel` after the first add question | Whole draft cancelled; second unknown product not asked after. | Partial state after cancel. |

## I. Stock and duplicates

| ID | Messages | Expected | Risk |
|---|---|---|---|
| I1 | Lawn Suit stock 3. `Lawn Suit 2, Lawn Suit 2` | Total 4 vs stock 3: order saved with oversell warning (stock goes to -1). | Merged lines vs stock check. |
| I2 | Lawn Suit stock 1. `Lawn Suit 1, Kurti 2` saved. Then `Lawn Suit 1` again | Low/out warning once per turn; stock 0 then -1 correctly. | Warning dedupe. |
| I3 | Same message sent twice within 1 minute by the seller (`Ayesha 2 suit, Kurti 1`) | Duplicate confirmation question. Not two identical orders silently. | Duplicate detection on multi-item. |
| I4 | Same webhook message id delivered twice (Meta retry) | Only one order. | Webhook claim. |
| I5 | Ayesha has order 12 Pending (Lawn Suit x2). Seller sends `Ayesha 2 suit, Kurti 1` | Possible-duplicate question; not a silent second order. | Duplicate vs new basket. |

## J. Walk-in / counter sales with several items

| ID | Message | Expected | Risk |
|---|---|---|---|
| J1 | `walkin Kurti 2, Dupatta, Lawn Suit 1 yes (cash)` | Dupatta unknown: question first; not saved. After answer, saved as Delivered + Paid, cash. | Confirm word inside a multi-item sale. |
| J2 | `counter sale Kurti 2 1800, Shawl 1 2500, 1000` | Two items with prices; the bare 1000 is an extra amount, asked about, not a third item. | Bare amount vs item. |
| J3 | `dukan par Kurti 1, Polo Shirt 2 bank kal` | Backdated to yesterday, payment bank (Manual). | Backdate + multi-item. |

## K. Voice notes (transcripts)

| ID | Transcript | Expected | Risk |
|---|---|---|---|
| K1 | "Ayesha ko do suit aur Bilal ko ek kurti, Ayesha ka phone zero teen sau ek ek ek..." | Two customers, amounts kept, phone digits faithful. | Spoken digits; faithfulness gate may refuse and ask to retype. |
| K2 | "Lawn suit teen hazaar, kurti do hazaar, dono ka order Sara ke liye" | Lawn Suit 3000 (not 3500 catalog), Kurti 2000, both for Sara; price question if catalog differs. | Spoken amounts vs catalog. |
| K3 | "Pehla wala do, dusra wala ek" (during a grouping question) | Mapped to option 1 and 2 of the question shown. | Ordinal references. |
| K4 | "Sara ka order cancel kar do aur Bilal ka Kurti 2 kar do" | Two commands: cancel Sara's order (risky, needs YES), and new Bilal order. Not one merged action. | Two actions in one voice note. |

## L. Hostile or messy input

| ID | Message | Expected | Risk |
|---|---|---|---|
| L1 | `🧕 Kurti x 2 ✨, 🎀 Dupatta` | Emoji stripped; items parsed. | Emoji in names. |
| L2 | `Kurti​ 2, Dupatta 1` (zero-width space) | Stripped; Kurti matches catalog, not a new product. | Look-alike product (documented fix). |
| L3 | `لان سوٹ 2، دوپٹہ 1، ayesha 03001234567` | Lawn Suit x2 (Urdu name matched), Dupatta x1, customer Ayesha. | Mixed script with Urdu comma `،`. |
| L4 | `Kurti 1\nKurti 1\nKurti 1` (identical lines) | Merged to x3 or kept as three lines. Total 5400. | Duplicate lines. |
| L5 | `Kurti 2, Dupatta 1,,, Shawl 1,` | Empty items ignored; no empty product. | Empty segments. |
| L6 | `Kurti 2 ; Dupatta 1 | Shawl 1 & Chadar 1` | All separators (`;`, `|`, `&`) work or each is handled; Chadar unknown asked. | Separators in the split regex. |
| L7 | `Ayesha 2 suit 03001234567 ' OR 1=1 --, Kurti 1` | Stored as plain text; no SQL effect; "OR" not a product. | Injection-like text (EF parameterized; check name stored literally). |
| L8 | 2,000-character message with 40 product lines | Bot replies within limits; either all parsed or it asks to split. Nothing truncated silently. | Length limits. |

---

## How to run

1. Load the catalog above (Zara Boutique) and stock Lawn Suit at 10.
2. Send each row's messages as separate webhook posts in order (use the curl in `CLAUDE.md`, changing the body text).
3. For AI cases (anything without a deterministic command), an OpenAI key is required. Without it the bot asks for clarification instead, so mark those rows as AI-only.
4. Record: saved order items, qty, prices, total, customer, phone, and the bot's reply. A row fails if any item is dropped, merged wrongly, or attached to the wrong customer.

## Highest-risk rows (run these first)

A1, A6, A7, B1, B4, E2, F1, G2, H2, I1, I3, K4, L4

