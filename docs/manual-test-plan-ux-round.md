# Manual test plan — UX round (limits, rozana, tap-to-confirm, picture tips, price-list photo, voice best guess)

Covers the last 8 commits (`692d809` … `e31c406`). Send from the **seller's WhatsApp**, in order. Tick each row.
🔑 = needs `OpenAi:ApiKey`. 🎤 = voice note. 📷 = picture. Setup: fresh number or `reset account` + `yes`; finish onboarding with `Kurti - 1800` and `done`.
Defaults assumed: text 150/h, voice 30/h, picture 15/h, voice max 5 min, 5 pictures at once, 10 min to label a picture.

## 1. `limits`
| # | Send | Expect |
|---|---|---|
| 1.1 | `limits` | "Bot kitna kaam leta hai": Voice note 5 minute tak · Voice notes 30/ghante · Tasveerein 15/ghante (ek saath 5 tak, type batane ke liye 10 minute) · Messages 150/ghante |
| 1.2 | `hadood` | Same as 1.1 |
| 1.3 | `meri limits` | Same as 1.1 |
| 1.4 | `حدود` | Same as 1.1 |
| 1.5 | `limit` (singular) | Same as 1.1 |
| 1.6 | `limits kya hain aaj` | NOT the limits reply (not an exact command; falls to AI / clarification) |
| 1.7 | Set `MediaLimits:ImagePerHour=0`, restart, `limits` | Pictures line says "koi hadd nahi" |
| 1.8 | During an order draft (`Sara: Kurti 1, 03001112222`), send `limits` | Limits reply; draft still waiting afterwards (send `cancel` to clean up) |

## 2. `rozana`
| # | Send | Expect |
|---|---|---|
| 2.1 | `rozana` | "Roz ke kaam — chunein" + 6 taps: Naya order · Aaj ka hisaab · Kharcha likhna · Tankhwah dena · Stock dekhna · Menu; tip line about cost + stock + "data check" |
| 2.2 | `roz ka kaam` | Same as 2.1 |
| 2.3 | `daily work` | Same as 2.1 |
| 2.4 | `روزانہ` | Same as 2.1 |
| 2.5 | Tap **Aaj ka hisaab** | Today's summary (same as typing `aaj ka hisaab`) |
| 2.6 | Tap **Kharcha likhna** | Expenses list / how-to (same as `expenses`) |
| 2.7 | Tap **Stock dekhna** | Stock list |
| 2.8 | Tap **Menu** | Main Menu list |
| 2.9 | Tap **Naya order** | New-order help |

## 3. Quick bar (shortcut buttons)
| # | Send | Expect |
|---|---|---|
| 3.1 | `catalog` (idle) | Reply + 3 buttons; **first is `📋 Rozana`** (not Guide), middle = next-step, last `📋 Menu` |
| 3.2 | Tap `📋 Rozana` | Same as 2.1 |
| 3.3 | `language english`, then `catalog` | First button `📋 Rozana`, text in English |
| 3.4 | Language Urdu script, then `catalog` | First button `📋 روزانہ`, last `📋 مینو` |
| 3.5 | `shortcut off`, then `catalog` | No quick bar. `shortcut on` brings it back |
| 3.6 | `Lawn Suit - 3500, cost 2200` | Saved; bar offers `walkin Lawn Suit 1` / `catalog` / Menu |

## 4. End of setup tip
| # | Send | Expect |
|---|---|---|
| 4.1 | New number: `start` → Roman Urdu → Setup → shop name → `Kurti - 1800` → `done` | "Setup mukammal" message now also mentions `rozana`, `limits` and help |

## 5. Tap-to-confirm buttons (voice / picture steps)
Needs 🔑 + 🎤/📷. The "yes" button names the fact being approved.
| # | Send | Expect |
|---|---|---|
| 5.1 🎤 | Say "pachas rupay packaging ka kharcha likho" (risky-free path may run directly; if parked:) | Echo "Samjha: expense 50 packaging" with buttons `✅ Rs.50 likho` / `❌ Nahi` |
| 5.2 | Tap `✅ Rs.50 likho` | Expense saved (`expenses today` shows 50 packaging) |
| 5.3 🎤 | Say "order barah cancel kar do" (order 12 must exist) | Parked; buttons `✅ Order #12 cancel` / `❌ Nahi` |
| 5.4 | Tap `❌ Nahi` | "Theek hai, kuch nahi kiya"; order 12 untouched |
| 5.5 🎤 | Say two things: "kharcha 300 petrol aur wages Ali 5000" | One YES for both; button `✅ 2 kaam karo` |
| 5.6 | Type `yes` instead of tapping | Also runs (typed YES always works) |
| 5.7 | Park something, wait > 10 min, tap yes | "Purana ho gaya / dobara bhejein"; nothing runs |
| 5.8 | Park an expense, then type a different real command (`stock`) | Parked step dropped, `stock` runs |
| 5.9 | Park `cancel order 12` by voice, change order 12 status meanwhile (`mark 12 shipped`), tap yes | Refuses as stale; not cancelled |
| 5.10 | Button label > 20 chars (e.g. expense `123456789` Rs) | Falls back to `✅ Haan, karo` |

## 6. Voice: one-tap best guess (instead of "say again")
| # | Send | Expect |
|---|---|---|
| 6.1 🎤 | Say an amount the speech model may write differently ("teen sau pachas ka kharcha petrol") | If amounts don't match: "Mujhe pakka nahi ke maine aap ki baat sahi suni… upar Samjha dekh kar tasdeeq karein" + yes/no buttons — NOT "dobara bolein" |
| 6.2 🎤 | Say "teen nahi, do kharcha packaging" (seller takes back a value) | Plain "say it again" (no proposal button) |
| 6.3 🎤 | Say "shayad order 12 cancel kar dun" | Refused (risky + hesitant); no cancel button offered |
| 6.4 🎤 | Say a risky step with an unclear number ("order do sau cancel") | Refused with "rakam awaaz se match nahi hui…", nothing changes |
| 6.5 🎤 | Voice note shorter than ~1 s | "Awaaz bahut chhoti" refusal |
| 6.6 🎤 | Voice note > 5 min | "Bahut lambi" refusal |
| 6.7 🎤 | Send the same voice note twice | Second: duplicate-note message, nothing run twice |
| 6.8 | `GET /api/admin/reports/voice-stats` (X-Admin-Api-Key) after 6.1–6.7 | Counts for `note_transcribed`, `rewrite_called`, `refused_too_short`, `refused_too_long`, `duplicate_note`, `decision_*`; no transcripts |

## 7. Pictures: first-time tip and labels
| # | Send | Expect |
|---|---|---|
| 7.1 📷 | Send any picture, **no caption** (first ever) | Clear-photo tip ("roshni mein, seedhi aur qareeb se… caption: kharcha · tankhwah · customer · products · order"), then "Yeh tasveer kis cheez ki hai?" with 6 taps incl. **Products ki list** |
| 7.2 📷 | Send a second unlabelled picture | No tip again (once only), only the question |
| 7.3 | Tap `Kuch nahi` | "Theek hai, tasveer se kuch save nahi kiya" |
| 7.4 📷 | Picture with caption `kharcha` (bill) | Reading shown + yes button naming the amount; no photo tip (labelled) |
| 7.5 📷 | Picture with caption `wages` | `✅ Rs.<n> dein` button |
| 7.6 📷 | Picture with caption `customer` | `✅ Customer save` button |
| 7.7 📷 | Unreadable/blurry picture, caption `kharcha` | "Saaf maloomat nahi parhi…" example line; nothing saved |
| 7.8 📷 | Draft order open, then send picture | "Pehle mojooda kaam mukammal karein ya cancel likhein" |
| 7.9 📷 | Send 6 pictures quickly | Max 5 kept at once (see `limits`) |

## 8. Price-list photo → products
| # | Send | Expect |
|---|---|---|
| 8.1 📷 | Photo of a handwritten/printed price list (3+ items, with prices), caption `products` | "Tasveer ko products samjha. Maine yeh parha:" one `Name - price` line per item; button `✅ N products`; cost/stock tip line below |
| 8.2 | Tap `✅ N products` | All saved (`catalog` lists them); confirmation echoes |
| 8.3 📷 | Same list, caption `pricelist` / `catalog` / `saman` / `ratelist` / `maal` / `پروڈکٹس` | Each treated as products |
| 8.4 📷 | List with costs shown (price + cost columns) | Lines read `Name - 500, cost 300` |
| 8.5 📷 | Price list with one unreadable row | Others read; "1 tasveer mein se… saaf nahi" note, the bad row skipped |
| 8.6 📷 | One-item price list | Button `✅ Product save` |
| 8.7 📷 | List of 40 items | At most 30 taken |
| 8.8 📷 | Photo with no prices at all, caption `products` | "Saaf maloomat nahi parhi", nothing saved |
| 8.9 | Send `❌ Nahi` instead | Nothing saved |
| 8.10 📷 | Re-send the same list after 8.2 | Products already there are reported as "pehle se catalog mein" / no price overwrite |
| 8.11 | Unlabelled photo → tap `Products ki list` | Same flow as 8.1 |

## 9. Earned trust: small picture expenses (needs `Features:StrategyLearning` on)
Threshold = 16 identical "yes" answers in a row, none corrected.
| # | Send | Expect |
|---|---|---|
| 9.1 📷 | 1st–15th expense photo (caption `kharcha`, amount ≤ 20,000), tap yes every time | Always asks (button each time) |
| 9.2 📷 | 16th expense photo, tap yes | Still asks (sixteen answers needed *before* it skips) |
| 9.3 📷 | 17th expense photo | Saved **without asking** + "Aap har baar tasveer ke kharche ko haan kehte hain… galat ho to undo" |
| 9.4 | `undo` right after | Expense removed; trust retracted |
| 9.5 📷 | Next expense photo | Asks again (correction brought the question back) |
| 9.6 📷 | Expense photo with amount > 20,000 (after trust earned) | Always asks |
| 9.7 📷 | Wages / customer photos after trust earned | Always ask (trust is for expenses only) |
| 9.8 📷 | Tap `❌ Nahi` on any expense photo | Counted as refusal; next one asks |
| 9.9 | `Features:StrategyLearning=false` | Never skips the question, whatever the history |

## 10. Regression
| # | Send | Expect |
|---|---|---|
| 10.1 | `help` / `menu` | Unchanged lists |
| 10.2 | `yes` with nothing waiting | Normal "kuch intezar mein nahi" style reply, no crash |
| 10.3 | Order draft `Sara: Kurti 1, 03001112222` → Save/Edit/Cancel buttons → tap Save | Order saved |
| 10.4 | `version` | Build info |
| 10.5 | `kya karun` | Guidance for current step (unchanged) |

## 11. Automated check before manual run
```bash
dotnet test --filter "FullyQualifiedName~MockupFeatureTests|FullyQualifiedName~TurnDecisionTests|FullyQualifiedName~CommandDictionaryTests|FullyQualifiedName~MessageTranslationsTests|FullyQualifiedName~TemplateGuardTests"
```
