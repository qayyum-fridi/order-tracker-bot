# Core value roadmap — "WhatsApp par bol kar order, baqi bot sambhale"

Sellable promise: voice/text order in Roman Urdu -> saved order -> status/COD tracking -> receipt/label. No app, no form.
Each stage has a pass/fail check. Do them in order; stop at the first failure. Rows marked 🔑 need `OpenAi:ApiKey`.

## Stage 0 — Does it build and pass? (automated, 10 min)

| # | Command | Pass |
|---|---|---|
| 0.1 | `dotnet build` | 0 errors |
| 0.2 | `dotnet test` | all green |
| 0.3 | `dotnet test --filter "FullyQualifiedName~ConversationEngineTests\|DraftConfirmationTests\|PayoutTests\|LabelScanDrawerTests\|ClientApiTests"` | green (core flow only) |
| 0.4 | `cd src/OrderTrackerBot.Api && dotnet run` then `GET /health` | 200 |

## Stage 1 — Order capture by text (no AI needed, local curl)

Use the webhook curl from CLAUDE.md. Send each as its own message.

| # | Send | Expect |
|---|---|---|
| 1.1 | `start` | onboarding begins |
| 1.2 | shop name -> kind of shop -> `Kurti - 1800` | product saved |
| 1.3 | `Ayesha 03001234567 Kurti 2 Lahore` | draft with Save / Edit / Cancel |
| 1.4 | `yes` | order saved, id shown |
| 1.5 | `orders today` | the order listed |
| 1.6 | `Ayesha ka order` | that order shown |

## Stage 2 — Order capture by voice / free text 🔑 (the actual selling point)

| # | Do | Pass |
|---|---|---|
| 2.1 | Real voice note (Urdu/Roman Urdu): customer, phone, product, quantity | bot echoes "Maine suna", then a correct draft |
| 2.2 | Same order, 3 different phrasings, noisy room | amounts and phone exactly right (a wrong amount must be refused, never saved) |
| 2.3 | "haan kar do" / "pehla wala" by voice mid-draft | treated as yes / 1 |
| 2.4 | Free text with typo: `Kurtti 2 Ayesha 0300…` | did-you-mean buttons or correct match |
| 2.5 | Price differs from catalog | price-guard question appears |
| 2.6 | Screenshot of a customer chat | order extracted |

**This stage decides if the product sells. Test with 10+ real notes from a real seller.**

## Stage 3 — Status, COD, money

| # | Send | Expect |
|---|---|---|
| 3.1 | `mark 1 shipped` | status changes, label offered |
| 3.2 | `mark 1 delivered` | asks "Cash collect ho gaya?" for COD |
| 3.3 | `order 1 advance 500` | balance shown |
| 3.4 | `mark 1 returned` | excluded from sales |
| 3.5 | `payout pending` / `payout 11700 TCS orders 1 2 3 fee 300` | Barabar / KAM / ZYADA message |
| 3.6 | `undo` after any of the above | restored |

## Stage 4 — Receipt and label

| # | Send | Pass |
|---|---|---|
| 4.1 | `receipt 1` | PDF arrives, amounts correct, Urdu name renders |
| 4.2 | `label 1` | parcel label PDF, COD amount correct |
| 4.3 | `receipt size 80mm` then print a real one | layout OK (thermal was never rendered by the author) |

## Stage 5 — Real WhatsApp (needs Meta setup)

| # | Check | Pass |
|---|---|---|
| 5.1 | Webhook verifies in Meta dashboard | verified |
| 5.2 | Message from your own phone reaches the bot | reply arrives |
| 5.3 | Same message redelivered | not processed twice |
| 5.4 | Restart service mid-day | no lost / doubled order |
| 5.5 | Bad signature POST | rejected |

## Stage 6 — Dashboard <-> bot

| # | Check | Pass |
|---|---|---|
| 6.1 | Order made in bot appears in dashboard (`GET api/client/dashboard`) | yes |
| 6.2 | `PUT api/client/orders/{id}/status` | bot `order N` shows new status |
| 6.3 | Real-time push (SignalR) | **not built yet** — dashboard needs refresh |

## Stage 7 — Pilot (3–5 real sellers, 1 week)

Track daily: orders entered per seller, voice notes that needed a retype, wrong drafts, sellers who stopped.
Go / no-go: at least 3 sellers still entering orders on day 5 without being reminded.

## What I could NOT verify

The `dotnet` SDK is not installed in this cloud container and the proxy blocked the installer, so Stages 0–1 have not been executed by me. Run Stage 0 on your machine first.
