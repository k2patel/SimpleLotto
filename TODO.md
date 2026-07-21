# SimpleLotto TODO

## Known-Good Closing Baseline

- On 2026-07-18, the user confirmed that installer [`SimpleLotto-0.0.1-921fba3.exe`](https://files.k2patel.in/u/SimpleLotto-0.0.1-921fba3.exe), built from commit `921fba3`, produced the correct closing outcome and that its closing scan worked correctly with the scanner paired.
- Keep this installer and commit as the closing regression baseline until a newer build passes the same Windows store test. Do not infer a regression from code differences alone.
- The exact game, bundle, bin, ticket, and manual-total values used for that successful run were not captured. Before testing a newer build, record the scanner pairing/focus state, game price, bundle price, first/last ticket, starting bin/bundle/current ticket, scanned barcode sequence, manual closing totals, and expected closing result so the comparison is reproducible.

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
   - Initial import collects bin + bundle/ticket placements first, then `Continue to Login` requires only the ticket price once per distinct unconfigured Game ID. Repeated bundles for the same Game ID reuse that saved price; bundle total is derived automatically; cancel, invalid input, game-save failure, or setup-save failure keeps initial import open.
   - After setup completes, regular scanning of any ticket serial in a new bundle, then bin, then missing game price/name records activation gap-fill from the global first ticket through the scanned ticket; for example with global start `000`, `001` records `000-001`, quantity `2`, next `002`, and `008` records `000-008`, quantity `9`, next `009`.
   - With active current ticket `002`, scanning `003` records sale range `002-003`, quantity `2`, amount `2 * game price`, and next available ticket `004`.
   - If a placed bundle has no valid positive game price or valid automatically derived bundle total, scanning a ticket must require complete game setup and must not record a `$0.00` sale.
   - Verify automatic bundle totals and completion: a `$50` ticket derives a `$900` bundle; every other positive ticket price derives `$500`. No initial-import, activation, receiving, Add Game, or Game Prices workflow asks for bundle price. Derived totals must still produce a whole ticket count or configuration remains blocked.
   - Verify the one global first-ticket setting controls every game and bundle, persists across restart, and is not shown as a per-game or per-bundle field.
   - Upgrade a database without the global setting: unanimous/majority legacy `001` games initialize the global control to `001`; no configured games initialize it to `000`.
   - Verify premature sold-out recovery for every denomination after automatic bundle-total or global-first-ticket correction: affected grey bins reopen at the first unclaimed ticket (using the ticket-claim ledger after restart), while genuinely complete bundles at the calculated last ticket remain sold out. Later bundles with the same configured Game ID reuse its saved ticket price without prompting again.
   - Verify ledger integrity: scan ticket `003` twice and confirm only the first scan records revenue; repeat in a closing backfill. Void a sale twice and confirm the second attempt is rejected, including after restarting the app.
   - Verify configuration integrity: make SQLite game persistence fail and confirm activation/receiving does not continue; use missing, non-divisible, or malformed price/range configuration and confirm activation and closing are blocked without a one-ticket or `$0` sale.
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
   - Updating inventory validates missing ticket prices, derives bundle totals automatically, and commits all staged bundles to unopened receiving inventory.
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
   - The PIN migration release keeps the existing Password field, unrestricted input, focus behavior, and Enter-key login action.
   - Opening the login screen focuses the Password field, and pressing Enter performs the same validation and login action as clicking Login.
   - New setup limits Manager and optional Clerk PIN fields to four characters and accepts only values containing exactly four digits; letters and shorter values are rejected.
   - Invalid passwords or PINs remain on the login screen and show the existing validation message without a retry delay or account lockout.
   - Every correct legacy SHA-256 credential, including an existing four-digit value, requires creation and confirmation of a different four-digit PIN before login completes.
   - The required PIN dialog captures valid values before closing. Invalid, mismatched, or unchanged values keep the same dialog open with a specific inline message and never reopen in a loop.
   - Successful required migration atomically replaces only that user's stored hash with the versioned PBKDF2 format; cancelling the dialog or failing the write does not log in.
   - A malformed hash or failed hash-upgrade write does not log in or replace the in-memory credential.
   - Settings > Users/PIN lets Manager and Clerk change their own PIN. Each flow rejects an incorrect current PIN, a repeated current PIN, non-four-digit values, and mismatched confirmation.
   - Clerk sees only PIN and Scanner and Display settings. The Clerk reset card and all other Manager-only settings remain hidden.
   - Manager can create or reset the Clerk login using a name and matching four-digit PIN without knowing the previous Clerk PIN.
   - Successful own-PIN changes and Manager-authorized Clerk resets take effect on the next login, preserve the current close interval, and write audit entries without recording a PIN or hash.
   - The later strict PIN-enforcement release is separate: only after the migration version is deployed should login itself reject or prevent non-four-digit input.
15. Verify manually published branch upgrades:
   - A manual Windows workflow run without `publish_update_manifest` builds/uploads the installer but does not replace the public latest manifest.
   - A manual run with `publish_update_manifest` enabled uploads the manifest and the installed app's Check for Upgrade action discovers that exact build.
16. Verify closing scan cancellation:
   - `Close scanning` keeps temporary evidence available for review and finalization.
   - Reopening `Start Closing Scan` resumes the same valid scan rows, matched bins, generated closing ranges, unmatched scans, and errors instead of resetting them.
   - Rescanning the correct ticket for a bundle replaces that bundle's earlier rejected error. Selecting `Discard Selected Error` removes only that rejected row; valid scans remain and can still finalize.
   - `Cancel Closing Scan` with evidence asks whether to discard or keep scanning.
   - Discard clears closing rows, matched bins, issues, unmatched scans, reconciliation changes, and generated closing sales without changing persisted shift data.
   - Cancellation is audited with discarded counts.
17. Verify scanner capture and routing:
   - A paired HID scanner records valid scans while the app is unfocused and while it is minimized to the tray; normal keyboard typing from a different device does not create a scan or audit row.
   - With the paired scanner active and SimpleLotto hidden in the tray, scanning an unknown bundle restores and foregrounds the app before the activation-bin dialog; a missing-price activation also restores before its price dialog. Configured-game sales that need no dialog leave the app in the tray.
   - Reconnecting the paired scanner changes Settings and Dashboard status to listening; unplugging it shows a clear not-detected status.
   - Paired and unpaired keyboard-class scanners preserve the complete digit sequence regardless of inter-character timing and dispatch exactly once on Enter, matching the known-good `main` behavior. No inter-character or idle timer splits, emits, or discards barcode input.
   - With a paired scanner, Closing exclusively receives each complete Enter-terminated scan from the paired Raw Input path before global classification; dashboard sale, activation, import, and other routes do not process it. When paired capture is unavailable, Closing uses focused WinUI `KeyDown` as fallback. Confirm the displayed scan exactly matches the scanner output and each physical scan is processed once.
   - Scan a ticket from a bin containing two active bundles during Closing. Confirm one Enter produces exactly one scan row with the raw printed barcode and no second inferred number, bundle, or outside-range error.
   - Resize the app while Receiving and Closing scan overlays are open. Confirm the header and all footer actions remain visible/clickable, only the scan list scrolls, and narrow layouts stack buttons without horizontal or vertical clipping.
   - `BIN-<digits>`, `PRICE-<cents>`, and a configured-state ticket are classified before routing. Email-like text or any other non-barcode sequence is rejected, audited with its raw value, and says `Scan again.`
   - Receiving and Closing reject bin and price commands with `Ticket only.` Receiving finalizes through `Update Inventory`; Closing retains `Close Scanning`.
   - A price label can populate the activation and receiving game-price field while normal manual price entry remains possible.
18. Verify header metrics:
   - Bins shows an `Activated bundles` card counting activations in the current open close interval; completing a shift resets the displayed count for the new interval.
   - Closing Sales displays whole-dollar currency in a card wide enough for a five-digit amount, while closing details and reports retain exact cents.
   - At reduced window heights, verify every dialog keeps its title and footer actions visible inside the dialog border while only its content scrolls. In particular, test Finalize Closing, Cached Image, Pair Scanner, Add Game, activation game setup, receiving game setup, and closing reconciliation.
19. Verify crash-consistent closing reports and backups:
   - Force report-folder creation or report writing to fail and confirm the SQLite closing transaction still commits, the next close interval begins, and a pending/failed `closing_report_outbox` row retains the immutable report snapshot.
   - Restart after a committed close with a pending report job and confirm the exact shift report is regenerated, the outbox row becomes completed, and no sale, closing, or inventory row is duplicated.
   - Interrupt report generation after some artifacts are written, restart, and confirm partial output is replaced by the complete report set.
   - Create a backup while the active database has committed WAL frames, restore/open the zipped `simplelotto.db`, and confirm the latest committed closing, sales, inventory, and outbox data are present.
20. Verify schema-v17 ledger identities and migration on copies of real store databases:
   - Before opening an older database, confirm `migration-backups/simplelotto-pre-ledger-v17-from-v<version>.db` is created through SQLite online backup and can be opened independently.
   - For history whose inferred sale rows exactly match each saved closing's row count, ticket count, and cents, confirm sales and activations receive the verified closed interval ID and the current rows receive the one open interval ID.
   - Move the Windows clock backward, record a sale, restart the app, and confirm the sale remains in the current interval because membership uses `interval_id`, not `sold_at_utc`.
   - Close the interval and confirm closing history, generated gap-fill sales, report outbox, closer actor, closed interval, and newly opened interval commit together. Confirm an old interval ID cannot accept a later sale.
   - Confirm every new sale has a stable sale ID, actor ID/name, interval ID, and source; every activation has actor and interval identity; and report CSV rows preserve these IDs.
   - Void one sale and confirm the negative correction references the original sale ID, a second void is rejected, and the original physical ticket claims remain attached to the original sale.
   - Attempt direct update/delete of a sale, ticket claim, activation event, closing-history row, and closed interval and confirm SQLite rejects it.
   - Seed duplicate historical ticket claims, malformed ranges, missing Bundle IDs, summary mismatches, and ambiguous legacy voids. Confirm no financial rows are deleted or rewritten, unresolved data is quarantined in `legacy_unresolved`, structured conflicts are stored, and matching Audit entries are visible after login.
21. Verify manual bundle movement from Bins:
   - Selecting a bin alone does not show `Move Bundle`; selecting one bundle card in Bin Details reveals and enables it.
   - `Move Bundle` shows the selected Game ID/Bundle ID, accepts only a whole configured destination bin, rejects the current bin, and provides `OK` and `Cancel`.
   - Cancelling changes nothing. Confirming changes only the active placement bin while preserving current ticket, sold-out state, sales, activation history, and close-interval history.
   - After a successful move, Bins, Inventory, Closing, Rdisplay, and Audit show the destination bin and the old-to-new movement.
22. Verify the Settings Audit viewport and performance:
   - Seed more than 200 audit rows and confirm startup loads only the latest 200 into UI memory while every row remains stored in SQLite.
   - Confirm the Audit list stays within one screen-height page, long detail text is ellipsized, and Previous/Next changes pages without creating an unbounded outer vertical scroll.
   - Record scanner and workflow audit events while another page is open and confirm the Audit page refresh is deferred until it is opened.

## Missing Follow-Up Work

1. License expiry lifecycle needs to be aligned with the final rule:
   - Renew-soon banner starts 7 days before signed `expires_at`.
   - Signed `expires_at` date starts a 7-day grace period while the app remains usable.
   - Full operational lock starts after `expires_at + 7 days`, not immediately on `expires_at`.
   - Automatic license refresh should run non-blocking at startup and recovery points such as login/license-sensitive actions when near expiry, in grace, or locked.
2. Rdisplay expiry/grace banner is not implemented:
   - Current display payload only sends `license_status`.
   - Rdisplay client/renderer must be extended with banner text, expiry/grace fields, and daily opacity changes before transparent display banners can work.
