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
   - During first-time setup initial scan, scanning ticket `001` records opening inventory only, keeps current ticket `001`, and does not create a sale or gap-fill.
   - After setup completes, regular scanning of any ticket serial in a new bundle, then bin, then missing game price/name records activation gap-fill from the bundle's first ticket through the scanned ticket; for example `001` records `000-001`, quantity `2`, next `002`, and `008` records `000-008`, quantity `9`, next `009`.
   - With active current ticket `002`, scanning `003` records sale range `002-003`, quantity `2`, amount `2 * game price`, and next available ticket `004`.
   - If a placed bundle has no positive game price, scanning a ticket must require game price setup and must not record a `$0.00` sale.
   - Scanner input, rejected scans, ticket sales, bundle activations, opening placements, and bin placements appear in Audit.
7. Verify license workflow:
   - Store > Registration shows the 64-character registration ID before first check.
   - Check License calls the WindowsPOS-compatible license endpoint and updates status, last check, and subscription expiry.
   - Authorized responses remain valid when a later network/server check fails.
   - Expired registration blocks scanner sales, bundle activation, voids, and closing while Settings remains available.
   - Top banner appears for soon-to-expire licenses and during the 7-day grace period.
   - Expired subscription date also locks the software and shows the expired banner.
   - Rdisplay snapshots include the current `license_status`.
8. Verify automatic app upgrade checks:
   - Startup on first-week Monday runs a non-blocking app upgrade check once for that local date.
   - Startup on second-week Tuesday, third-week Wednesday, and fourth-week Thursday follows the same once-per-date rule.
   - Startup on non-scheduled dates does not check automatically.
   - No hourly or recurring background upgrade timer runs after startup.
   - Manual Check for Upgrade still works from Settings.
9. Verify application lifetime power behavior:
   - While SimpleLotto is running, Windows does not enter idle sleep.
   - The sleep-prevention request remains active when SimpleLotto is minimized to the tray.
   - The sleep-prevention request is released after using the tray Exit command or otherwise exiting the app.

## Missing Follow-Up Work

1. License expiry lifecycle needs to be aligned with the final rule:
   - Renew-soon banner starts 7 days before signed `expires_at`.
   - Signed `expires_at` date starts a 7-day grace period while the app remains usable.
   - Full operational lock starts after `expires_at + 7 days`, not immediately on `expires_at`.
   - Automatic license refresh should run non-blocking at startup and recovery points such as login/license-sensitive actions when near expiry, in grace, or locked.
2. Rdisplay expiry/grace banner is not implemented:
   - Current display payload only sends `license_status`.
   - Rdisplay client/renderer must be extended with banner text, expiry/grace fields, and daily opacity changes before transparent display banners can work.
