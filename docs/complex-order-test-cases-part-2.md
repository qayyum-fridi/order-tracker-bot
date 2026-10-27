# Complex order test cases, part 2 (after the order is saved)

Continues [complex-order-test-cases.md](complex-order-test-cases.md). Part 1 covers placing multi-product orders. This part covers what happens next: status, payments, undo, dates, reports, discounts, customers, screenshots, billing and concurrency.

Same catalog as part 1 (Zara Boutique: Lawn Suit 3500 stock 10, Kurti 1800, Dupatta 600, Sharara Set 4200, Shawl 2500, Polo Shirt 500). Shop timezone Asia/Karachi.

"Risk" says where a case is likely to break: a known gap from `CLAUDE.md`, or "unverified" when it is a guess to check by running it.

---

## M. Status changes on several orders at once

Setup: orders 12 (Ayesha, Lawn Suit x2 + Kurti x1, Pending), 13 (Bilal, Dupatta x3, Pending), 14 (Sara, Sharara Set x1, Shipped), 15 (Sara, Kurti x2, Delivered).

| ID | Messages | Expected | Risk |
|---|---|---|---|
| M1 | `mark 12 13 shipped` | Both move to Shipped. Reply lists both. | Space-separated ids. |
| M2 | `mark 12, 13 shipped` | Same as M1. | Comma-separated ids. |
| M3 | `mark 12 and 13 shipped` | Same as M1, or asks. | "and" inside a command. |
| M4 | `Sara ke sab orders delivered` | Order 14 becomes Delivered; 15 is already Delivered and is said so. | Mixed-state bulk change. |
| M5 | `mark 12 delivered` where order 12 is Pending | Refused or step-by-step: Pending to Delivered skips Shipped. Clear message. | Skipped states. |
| M6 | `mark 14 returned` (Shipped) then `mark 15 returned` (Delivered) | Both Returned. Returned orders excluded from sales. | Returned from Shipped/Delivered only. |
| M7 | `mark 13 returned` where order 13 is Pending | Refused: a pending order is cancelled instead. Message says so. | Documented rule. |
| M8 | `mark 12 shipped` then `12 tracking TCS 77889900` then `mark 12 delivered` | Tracking saved; status Delivered; delivery time measured from ShippedAt. | Delivery-time fallback. |
| M9 | `mark 12 13 14 15 shipped` (14 already Shipped, 15 Delivered) | Each result listed separately; no silent failure of the rest. | Partial success in bulk. |
| M10 | `mark 99 shipped` (no such order) and `mark 12 shipped` in one message | Order 99 reported as not found; order 12 still changes. | One bad id must not block the rest, or the whole batch must refuse. Expect one of these, stated clearly. |

## N. Payments on multi-item orders

Setup: order 12 = Lawn Suit x2 + Kurti x1, total 8800, delivery 250, Pending, COD.

| ID | Messages | Expected | Risk |
|---|---|---|---|
| N1 | `order 12 advance 1000` then `order 12 advance 1000` | Amount paid 2000; balance 7050; state Partly paid. | Repeated advance. |
| N2 | `12 paid 9050` | Paid; balance 0; status Paid. | Full payment as "paid". |
| N3 | `12 paid 9000` (total 9050) | Balance 50 shown; not Paid. | Off-by-a-little. |
| N4 | `mark 12 paid` after an advance of 1000 | Full total marked paid (MarkFullyPaid); previous amount 1000 logged for undo. | Mark paid with a part payment. |
| N5 | `cod collected 12` when balance is 0 | Refused or says already paid. | COD on paid order. |
| N6 | `unpaid orders` after order 12 is edited to remove Kurti | Balance follows the new total, not the old one. | Stale balance after edit. |
| N7 | `payment cod` on order 12 that was Paid by bank | Refused or asks; no silent change of paid method. | Changing a paid order's method. |
| N8 | `12 paid 1000 jazzcash` then `12 paid 1000 cash` | Two payments of 1000 each, method of each recorded. | Method per payment. |

## O. Cancel, undo and restore

| ID | Messages | Expected | Risk |
|---|---|---|---|
| O1 | Save `Lawn Suit 2, Kurti 1` (Lawn Suit stock 10 to 8) as order 16. Then `cancel 16` then `yes` | Stock back to 10. Order Cancelled. Excluded from sales. | Stock footprint on cancel. |
| O2 | Same, then `undo` right after the cancel | Order back to its previous state; stock 8 again. | Undo of a cancel. |
| O3 | Edit order 12 (change Lawn Suit qty 2 to 4) then `undo` | Items and amounts restored; stock restored; customer name/phone unchanged. | Undo of an edit with stock. |
| O4 | Save an order, then save an expense, then `undo` | Only the expense is undone (undo works on the last action). | Undo scope. |
| O5 | Save order 17, then `Sara ka phone 03009990000`, then `undo` | The phone correction is undone; order 17 untouched. | Undo order for different actions. |
| O6 | Save order 18 with Lawn Suit x2, then `mark 18 shipped`, then `undo` | Undo of the status change only: back to Pending; stock still deducted (order not cancelled). | Undo scope on status. |
| O7 | `cancel 12` where order 12 is Delivered | Refused, or asks; delivered orders are not silently cancelled. | Cancel after delivery. |

## P. Dates, backdating and timezone

| ID | Messages | Expected | Risk |
|---|---|---|---|
| P1 | `kal Ayesha 2 suit, Kurti 1 cash` | Order created with SoldAt yesterday; appears in yesterday's summary, not today's. | Backdate parsing. |
| P2 | `parso Sara Dupatta 2` | Dated two days back. | "parso" both future and past word. |
| P3 | Message sent at 23:58 PKT Sunday, webhook received at 00:03 PKT Monday | Order dated by send time (Sunday 23:58) if within 3 days; belongs to Sunday's sales. | RecordTimeUtc and day boundary. |
| P4 | Message sent while the phone was offline for 4 days | Timestamp ignored (older than 3 days); order stamped with now and the reply says so. | 3-day cutoff. |
| P5 | `orders today` at 00:30 PKT, with an order placed 23:50 PKT yesterday | Yesterday's order not in today's list. | Day boundary per seller timezone. |
| P6 | `last 7 days` report, with an order at 22:00 PKT on the window's edge | Documented as UTC-based: record which day the order lands in. | UTC window (documented gap). |
| P7 | `kal ka hisaab` while a cash drawer is open from yesterday | Yesterday's day account shows the drawer's expected cash for that day. | Drawer across midnight. |

## Q. Reports after multi-item orders

Setup: order 20 (Ayesha: Lawn Suit x2, Kurti x1, delivery 250, discount EID10 10%, Pending today), 21 (Bilal: Dupatta x3, Cancelled), 22 (Sara: Sharara x1, Returned), 23 (Ali: Polo Shirt x4, Pending today, no cost price).

| ID | Message | Expected | Risk |
|---|---|---|---|
| Q1 | `orders today` | Shows 20 and 23; not 21 (cancelled) or 22 (returned). | Excluded statuses. |
| Q2 | `today's summary` | Revenue = order 20 total (subtotal minus 10% plus 250 delivery) + order 23 total. Delivery included. Returned count shown. | Delivery in sales totals. |
| Q3 | `profit` | Order 20 profit uses UnitCost per item; order 23 (no cost) excluded and counted, not guessed. | Missing cost handling. |
| Q4 | `margin` | Products with cost listed; Polo Shirt with no cost counted as missing, not 0% margin. | Missing cost. |
| Q5 | `aaj ka hisaab` | Product sold most by units: Polo Shirt (4) beats Lawn Suit (2). | Top product by units. |
| Q6 | `category sales` | Lawn Suit and Kurti split by their categories; discount not spread, so total can differ from summary (said). | Discount not spread. |
| Q7 | `Lawn Suit ka report` | Units and amount from order 20 only; cancelled/returned excluded. | Units per product. |
| Q8 | `export orders today` | One row per order, items kept; totals match the summary; Urdu names kept. | Export of multi-item. |

## R. Discounts and loyalty with multi-item orders

| ID | Messages | Expected | Risk |
|---|---|---|---|
| R1 | `create discount: EID10 10%` then `Ayesha 2 suit, Kurti 1, code EID10` | 10% on subtotal only; delivery not discounted. | Percent on multi-item. |
| R2 | `create discount: FLAT300 300 off` then `Kurti 1, Dupatta 1, code FLAT300` | Flat 300 applied once to the whole order, not per item. | Flat per order vs per item. |
| R3 | `Kurti 1, code FLAT300` where Kurti costs 1800 but a 2000 flat discount exists | Discount capped at the subtotal: total = 0 + delivery. Never negative. | Cap. |
| R4 | Code used on order 1; then `Kurti 1, code EID10` for the same customer | Reuse allowed or refused per the discount rule; record which. | Reuse of a code. |
| R5 | Customer Sara has 4 previous orders; new order `Lawn Suit 1, Kurti 1` crosses the loyalty threshold | Loyalty discount offered for confirmation; not applied silently. | Loyalty confirmation. |
| R6 | Loyalty discount offered, seller answers `no`, then sends `Dupatta 1` | Order saved without loyalty; Dupatta starts a new draft, not an answer to loyalty. | Confirmation routing. |
| R7 | `create discount` with expiry yesterday, then used | Refused: says expired. | Expiry date. |

## S. Instagram / screenshot orders (AI image path)

Needs an OpenAI key. Mark as AI-only.

| ID | Input | Expected | Risk |
|---|---|---|---|
| S1 | Screenshot of an Instagram DM: "Lawn Suit (Blue, M) x2, Dupatta x1, total Rs. 8100" | Two items; size and colour kept as attributes; total matches the screenshot. | Screenshot totals vs catalog price. |
| S2 | Screenshot of an Easypaisa receipt for Rs. 5000 sent by a buyer | Recognised as a payment receipt, not an order. Asks what to do with the amount. | Receipt vs order. |
| S3 | Screenshot with 3 buyers' messages in one chat | Grouped per buyer or asks; never all items on one customer. | Multi-customer screenshot. |
| S4 | Screenshot plus caption "Ayesha ka order, advance 500" | Caption and image both used; advance 500 saved; customer Ayesha. | Caption vs image. |
| S5 | Blurry screenshot, phone unreadable | Phone asked; order not saved with a guessed phone. | Guessing from blur. |
| S6 | Screenshot of a product the seller does not sell | Product flagged as unknown and asked as add or map. | Unknown product in screenshot. |

## T. Customer data after the order

| ID | Messages | Expected | Risk |
|---|---|---|---|
| T1 | Order 20 for Sara, then `Sara ka address Gulberg 3 Lahore` | Customer record updated. Record whether order 20 shows the new address (snapshot) or the old one. | Snapshot vs shared record (documented: edits change the shared record). |
| T2 | `order for Sara` then `Kurti 2, Dupatta 1` | Draft with Sara's phone and address prefilled; items as typed. | Order-for flow. |
| T3 | `order for Sara`, then `cancel`, then `Kurti 1` | Cancel clears the armed state; Kurti 1 is a plain draft with no customer prefilled. | Armed state after cancel. |
| T4 | `Sara ka phone 0300 1234567` when two customers already share the last 10 digits 1234567 | Ambiguous: bot lists the matches, does not guess. | Duplicate phone digits. |
| T5 | Customer Sara exists with phone 03001234567; new order `Sara 0300-1234567, Kurti 1` | Matched to the same customer (phone normalized). | Phone forms matching existing customer. |
| T6 | Customer with no phone: order `Hina, Kurti 1`, later `Hina ka phone 0333 4445556` | Phone saved; earlier order shows the phone too. | Phone added later. |

## U. Receipts, labels and printing with many items

| ID | Message | Expected | Risk |
|---|---|---|---|
| U1 | `receipt 20` (order with 12 different items) | PDF lists all 12 lines, totals match the order, delivery and discount shown separately. | Long receipt. |
| U2 | `label 20` where order 20 is partly paid (balance 4000) | COD amount to collect = 4000 (balance), not the total. | Label amount with advance. |
| U3 | `label 22` where order 22 is Returned | No label: cancelled or returned orders get none. | Documented rule. |
| U4 | `receipt size 58mm` then `receipt 20` with a long Urdu product name | Name wraps or truncates visibly; no crash; receipt still produced. | Thermal layout (unrendered per docs). |
| U5 | `receipt 20`, then `note 20 gift wrap`, then `receipt 20` again | Second receipt shows the note; receipt number not used up twice. | Receipt serial. |

## V. Onboarding and setup order

| ID | Messages | Expected | Risk |
|---|---|---|---|
| V1 | New seller, first message is `Ayesha 2 suit, Kurti 1` | Bot asks for onboarding or holds the order; the order is not saved before the shop name exists. | Order before setup. |
| V2 | Onboarding at product step, seller sends a multi-product order with a phone | Treated as an order (not products to add), or asks which the seller means. | Add-products vs order during onboarding. |
| V3 | Onboarding: `Lawn Suit 3500, Kurti 1800, Dupatta 600` (three product lines) | Three products saved in one go, each with its price; listed back. | Bulk product add in setup. |
| V4 | Onboarding: `Lawn Suit 3500 Kurti 1800` (no comma, no dash) | Two products, or asks. Not one product "Lawn Suit 3500 Kurti" at 1800. | Inline product split. |

## W. Billing and limits

| ID | Setup | Message | Expected | Risk |
|---|---|---|---|---|
| W1 | Trial expired seller | `Ayesha 2 suit, Kurti 1` | Blocked by billing with the subscribe prompt; no order saved. | Billing gate on order path. |
| W2 | Free seller (id within FreeSellerLimit) | 30 multi-item orders in one hour | Text rate limit (150 per hour) not hit; budget not exceeded. | Rate limit. |
| W3 | Seller sends 40 text messages in one hour | 40th message gets a limit reply; no order partly saved. | Rate-limit reply mid-draft. |
| W4 | Seller sends 16 images in one hour (image limit 15) | 16th refused with a clear message; text still works. | Per-kind limits. |

## X. Concurrency, retries and ordering

| ID | Setup | Expected | Risk |
|---|---|---|---|
| X1 | Two messages from the same seller within 100 ms: `Ayesha 2 suit` then `Kurti 1` | Same lane, in order; one draft with both lines or two drafts in order. Never swapped. | Lane ordering. |
| X2 | Two sellers at the same time, both send `orders today` | Each sees only their own orders. | Tenant isolation. |
| X3 | Webhook redelivered with the same message id while the first is still being handled | Second is dropped as Duplicate; one order. | Claim race. |
| X4 | Server restarts after the order row was claimed but before the reply was sent | Seller asked to resend (OTB-5005); no second order created. | Interrupted handling. |
| X5 | Queue lane full (1000 messages) | 503 returned so Meta retries; no message lost. | Lane overflow. |
| X6 | Scheduled weekly summary runs while the seller is saving an order | Summary sent once; order saved; no double sends. | Scheduler and webhook overlap. |

---

## How to run

1. Use the Zara Boutique catalog and the setup rows above, in order, on a fresh database.
2. Send the messages as separate webhook posts, in the order shown.
3. Rows marked AI-only (S, and any free-text row with no deterministic command) need an OpenAI key.
4. Record the reply and the database state. A row fails if money, stock or status is wrong, if a record is attached to the wrong customer, or if a reply hides a failure.

## Highest-risk rows

M10, M5, M6, N4, O2, O6, P1, P3, Q2, Q5, R2, R3, R5, S3, S4, T1, V1, W1, W3, X3, X4
