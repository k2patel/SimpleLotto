# SimpleLotto TODO

## Windows Verification

The latest fixes need verification in the intended Windows WinUI environment because this macOS host cannot execute the Windows XAML compiler.

1. Run `..\BuildAndRun.ps1 -SkipRun` from `SimpleLotto.App`.
2. Verify Bins current-shift sales updates after ticket scans.
3. Verify Inventory shows separate unopened receiving and active bin bundle cards.
4. Verify Closing top metric cards switch between history context and live scan context correctly.
5. Generate `closing_report.pdf` and confirm it no longer lists report files.
6. Verify ticket backfill:
   - With current ticket `003`, scanning `007` records sale range `003-007`.
   - Quantity is `5`.
   - Amount is `5 * game price`.
   - Next available ticket advances to `008`.
   - Regular sale rows keep `normal_sale`; closing-generated rows keep `closing_gap_fill_sold`.
7. Verify license workflow:
   - Store > Registration shows the 64-character registration ID before first check.
   - Check License calls the WindowsPOS-compatible license endpoint and updates status, last check, and subscription expiry.
   - Authorized responses remain valid when a later network/server check fails.
   - Expired registration blocks scanner sales, bundle activation, voids, and closing while Settings remains available.
   - Top banner appears for soon-to-expire licenses and during the 7-day grace period.
   - Expired subscription date also locks the software and shows the expired banner.
   - Rdisplay snapshots include the current `license_status`.
