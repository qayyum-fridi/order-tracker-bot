# Free-form order pipeline — QA suite coverage audit (2026-11-27)

This checks the 60-case QA suite (TC01–TC60, E2E-01..05) against the code and tests on `main` (`e421525`).
I found the evidence by searching the code. **I did not run anything for this audit.** "Covered" means a test that exercises
the behaviour exists. It does not mean the case was run with the suite's sample catalog (Sugar/Rice/Oil/Milk/Tea).

Legend: **C** covered by a test · **P** partly covered (the code exists, but no test matches the case, or only the AI path handles it) ·
**G** gap: the code does not do it, or does something risky · **AI** needs a live model to verify (the `OTB_*` evals)

## Findings worth acting on first

| # | Severity | Finding | Where |
|---|----------|---------|-------|
| 1 | High | **No oversell guard in chat orders.** Saving subtracts stock (`StockFootprint` / `ApplyStockChangeAsync`) but never checks it first, so `StockQty` can go negative. TC34, TC48 and E2E stock checks fail as written. | `ConversationEngine.Commands*.cs`; no stock-short template in `Templates/Messages/stock.json` |
| 2 | High | **A zero or negative quantity from extraction is silently turned into 1.** `var quantity = item.Quantity <= 0 ? 1 : item.Quantity;` A wrong AI quantity therefore becomes a valid-looking 1 instead of being rejected (TC33, TC56). The seller still sees it on the confirmation, so it is not saved without a YES. `ActionSchema` rejects `<= 0`, but only on the model-first path. | `ConversationEngine.OrderFlow.cs:296`; compare `ActionSchema.cs:73` |
| 3 | Medium | **No upper quantity limit on the order draft path.** `WalkIn` and `CounterReturn` cap at 999; the free-form order draft does not (TC34). | `ConversationEngine.OrderFlow.cs` |
| 4 | Medium | **Two customers with the same name:** `ReferenceResolver.ResolveCustomerAsync` is used for guided taps (`Guided.cs:73`). I found no test where two saved customers share a name inside the free-form order flow (TC23). | `ConversationEngine.Guided.cs`, `ReferenceResolver` |
| 5 | Low | **Unit-less quantity** (`Ali ko 2 sugar`, TC06) and **unit conversion** (kg vs. a pack-priced product): the domain has `UnitType`/`UnitQty` and `ProductPackTests`/`ProductUnitTests`, but nothing clarifies when a unit is missing. | `ProductUnitTests.cs`, `UnitSuggestionTests.cs` |

## Case-by-case

### Core (TC01–10)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC01–03 | C/AI | Single-customer drafts: `ConversationEngineTests`, `ModelFirstFreeFormTests`, fixture `free-form-cases.json` (15 `create_order` cases). Groceries in kg are not in the fixtures (they are clothing-heavy). |
| TC04–05 | C | Multi-item lines: `ConversationEngineTests`, `ConversationPipelineTests`. |
| TC06 | G | No missing-unit clarification (Finding 5). |
| TC07 | C | Aliases: `ProductDetailAliasTests`; `chini/cheeni` appears in `ProductPackTests`, `IndustryProfileTests`. |
| TC08 | C | Variant / ambiguous match asks; it never guesses (`AmbiguousVariants`, `VariantMatchTests`, `ProductChoiceTests`). |
| TC09 | C | Unknown product → "nahi mila" with suggestions: `DidYouMeanTests`, `MissChoiceTests`. |
| TC10 | C | `ConfirmButtonTests.TapCancelOrEdit_DropsTheDraft_NothingSaved`; Safety gateway YES for cancel (`edf76dd`). |

### Language (TC11–18)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC11–12 | AI | Extraction quality: `TurnUnderstandingEval`, `PakistaniCorpusTests` (opt-in live evals). |
| TC13 | P | Delivery date words appear in `CasualPhrasingTests`, `CommandParserTests`; I found no order-draft test that keeps "kal". |
| TC14 | P | Mixed-unit item with a missing quantity: same gap as TC06. |
| TC15–16 | C | `BackReferenceOrderTests`, `UsualItemsTests`, `ReferenceResolverTests`. |
| TC17 | P/AI | Corrections: `ConversationEngineTests` (correction), `OrderEditSpeech`. "2 nahi 5" inside a new order is left to the AI. |
| TC18 | C | Draft edit before save: `DraftConfirmationTests`, `EditModeTrapTests`. |

### Customer (TC19–26)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC19 | C | Existing-customer reuse: `ConversationEngineTests`, `SharedCustomerEditTests`. |
| TC20–21 | C | Missing phone prompt and `PhoneLater` (`OrderFlow.cs:227,544,1134`). |
| TC22 | C | A paid-in-full or counter sale skips the phone question (`OrderFlow.cs:544`). |
| TC23 | P | Finding 4. |
| TC24 | P | `IsPlausiblePhone` is checked on the edit, agent and media paths; check that the order-draft phone reply rejects bad numbers (`GuidedQuestionTests` has one invalid-phone case). |
| TC25 | P | PhoneLater then a typed phone: the flow exists; I found no test asserting there is no duplicate customer. |
| TC26 | C | `DidYouMeanTests`, `ReferenceResolverTests`. |

### Money (TC27–35)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC27–28 | C | Totals come from the catalog price × quantity in code (`OrderFlow.cs:401`); `OrderMoneyTests`. |
| TC29 | C | Written price kept as `WrittenPrice` / `StatedUnitPrice` and flagged against the catalog: `SpokenPriceTests`, `ModelAmountQuoteTests`. |
| TC30–31 | C | `AdvancePaid`, `DeliveryCharge` on the draft; `OrderMoneyTests`, `SupplierCourierFeeTests`. |
| TC32 | P | "free" lines: `AdversarialInputTests`. I found no test that "free de do" cannot zero the price without a discount. |
| TC33 | **G** | Finding 2. |
| TC34 | **G** | Findings 1 and 3. |
| TC35 | P | Quantities are decimal in places; a decimal quantity for piece-sold products is not rejected. |

### Multi-customer (TC36–42)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC36, 40, 42 | C | `MultiBuyerOrderTests.TwoBuyersInOneMessage_…SavedSeparately`. |
| TC37 | P | The test uses two buyers; three should work, but no test covers it. |
| TC38 | P | Missing phone for each buyer separately: no test. |
| TC39 | G? | Confirming one buyer and cancelling the other: not found; the confirmation looks all-or-nothing. Verify. |
| TC41 | C | `MultiBuyerOrderTests.TheFirstBuyersOrderHasAnUnknownProduct_…`. |

### Database integrity (TC43–50)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC43 | C | `ConfirmButtonTests.DoubleTapOrRepeatedSave_CreatesOnlyOneOrder`; version tokens (`e5ed053`). |
| TC44–45 | C | `WebhookInboxTests`, `WebhookAdversarialTests` (message id is the primary key, so a retry is a duplicate). |
| TC46–47 | C | `SaveTransactionTests` (rollback, multi-order rollback). |
| TC48 | **G** | Finding 1. The per-sender lock serializes one seller, but there is no stock check at all. |
| TC49 | C | Undo restores stock: `ConversationEngineTests` (undo) plus `StockFootprint` delta. |
| TC50 | C | `ConfirmButtonTests.TapCancelOrEdit_…NothingSaved`. |

### AI failure (TC51–60)
| ID | Status | Evidence / note |
|----|--------|-----------------|
| TC51, 58 | C | `OpenAiOrderAssistant` retries once on a dropped connection, 429 or 5xx, then degrades; `OpenAiOrderAssistantTests`. |
| TC52–54 | C | `JsonException` caught on every parse; strict schema; `OpenAiOrderAssistantTests`, `ActionSchemaTests`. |
| TC55 | C | Products are matched against the catalog in code; an unmatched name becomes a "nahi mila" choice. |
| TC56 | **G** | Finding 2 (the order-draft path turns it into 1). |
| TC57 | C | `ProductChoiceTests`, `CandidateStageTests`, `ScoredChoiceTests`. |
| TC59 | C | `UsageLimitTests`, `FreeSellerAndMediaLimitTests` (USD per hour). |
| TC60 | P | Pending actions persisted (Stage 6) and `PendingWebhookMessages` replay; I found no explicit "worker killed mid-save" test. |

### E2E
| ID | Status | Note |
|----|--------|------|
| E2E-01 | C (minus stock 100→98 check on the grocery catalog) | |
| E2E-02 | C | |
| E2E-03 | C | "brown sugar" → unknown / did-you-mean, never silently mapped (assuming no fuzzy over-match; add a case). |
| E2E-04 | C | |
| E2E-05 | P | Separation is tested; the per-buyer stock change in multi-buyer is not asserted. |

## Summary
- **Covered:** about 36 · **Partial:** about 18 · **Gap:** 4 hard gaps (TC33, TC34, TC48, TC56; TC06 is a softer gap), plus TC39 to verify.
- Suggested next step: fix Findings 1–3 (stock guard at draft/confirm time, reject `<= 0` and over-limit quantities in the order draft),
  then add a grocery-catalog fixture (`kg`/`litre`) to `free-form-cases.json` so TC01–14 run against the eval.
