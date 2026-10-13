# Manual test plan — one full business day, all features

Send each message from the **seller's WhatsApp** to the bot, in order. "Expect" is what should come back. Tick each row.
Business used throughout: **Ayesha Collections, Lahore** (clothes + a few grocery items). Sample buyers: Sara (03001112222), Bilal (03004445555), Hina (03007778888).

**Setup**
- Use a fresh WhatsApp number, or send `reset account` then `yes` to wipe an old test account.
- 🔑 = needs `OpenAi:ApiKey` (free-form text, voice, screenshots). Without it the bot asks to rephrase; everything else works without a key.
- 📧 = needs `MerchantEmail:SmtpUser/SmtpPassword`. 🎤 = voice note. 📷 = picture.
- Button labels can differ slightly from the text here; typing the command always works.

---

## 1. Onboarding
| # | Send | Expect |
|---|---|---|
| 1.1 | `start` | Welcome + language buttons |
| 1.2 | `Roman Urdu` | Language set, setup prompt |
| 1.3 | `Setup shuru karein` | Asks shop name |
| 1.4 | `Ayesha Collections` | Saved, next question |
| 1.5 | `skip` (Instagram) | Skipped |
| 1.6 | `10 ke qareeb` (catalog size) | Asks for products |
| 1.7 | `Kurti - 1800` | Product saved |
| 1.8 | `done` | Setup complete, main help list |

## 2. Catalog and product details
| # | Send | Expect |
|---|---|---|
| 2.1 | `Lawn Suit - 3500, cost 2200, stock 20, color white` | Saved; confirmation echoes cost, stock, color. Quick bar after it: `walkin Lawn Suit 1` / `catalog` / `Menu` |
| 2.2 | `Dupatta - 600, cost 300, stock 50` | Saved |
| 2.3 | `Polo Shirt - 500, cost 300, stock 10, color white, fabric: cotton` | Saved with attribute fabric |
| 2.4 | `Sugar 5 kg - 500` | Saved as a 5 kg pack |
| 2.5 | `Kaju - price tiers: 1kg=320, 10kg=300, 25kg=280` | Bulk pricing saved (3 tiers) |
| 2.6 | `Rs. 2,200 Shawl - 2,200` or `Shawl - Rs. 2,200` | Price read as 2200 (commas / Rs accepted) |
| 2.7 | During onboarding product step only: `Handbag - abc` | Says which part was not understood (outside onboarding it is not a product line and goes to the AI) |
| 2.8 | `catalog` | List of all products |
| 2.9 | `Kurti - 1900` | Price updated to 1900 |
| 2.10 | `products category Shirts` | Filter works (empty or Polo Shirt if categorised) |
| 2.11 | `share catalog` | Shareable catalog text |
| 2.12 | `delete product Handbag` | Handbag removed (if it exists) |
| 2.13 🔑 | `teen chadar aur do dupatte naye products hain` | Treated as new products, asks prices (not an order) |

## 3. Stock
| # | Send | Expect |
|---|---|---|
| 3.1 | `stock` | Stock list: Lawn Suit 20, Dupatta 50, Polo 10 |
| 3.2 | `stock Kurti 20` | Kurti now tracked, 20 |
| 3.3 | `stock Kurti +10` | 30 |
| 3.4 | `stock Polo Shirt 4` | 4 |
| 3.5 | (after orders below) place an order that takes Polo Shirt to ≤3 | Low-stock warning once, after the reply |
| 3.6 | `stock Kurti off` | Kurti no longer tracked |

## 4. Customers
| # | Send | Expect |
|---|---|---|
| 4.1 🔑 | `Sara: Lawn Suit 2, 03001112222, Gulberg Lahore, COD` | Draft confirmation: Customer Sara, total 7,000, phone, COD |
| 4.2 | `yes` | Order #1 saved + action buttons (shipped / receipt…) |
| 4.3 🔑 | `Bilal: Kurti 1, Dupatta 2, 03004445555, Johar Town, easypaisa` | Draft total 3,100 (1900 + 1200), advance/manual payment |
| 4.4 | `delivery 200` | Draft total 3,300 |
| 4.5 | `yes` | Order #2 saved |
| 4.6 | `customer list` | Sara, Bilal |
| 4.7 | `search customer: Sara` | Sara's detail |
| 4.8 | `Sara ka phone 03005556666` | Updated, shows previous number; quick bar `order for Sara` / `Sara ka order` / `Menu` |
| 4.9 | `undo` | Phone back to 03001112222 |
| 4.10 | `Sara ka address Model Town` | Address updated |
| 4.11 | `Sara ka order` | Sara's orders listed |
| 4.12 | `customer Sara name Sara Khan` | Name changed |
| 4.13 | `delete customer Hassan` (not existing) | "not found", nothing breaks |

## 5. Order for a saved customer (no phone asked)
| # | Send | Expect |
|---|---|---|
| 5.1 | `order for Sara` (or `Sara ke liye order`) | "Sara ke liye order — items likhein" |
| 5.2 | `Kurti 2, Dupatta` | Draft for Sara with her phone/address, total 4,400 |
| 5.3 | `yes` | Order #3 saved, still one Sara in customer list |
| 5.4 | `order for Zainab` | "saved nahi hai" + how to start |
| 5.5 | `order for Sara` then `catalog` | Request dropped, catalog shown; a later `Kurti 2` is **not** treated as Sara's |

## 6. Order lifecycle
| # | Send | Expect |
|---|---|---|
| 6.1 | `orders today` | Orders #1–#3 |
| 6.2 | `pending orders` | Same, all pending |
| 6.3 | `order 1` | Full detail of order 1 |
| 6.4 | `mark 1 shipped` | Shipped; offers `label 1` |
| 6.5 | `add tracking: 1, TCS123456` (or `add tracking: TCS, 123456`) | Tracking saved |
| 6.6 | `mark 1 delivered` | Delivered; asks "Cash collect ho gaya?" for COD |
| 6.7 | `yes` | Paid; tells expected drawer cash if a drawer is open |
| 6.8 | `order 2 advance 500` | Part payment, balance shown |
| 6.9 | `unpaid orders` | Order 2 (balance), order 3 |
| 6.10 | `cod pending` | COD orders not yet collected |
| 6.11 | `mark 2 paid` | Paid in full |
| 6.12 | `edit order 3` | Edit mode |
| 6.13 | `price 1 = 1500` / `add Polo Shirt 1` / `remove 2` / `delivery 150` / `done` | Each applied one at a time; totals recomputed; stock follows |
| 6.14 | `undo` | The whole edit reverted |
| 6.15 | `cancel 3` (confirm) | Cancelled, stock returned |
| 6.16 | `mark 1 returned` | Rejected only if not shipped/delivered; order 1 is delivered so accepted |
| 6.17 | `mark 2 shiped` (typo) | Read as `shipped` |
| 6.18 | `mark all pending as shipped` | Bulk confirmation (or "Koi pending order nahi hai") |
| 6.19 | `Sara: Lawn Suit 2, 03001112222` twice within 30 min 🔑 | Second one warns "isi jaisa order…" duplicate |

## 7. Discounts and loyalty
| # | Send | Expect |
|---|---|---|
| 7.1 | `create discount: EID10, 10 percent, expires 15 days` | Discount code created |
| 7.2 | `create discount: SAVE200 200 off` | Flat discount (or follow its prompt) |
| 7.3 | `discount list` | Both codes |
| 7.4 🔑 | `Hina: Lawn Suit 1, 03007778888, code EID10` | Draft total 3,150 (10% off) |
| 7.5 | `create loyalty: 3 orders = 10% off` | Loyalty rule saved |
| 7.6 | Place 3 orders for Sara | After the 3rd, loyalty message |
| 7.7 | `loyal customers` | Sara listed |
| 7.8 | `discount performance` | Usage of EID10 |
| 7.9 🔑 | `create category Women Cloths` (typed, then as a voice note) | Must **not** open the discount flow (this goes to the AI, which now knows categories are not discounts) |

## 8. Counter (offline) sales
| # | Send | Expect |
|---|---|---|
| 8.1 | `walkin` | Usage help |
| 8.2 | `walkin Kurti 2, Dupatta` | Draft for **Walk-in**, no phone asked, "counter sale, paid", total 4,400 |
| 8.3 | `yes` | Saved Delivered + Paid |
| 8.4 | `counter sale 1500` | Draft "Counter sale" 1,500 |
| 8.5 | `offline sale Chai - 200 bank` | Ad-hoc item, bank payment |
| 8.6 | `walkin Kurti 1 kal` | Draft shows yesterday's date |
| 8.7 | Same `walkin Kurti 1` twice quickly | No duplicate warning, one Walk-in customer |
| 8.8 | `orders today` | Walk-in cash sales included, the backdated one is not |
| 8.9 | **Offline phone test:** turn on airplane mode, send `expense 300 tea`, wait 5 min, turn it off | Expense recorded at the time you sent it (check `expenses today`) |

## 9. Cash drawer
| # | Send | Expect |
|---|---|---|
| 9.1 | `drawer open 5000` | Drawer opened with 5,000 |
| 9.2 | `walkin Kurti 1` then `yes` (cash) | Counted as drawer cash |
| 9.3 | `cash in 500 float` | +500 |
| 9.4 | `cash out 200 chai` | -200 |
| 9.5 | `cash undo` | Last movement reverted |
| 9.6 | `drawer` / `drawer status` | Expected = 5000 + cash sales + 500 − 200 (bank/easypaisa sales NOT counted) |
| 9.7 | Mark a COD order paid | Reply tells what the drawer should hold |
| 9.8 | `drawer close 9000` | Shows expected vs counted and the difference |
| 9.9 | `drawer history` | Previous sessions |
| 9.10 | `galla` | Ambiguity taps (drawer commands) |

## 10. Courier payouts
Prepare: 3 COD orders (1900 + 3500 + 1800 …), each `mark N shipped`, `add tracking: N, TCS<num>` (courier TCS), `mark N delivered`, answer **no** to "cash collected".
| # | Send | Expect |
|---|---|---|
| 10.1 | `payout pending` | TCS: 3 orders, total, days waiting |
| 10.2 | `payout 7000 TCS orders 4 5 6 fee 300` | Says "Barabar" if 7,000 = total − fee, else KAM/ZYADA; orders marked paid |
| 10.3 | `payout list` | The payout |
| 10.4 | `payout undo` | Orders unpaid again |
| 10.5 | `payout skip 4` | Order 4 no longer expected from courier |
| 10.6 | `payout 99999 TCS orders 999` | Refused (unknown order), nothing changed |
| 10.7 | Drawer check | Courier-settled orders do not add to drawer cash |

## 11. Receipts, printing, labels, email
| # | Send | Expect |
|---|---|---|
| 11.1 | `receipt 1` (also `receipt`, `Sara ki receipt`, `رسید 1`) | PDF receipt document |
| 11.2 | `note 1 Gift wrap karna hai` | Note saved; appears on receipt |
| 11.3 | `receipt size 80mm` | Setting saved; next receipt is a narrow roll PDF |
| 11.4 | `receipt size 58mm` / `A5` / `A4` | Each switches the layout |
| 11.5 | `receipt footer Shukriya, dobara tashreef layein!` | Footer on receipts |
| 11.6 | `receipt settings` | Shows size, footer, email |
| 11.7 | `printer` | Printer guide |
| 11.8 | `label 1` | Shipping label PDF (to/from, order, COD amount, tracking, note) |
| 11.9 | `business label` | Shop sticker |
| 11.10 | `label size 4x6` / `a6` / `50x30` | 50x30 = business label only |
| 11.11 📧 | `email you@gmail.com` then `email receipt 1` | Receipt arrives by email; reply confirms |
| 11.12 📧 | `email receipt 1 to buyer@example.com` | Sent to that address |
| 11.13 📷 | Send a picture captioned `logo` | Logo saved, appears on next receipt |
| 11.14 | Print the 80mm receipt, 4x6 label and 50x30 sticker on a real printer | **Layouts were never rendered on real hardware — check these by eye** |

## 12. Barcode scanner
| # | Send | Expect |
|---|---|---|
| 12.1 | `barcode Kurti 8961234567890` | Code stored |
| 12.2 | `barcode Dupatta 8961234567891` | Stored |
| 12.3 | Send `8961234567890` (as the scanner would) | "Kurti" added to scan cart |
| 12.4 | Send `8961234567890` again, then `8961234567891` | Kurti ×2, Dupatta ×1 |
| 12.5 | `scan cart` | Cart with total |
| 12.6 | `scan done` | Draft asks customer name/phone, then confirm |
| 12.7 | `scan clear` | Cart emptied |
| 12.8 | Send `03001112222` (a phone) | **Not** treated as a barcode |
| 12.9 | `barcode remove Kurti` | Code removed |

## 13. Expenses, profit, reports
| # | Send | Expect |
|---|---|---|
| 13.1 | `expense 500 packaging` | Saved, category packaging |
| 13.2 | `kharcha 800 petrol` | Saved |
| 13.3 | `undo` | Last expense removed |
| 13.4 | `expenses` / `expenses today` | List |
| 13.5 | `monthly net` | Sales − expenses, top categories |
| 13.6 | `profit` / `profit today` / `munafa` | Sales − cost; orders without cost are counted and excluded |
| 13.7 | `today's summary` | Orders, sales, returned count |
| 13.8 | `weekly summary` | Week summary |
| 13.9 | `trending products` / `slow movers` | Lists (+ optional AI insight line 🔑) |
| 13.10 | `Lawn Suit ka report` | Product report |
| 13.11 | `export` | .xlsx with Orders, Order Items, Customers, Catalog, Discounts, Loyalty Rules, **Guide** tab |
| 13.12 | `export orders 30 days` | Orders for last 30 days |

## 14. Menu, shortcuts, help, language
| # | Send | Expect |
|---|---|---|
| 14.1 | `menu` | 8 categories incl. **🏪 Counter & dukaan** |
| 14.2 | Open each category | Rows tappable, each runs a command; ⬅️ Main menu works |
| 14.3 | `menu shop` | walkin, drawer, payouts, labels, scanner, printer, receipt settings, profit, stock |
| 14.4 | `help` | Core list |
| 14.5 | `kya karun` | Context tip (also mid-order) |
| 14.6 | `guide` → `Next ➜` | 4-step guide |
| 14.7 | After any plain reply | Quick bar `Guide / Naya order / Menu` |
| 14.8 | `shortcut off` / `shortcut on` | Bar stops / returns |
| 14.9 | `change language` → English / اردو | Replies switch language |

## 15. Voice notes 🎤🔑 (also type each one to compare)
| # | Say | Expect |
|---|---|---|
| 15.1 | "Sara ka order, do lawn suit, teen hazar paanch sau" | Echo "Maine suna…" then draft |
| 15.2 | "pehla wala" (while a numbered choice is open) | Treated as `1` |
| 15.3 | "haan kar do" (while draft is open) | `yes` |
| 15.4 | "order paanch sau ek cancel kar do" | Asks YES before cancelling (risky) |
| 15.5 | "teen sau nahi, chaar sau" | The later amount is used |
| 15.6 | "ایک کیٹیگری بنائیں جس کا نام Women Cloths ہوگا" | Not a discount |
| 15.7 | Mumbled/short audio | Asks to repeat, no change made |
| 15.8 | 31 voice notes inside one hour | 31st refused (limit 30/hour) |

## 16. Screenshot orders 📷🔑
| # | Send | Expect |
|---|---|---|
| 16.1 | Picture of a customer's order chat | Draft read from the picture, asks missing fields |
| 16.2 | 11 pictures in one hour | 11th refused (limit 10/hour) |

## 17. Import from an old system
| # | Send | Expect |
|---|---|---|
| 17.1 | `import` / `Purana data` | Help with 3 templates |
| 17.2 | `import template all` | .xlsx with Guide tab first |
| 17.3 | Fill 3 customers + 3 products, send the file back as a **document** | Summary: new / already there / bad rows |
| 17.4 | `yes` | Rows saved |
| 17.5 | Send the same file again | All "already there", nothing duplicated |
| 17.6 | `undo` | Imported rows removed |

## 18. Safety and edge cases
| # | Send | Expect |
|---|---|---|
| 18.1 | Mid-draft, send `orders today` | Command runs, draft stays or is dropped sensibly |
| 18.2 | Draft shown, wait 10+ min, send `yes` | Stale draft is **not** saved |
| 18.3 | `cancel 1` then `no` | Nothing cancelled |
| 18.4 | `mark 999 shipped` | "Order not found" |
| 18.5 | `Sara ka phone abc` | Rejected, not saved |
| 18.6 | `Kurti - 0` and `Kurti - -5` | **Known bug (found while writing this plan):** both are saved today — a Rs.0 product, and a product named "Kurti -" at Rs.5. Expected behaviour is to refuse a price ≤ 0. Do not leave such products in the catalog (delete them) |
| 18.7 | `reset account` | Asks YES |
| 18.8 | `yes` after 11 min | Expired, nothing wiped |
| 18.9 | `reset account` then `yes` within 10 min | Everything wiped (orders, customers, products, expenses, drawer, payouts, email/receipt settings), onboarding restarts |
| 18.10 | Send the same WhatsApp message twice (Meta retry) | Handled once |

## 19. Urdu-script spot checks
`آج کے آرڈرز`, `رسید 1`, `گائیڈ`, `مینو`, `📖 گائیڈ` button, `ایکسپورٹ`, `زبان بدلیں` — each behaves like its English command.

---

### Known limits to remember while testing
- The bot never messages buyers; receipts/labels go to the seller's own chat to forward.
- Proactive messages (weekly summary, trial reminder) only reach a seller who wrote in the last 24 h.
- Safepay payment link is a placeholder; subscription "paid" is trusted.
- Thermal receipt, labels and sticker layouts need a real-printer check.
- Timestamps: only orders and expenses keep the original send time for offline-delayed messages.
