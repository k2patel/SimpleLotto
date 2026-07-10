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
