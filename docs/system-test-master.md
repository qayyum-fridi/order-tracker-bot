# Overall system test — master run sheet

**One file to fill:** `docs/test-run/system-test-master.csv` — 1,307 cases, columns `ID · Suite · Section · Send · Expect · Needs · Result · Actual/notes`.
Open it in Excel / Google Sheets, fill **Result** with `PASS`, `FAIL`, `SKIP` (not applicable) or `BLOCKED` (could not reach the step), paste the bot's actual reply in the last column for every FAIL, and send the file back. I turn that into the report.

## Suites and order (run in this order — later suites assume earlier state)
| # | Suite (ID prefix) | Cases | What it covers |
|---|---|---|---|
| 1 | Changes made today (`TODAY-N..`) | 17 | Shop-type list: typed / tapped / spoken pick, acknowledgement, unclear voice re-asks the list |
| 2 | Full business day (`MTP-`) | 162 | Onboarding, catalog, stock, customers, orders, payments, reports, undo, receipts |
| 3 | UX round (`UXR-`) | 76 | `limits`, `rozana`, quick bar, tap-to-confirm, voice best guess, picture tips, price-list photo, earned trust |
| 4 | Real-world scenario (`RWS-`) | 69 | One full shop day end to end |
| 5 | Complex orders (`CO1-`, `CO2-`) | 88 | Multi-order, bulk status, tricky wording |
| 6 | Per-industry (`IND-`) | 512 | 7 shop types: setup, catalog, orders, status/payment, reports in Roman Urdu / English / Urdu, data check, voice |
| 7 | Live list (`LIVE-T`) | 383 | Command-by-command regression from real chats |

## Before you start
- Build running with the branch `claude/test-cases-writing-oaysj1` (it carries today's two voice/onboarding fixes — without it `TODAY-N09..N14` will fail).
- Use a **fresh WhatsApp number per suite** (or `reset account` + `yes`). Suites 2–5 use one shop ("Ayesha Collections"); suite 6 needs one fresh account **per industry**.
- `Needs` column: `AI key` = `OpenAi:ApiKey` set; `voice` = send a voice note; `picture` = send a photo; `email` = `MerchantEmail:*` set. Without the key, mark those rows `SKIP`, not FAIL.
- Flags for specific rows: `Features:StrategyLearning` on (UXR-9.x), `MediaLimits:ImagePerHour=0` + restart (UXR-1.7).
- A tap on a button sends its label; typing the command always works too.

## Reading results
- Expectation wording is from the docs/code and was **not** re-verified by running the bot for the older suites; some `Expect` cells may be stale. If the bot's behaviour looks right but differs from the row, mark `FAIL` and write "expect outdated?" in notes.
- Known not-covered: Instagram comments, broadcasts via Meta templates, SMS, Safepay, backups to Drive, SQL Server migrations (not manual-test items).

## Automated check (run first if a dev machine is available)
```bash
dotnet test
```
Report any red test by name; this session could not run it (`dotnet` not installed).
