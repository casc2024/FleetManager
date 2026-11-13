# Active MaterialIQ model — original Site parity

The active `/materialiq` page now serves `MaterialIQ/index.html`, copied from the original JCK MaterialIQ Site with its eight tabs and business calculations. The only integration changes are the protected FleetManager URL, API addresses and sign-in identity. It does not publish or modify the reference Site.

## Database and persistence

Apply `db/materialiq_modelo_original.sql` inside a single transaction before starting this version. It is already applied to the configured Railway database. It creates `materialiq.model_event` for append-only business events and `materialiq.model_reference_seed` for the imported baseline. Original event IDs make the migration repeatable. All 115 source events were copied, including 21 valued inventory snapshots; the complete baseline also retains 195 imported rate references and 235 price-history entries.

The active UI reads and writes `/api/materialiq/model/events`. Every write requires the existing MaterialIQ policy and server-authenticated user attribution; caller-supplied attribution is replaced. The model uses the same event replay as the reference for rate changes, demand, snapshots, consumption, receipts, purchase edits/voids, policies, supplier choices and catalog changes. Historical source attribution remains unchanged. No reference Site authentication tokens are copied.

The older relational tables and endpoints are retained for compatibility and backup; the active original-model interface uses the event ledger, and future edits in this interface are saved there. They do not automatically update the legacy relational reports. Exports from the active interface include its complete seed and events as JSON and purchases as CSV.

## Validation and starting

The project compiled with zero warnings and errors. `work/model-parity/check-tabs.cjs` checks rendering for all eight tabs and verifies 21 valued rows, confirmed inventory $318,447.00, replacement reference $309,517.25 and purchases $4,537.50. The PostgreSQL adapter was separately checked against every original event ID and payload. These are source/render and database checks; they do not constitute browser interaction tests.

Stop the existing debug session, rebuild and start FleetManager again, then open `/materialiq`. The new controller and adapter require a restart; refreshing an old running executable alone will not load the new backend.

---

## Previous relational implementation notes

# MaterialIQ — ready-mix concrete inventory

This module implements the business model from “Modelo de inventario de concreto” inside FleetManager's existing ASP.NET Core MVC application. Open `/materialiq` with an administrator account or a user granted the MaterialIQ permission. The interface remains in English.

## Install

From the repository root, apply `db/materialiq_esquema.sql` if the schema does not exist, then apply `db/materialiq_modelo_concreto.sql` **before running the updated application**. Both scripts are repeatable. The extension migration preserves inventory, orders and historic rates; it adds assigned suppliers, dated planning choices, optional workbook provenance and business audit triggers. Existing material catalogs and imports are not reloaded.

```powershell
psql "$env:DATABASE_URL" -v ON_ERROR_STOP=1 -f db/materialiq_modelo_concreto.sql
dotnet build FleetManager/FleetManager.csproj
```

The command above requires a PostgreSQL URL accepted by psql. If the application uses a .NET connection string, supply the equivalent psql connection details instead. No production database migration is performed by the application automatically.

## Daily workflow

1. **Administration:** create plants and SKUs and assign materials to plants. Quantities used by this model are tons. CY is the scheduled concrete production volume; multiply it by a material-specific tons/CY dosage to obtain material demand.
2. **Valued inventory:** confirm each opening physical balance and its total USD value. A blank value means unknown. Configure safety stock, receiving capacity, delivery lead time, purchase lot and the assigned supplier used for savings comparisons.
3. **Suppliers & rates:** record product and transportation prices separately, with an effective date and approval status. “Change price” creates a new dated rate and preserves previous rates and recorded purchase costs.
4. **Demand & supply:** enter tomorrow's demand for each plant/SKU. Enter **0** for no planned usage. Blank is pending. Use “Edit 7 days” to complete the week; a blank day is left unchanged. A saved demand overrides the statistical baseline.
5. Review physical-stock coverage, inbound orders due tomorrow, rounded purchase loads, projected closing inventory and capacity/lead-time warnings. The blue table headings remain fixed while scrolling. Dispatch export is blocked until all next-day demand fields in the current plant filter are confirmed.
6. The lowest current delivered rate is suggested automatically. Select another supplier and save a business reason to persist the change for the selected planning cutoff. Unapproved rates remain visibly provisional. Neither a suggestion nor a supplier choice creates a purchase automatically.
7. **Purchases & spending:** record an order with quantity, supplier, historical product/freight prices, expected arrival and ticket. Mark it “Receive” only after actual delivery. Edit, void or restore with a reason. Voided orders remain available via “Show voided orders” but are excluded from spending and stock.
8. Record actual consumption and reconcile against physical balances. A closing balance supersedes movements on or before its date. Correcting an older purchase does not rewrite an already confirmed physical closing balance.
9. Review purchases grouped by day, Monday-based week or month, by plant and SKU, in tons and USD. Charts distinguish product from transportation and can use order dates or receipt dates. Export filtered transactions to CSV.

## Equations and assumptions

| Measure | Calculation |
|---|---|
| Delivered USD/t | Product USD/t + Transportation USD/t |
| Material demand t | Scheduled production CY × dosage t/CY, or directly entered tons |
| Purchase shortfall t | max(0, demand + safety − physical stock − inbound due on the planned date) |
| Purchase loads | ceiling(shortfall / purchase lot) |
| Suggested purchase t | loads × purchase lot |
| Purchase USD | suggested tons × selected delivered USD/t |
| Closing t | opening t + due receipts + suggested purchase − confirmed demand |
| Physical-stock coverage | physical stock / tomorrow's demand; percentage = ratio × 100 |
| Weighted inventory after receipt | prior value + received tons × historical delivered USD/t |
| Average cost USD/t | confirmed inventory value / physical tons |
| Consumption value | actual tons consumed × weighted average cost immediately before consumption |
| Potential savings | max(0, assigned delivered rate − selected delivered rate) × weekly suggested tons |
| Provisional excess t | max(0, physical tons − confirmed seven-day demand − safety) |
| Potential capital release | provisional excess × confirmed average cost |

Only the most recent effective rate **per supplier, plant and SKU** is eligible. Ties on effective date use the newest rate ID. The suggestion then minimizes product plus transportation cost. Rates effective after the cutoff are excluded. Approval status is displayed rather than silently treating an unapproved reference as confirmed.

The weekly simulation starts tomorrow and processes each date in order. Only orders placed by the cutoff and expected on that date enter the projection. Overdue and undated transit orders require their expected dates to be corrected. Pending demand makes that day's purchase and all subsequent balances unknown; the weekly total is shown as pending. The historical baseline averages recorded consumption totals for each corresponding weekday within the last 28 days, requiring at least two observed dates for every weekday. Missing observations are not assumed to be zero.

Capacity is tested on inventory **before consumption**, after due and suggested deliveries. Lead-time alerts compare the configured delivery lead time with the number of days until the required receipt. Warnings require an operational decision; this model does not schedule trucks, confirm vendor capacity or optimize delivery times within a day.

Unknown initial valuation remains unknown until a confirmed balance is recorded or stock is fully consumed. Consumption in excess of physical stock produces a negative quantity and unknown value, visibly requiring reconciliation. It is never silently capped at zero.

## Provenance and history

“Source & calculation” shows the latest closing date, recording user, optional workbook/sheet/cell metadata, and the reconstruction rule. Imported balances whose original import did not store cell metadata are labelled **legacy imports**. The program does not invent a cell reference. A manual balance correction clears previous workbook attribution.

New business changes are captured transactionally by database triggers: user, timestamp, field, before/after values, reason and available plant/SKU/supplier/ticket context. Existing history is preserved. The History page filters by user and movement type and displays the latest 1,000 matching changes for its date range. It contains business records rather than technical JSON or IP details.

## Verification

`tests/InventoryChecks.csproj` is a small executable scenario suite without an additional testing package. From the repository root:

```powershell
dotnet run --project tests/InventoryChecks.csproj
```

The optional `--database` checks are explicitly isolated: they connect only to a disposable PostgreSQL server at `127.0.0.1:55449`, user `inventory_test`, and create a new database named `inventory_model_test_<process ID>`. They never read production connection settings. They cover repeatable migration, rates, supplier overrides, pending demand, CY dosage, receipts, weighted consumption, void/restore, charts and audit context.

## Repair an incomplete legacy quantity import

`db/materialiq_completar_saldos.sql` completes the existing 2026 Raw quantity data in one transaction. It maps Cement to Bulk Cement, adds missing distinct gravel/rock/sand SKUs and assigns only the imported plant/material pairs. Existing balances are preserved with `ON CONFLICT DO NOTHING`. It does not assign monetary valuations, create supplier rates or reconstruct purchase orders. Run using `psql --single-transaction -v ON_ERROR_STOP=1 -f db/materialiq_completar_saldos.sql`.

When rates are absent, the overview displays physical tons by plant instead of an empty cost chart. Missing confirmed valuations and rates are shown as pending; a missing weekly purchase is not displayed as zero.
