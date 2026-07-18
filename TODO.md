# SimpleLotto TODO

## Windows Verification

The latest fixes need verification in the intended Windows WinUI environment because this macOS host cannot execute the Windows XAML compiler.

1. Verify the Windows package through `.github/workflows/build-windows.yml`; local Windows/PowerShell verification is not part of the current development workflow.
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
   - Verify bundle-range completion: a `$20` game with a `$300` bundle and first ticket `000` permits `000`-`014`; scanning `014` records the final sale, keeps the bundle in the bin/Rdisplay as grey `Sold out`, and never creates ticket `015`. Verify `$50` games default to a `$900` bundle but their saved bundle price remains editable.
   - Verify ledger integrity: scan ticket `003` twice and confirm only the first scan records revenue; repeat in a closing backfill. Void a sale twice and confirm the second attempt is rejected, including after restarting the app.
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
10. Verify dedicated inventory receiving:
   - `Scan New Inventory` is visible in the top bar only while Inventory is selected, is also available inside the Receiving tab, opens the focused receiving overlay, and normal sales/activation scanner routing does not run underneath it.
   - Scanning any valid ticket barcode records only Game ID + Bundle ID; the ticket serial is not stored.
   - Scanning the same staged, already-received, or already-active bundle speaks `Duplicate` and does not change counts, inventory, sales, activation, or audit state.
   - Updating inventory validates missing game prices and commits all staged bundles to unopened receiving inventory.
   - Activating a received bundle removes it from Receiving in the same transaction that creates the active bin placement and activation sale.
   - Closing shows the current-shift activated bundle count; closing history, `shift_summary.csv`, text report, and PDF report preserve the count after finalization.
11. Verify physical bundle uniqueness and correction:
   - During initial import, scanning an already-imported Game ID + Bundle ID speaks `Duplicate` and does not add another row or change the selected bin.
   - Multiple different bundles can still be imported into the same bin.
   - Upgrading a database with duplicate active bundle rows keeps the most recently recorded placement, removes older duplicates, and creates a system audit entry without changing sales history.
   - An unscanned malformed duplicate cannot produce double closing gap-fill.
   - Removing a selected received or active bundle requires confirmation, updates current inventory, writes audit, and preserves sales/activation history.
12. Verify inventory and closing audit coverage:
   - Receiving and closing session start, accepted/rejected scans, close/cancel, finalization failures, successful finalization, and closing reconciliation decisions include actor, UTC timestamp, workflow, and relevant bundle/bin/ticket details.
   - Game setup changes and successful received/active inventory removals are audited.
   - Duplicate inventory scans produce only `Duplicate` audio and do not create an audit row.
   - An audit insert failure is written to the application log without blocking the operator action.
13. Verify installer upgrade timing on the Windows machine:
   - Installing a newer build over an existing SimpleLotto installation performs an in-place upgrade without launching the previous uninstaller.
   - The Visual C++ runtime installer is skipped when a compatible x64 runtime is already installed.
   - An unsigned build skips the certificate-import PowerShell step.
   - The existing firewall rule is refreshed without accumulating duplicate rules.
14. Verify login keyboard behavior:
   - Opening the login screen focuses the password field.
   - Pressing Enter in the password field performs the same validation and login action as clicking Login.
   - Invalid passwords remain on the login screen and show the existing validation message.
15. Verify manually published branch upgrades:
   - A manual Windows workflow run without `publish_update_manifest` builds/uploads the installer but does not replace the public latest manifest.
   - A manual run with `publish_update_manifest` enabled uploads the manifest and the installed app's Check for Upgrade action discovers that exact build.
16. Verify closing scan cancellation:
   - `Close scanning` keeps temporary evidence available for review and finalization.
   - `Cancel Closing Scan` with evidence asks whether to discard or keep scanning.
   - Discard clears closing rows, matched bins, issues, unmatched scans, reconciliation changes, and generated closing sales without changing persisted shift data.
   - Cancellation is audited with discarded counts.
17. Verify scanner capture and routing:
   - A paired HID scanner records valid scans while the app is unfocused and while it is minimized to the tray; normal keyboard typing from a different device does not create a scan or audit row.
   - Reconnecting the paired scanner changes Settings and Dashboard status to listening; unplugging it shows a clear not-detected status.
   - An unpaired scanner completes a valid fast barcode both with Enter/Tab and after 400 ms idle; ordinary typing in text, password, search, rich-text, and number fields does not become a scan.
   - An incomplete unpaired or paired fragment is discarded after five seconds without affecting the separate configurable activation bin/bundle pairing window.
   - `BIN-<digits>`, `PRICE-<cents>`, and a configured-state ticket are classified before routing. Email-like text or any other non-barcode sequence is rejected, audited with its raw value, and says `Scan again.`
   - Receiving and Closing reject bin and price commands with `Ticket only.` Receiving finalizes through `Update Inventory`; Closing retains `Close Scanning`.
   - A price label can populate the activation and receiving game-price field while normal manual price entry remains possible.

## Missing Follow-Up Work

1. License expiry lifecycle needs to be aligned with the final rule:
   - Renew-soon banner starts 7 days before signed `expires_at`.
   - Signed `expires_at` date starts a 7-day grace period while the app remains usable.
   - Full operational lock starts after `expires_at + 7 days`, not immediately on `expires_at`.
   - Automatic license refresh should run non-blocking at startup and recovery points such as login/license-sensitive actions when near expiry, in grace, or locked.
2. Rdisplay expiry/grace banner is not implemented:
   - Current display payload only sends `license_status`.
   - Rdisplay client/renderer must be extended with banner text, expiry/grace fields, and daily opacity changes before transparent display banners can work.
