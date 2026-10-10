# Step 1: Losses and wages (owner phone only)

Status: draft. No code yet. `dotnet` is not installed in the current session, so nothing here is compiled or tested.

## Scope

- Owner phone only. No `Staff` table and no multi-phone auth (that is step 2).
- Deterministic commands only. No AI intents.
- Undoable, following the `Expense` pattern.

## Schema

| Table | Columns | Index |
|---|---|---|
| `Losses` | `Id`, `SellerId`, `ProductId` (int?, FK Product, null for free text), `ProductName` (snapshot), `Quantity` (int), `UnitCost` (decimal, copied from `Product.CostPrice` at log time), `Reason` (string?), `CreatedAt` | `(SellerId, CreatedAt)` |
| `WageEntries` | `Id`, `SellerId`, `WorkerName` (string), `Amount` (decimal), `Days` (int?), `Note` (string?), `CreatedAt` | `(SellerId, CreatedAt)` |

- `ActionType`: add `LossLogged`, `WageLogged`.
- Sqlite: add both tables in `SqliteSchemaPatcher`.
- SQL Server: generate a migration (`dotnet ef migrations add AddLossesWages ...`, per CLAUDE.md).
- `reset account`: delete both tables' rows for the seller.
- `UnitCost` is stored, not looked up later, so profit history does not change when cost prices change.

## Commands

| Kind | Examples | Notes |
|---|---|---|
| `Loss` | `loss 2 Kurti damaged`, `nuqsan 2 Kurti`, `damage 1 Lawn Suit` | Product matched by catalog (fuzzy match via the existing path). Unmatched name: save as free text with `UnitCost` 0 and flag it. |
| `Wage` | `Ali ki 5000 dihari`, `wage Ali 5000`, `salary Ali 5000`, `Ali ko 3 din 1500` | Daily rate × days when `dihari`/`din` is given, otherwise a flat amount. |
| `LossList` / `WageList` | `losses [today\|last month]`, `wages [...]` | Same period handling as `ExpensePeriod`. |

Gotcha: `Ali ki 5000 dihari` is close to the `CustomerUpdate` form (`Bilal ki city …`). Add the wage check before `CustomerUpdate` and require a numeric amount, so customer corrections are not caught.

## Money and stock

- Loss: reduce `StockQty` by `Quantity` when the product tracks stock. Call the existing stock footprint logic; do not hand-adjust. Undo restores it. Collect low-stock warnings via `FlushStockWarningsAsync`.
- Loss valuation for P&L: `Quantity × UnitCost`, shown as its own line. It is not counted as a sale.
- Wage: outside sales. Does not touch stock.

## Reports (step 1)

- `monthly net` = sales − expenses − wages, with a losses line shown separately.
- `profit` is unchanged, except that losses are shown as a separate line.

## Open decisions (need your answer before code)

1. Loss P&L: valued at cost (proposed) or at sale price?
2. Should `monthly net` subtract wages (proposed: yes), or should wages stay a separate report?
3. Is `Days` mandatory for daily wages, or is a flat amount enough?

## Tests (when the build is available)

- Parser: each loss and wage form, plus the `CustomerUpdate` collision cases.
- Handler: loss reduces stock; undo restores it; wage saved and undone.
- Net: sales − expenses − wages; losses excluded from sales.
- Sqlite patcher: tables created on an existing DB.
- Existing `CommandParser` tests still pass (no regressions in order and expense parsing).

## Not in step 1

- Staff table, multi-phone auth, per-seller lock (step 2, see the concurrency gap in the earlier review: lanes are keyed by sender, not seller).
