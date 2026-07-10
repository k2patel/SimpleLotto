# SimpleLotto TODO

## Closing, Sales, Bins, Inventory

1. Bins page current-shift sales metric does not update after ticket scans.
   - Recheck the wiring between regular ticket sale recording, `RefreshTotals`, `RefreshBinCards`, and the `BinsShiftSalesText` metric.
   - The card should show sales from the current shift interval since the last successful close.

2. Inventory page needs an unopened bundle total card.
   - Add a top metric card for total unopened/receiving bundles next to the active bin bundle card.
   - Keep unopened inventory separate from active bin bundles.

3. Closing page top metric cards should reset/live-update correctly.
   - On Scan Evidence, top cards should show the current closing scan state, starting from zero where appropriate.
   - Selecting a closing history row may show that selected report's sales, total tickets, bins scanned, and expected cash.
   - Leaving the selected report context should restore live current-closing values.
   - Do not let the last selected closing report keep populating the top cards while doing a new scan.

4. Closing PDF should not list report files.
   - Remove the Report Files section from `closing_report.pdf`.
   - Keep the PDF focused on shift/cash/sales/inventory summary.

5. Ticket sale backfill is incomplete.
   - Applies to regular sales and closing scans.
   - If current ticket is `003` and scanned ticket is `007`, record tickets `003` through `007` as sold and advance next available ticket to `008`.
   - The sale should represent 5 tickets, not only the scanned ticket `007`.
   - Preserve source labels so normal sales and closing-generated sales remain distinguishable in reports/audit.
