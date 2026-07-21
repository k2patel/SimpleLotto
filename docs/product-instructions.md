# SimpleLotto Product Instructions

## High-Level Overview

SimpleLotto is a Windows UI application for lottery counter operations. Its purpose is to keep the daily workflow simple: manage lottery sales, manage lottery inventory, close the day, and configure the system.

The application should stay smaller and simpler than `../windowsPOS`, but it should reuse the same proven mechanisms where they matter, especially the local app/service pattern and the Rdisplay-style display communication model.

## Core Capabilities

### Manage Lottery Sales

- Record lottery ticket sales during the business day.
- Support sale entry by game, bin, ticket count, and dollar amount.
- Keep running totals for the current open close interval/day.
- Allow correction or void workflows with clear operator confirmation.
- Feed sales activity into closing totals so end-of-day balancing is straightforward.

### Manage Lottery Inventory

- Track active lottery inventory by bin.
- Show which game/book is assigned to each bin.
- Support inventory receiving, activation, movement, and ending counts.
- Keep enough history to explain inventory changes during closing.
- Make inventory status visible from the selling workflow so operators can act quickly.
- Allow multiple active bundles in the same bin when the workflow requires it.
- Treat each bundle as its own inventory object even when several bundles share one bin.

## Roles and Login

SimpleLotto has only two possible roles:

1. Clerk
2. Manager

Role-based access is always enabled. Do not build a setting to turn role-based access on or off.

First-install setup must require creation of a Manager PIN before the system can be used. A Manager account is mandatory.

Clerk accounts are optional. The first-install workflow may allow the installer to create a Clerk account, but it must not require one.

Login expectations:

- A login screen is always shown after setup is complete.
- Any valid user can log in.
- The Password field receives focus when the login screen opens, and pressing Enter in it performs the same login action as the Login button.
- During the PIN migration release, keep the existing Password field and unrestricted login input so every installed legacy password remains usable for its required one-time migration.
- After any legacy password verifies successfully, do not enter the application yet. Require the user to create and confirm a different four-digit ASCII PIN, persist its versioned PBKDF2 hash, and only then complete login.
- Validate and capture the new PIN before the required `ContentDialog` closes. Invalid, mismatched, or unchanged values must keep the same dialog open with inline feedback; do not close and recreate the dialog or read cleared password controls after closing.
- New setup credentials, user-initiated credential changes, and Manager-authorized Clerk resets must create exactly four-digit ASCII PINs.
- Do not add password-composition rules beyond the four-digit PIN requirement, failed-login delays, or account lockouts.
- Release a later strict PIN-enforcement version only after the migration release has given installed Manager and Clerk accounts the opportunity to convert. That later version may limit the login field itself to four digits.
- Logging in controls access only. It does not start, end, or otherwise define a financial shift/close interval.
- SimpleLotto is a single-computer, single-active-user application.
- Only one user is actively operating the application at a time.
- The active user can access the operational workflow allowed for their role.
- The active user can run closing at any time.
- Shift closing is primarily financial separation by the persistent closing-to-closing interval, not a hard user handoff.
- The same logged-in user may close multiple times in the same day.
- Manager access is required for sensitive system settings and user management.
- Clerk access, when configured, should support normal sales, bin, inventory, and shift-closing workflows unless a specific action is manager-sensitive.
- Settings is Manager-only except Clerk may see a limited Settings page with PIN and Scanner and Display tabs.
- Manager and Clerk may verify their current PIN and change their own PIN. A Clerk must never see Manager-only store, user-reset, backup, email, audit, or game settings.

First-install workflow:

1. Create required four-digit Manager PIN.
2. Optionally create a Clerk user.
3. Scan the initial bin and bundle/ticket placements.
4. Before initial import can finish, enter and persist the ticket price once for every distinct imported Game ID that is not already configured. Derive the bundle total automatically from that ticket price.
5. Continue into the first login screen only after all imported Game IDs have valid saved pricing.
6. Successful login enters the currently open financial close interval.

## Shift Model

SimpleLotto closes from shift to shift. There is no separate daily closing model by default.

In SimpleLotto, a shift is an always-running financial close interval. Login is not the start of a shift. The business boundary is the persistent interval opened by the previous successful close and closed by the current successful close; its timestamps describe that span but do not determine row membership.

- Sales and inventory movement occur inside the currently open close interval.
- The current close-interval record is opened by the previous successful close. On first install, the first interval is created during database/setup initialization before operational activity.
- Any active authorized user can close the current interval at any time.
- Closing records the current close time and creates the financial summary for rows assigned to that explicit interval.
- After closing succeeds, the next close interval begins immediately; no logout or user handoff is required.
- The same logged-in user may close multiple intervals in one day.
- Cash summaries are shift-to-shift only.
- Closing reports should be organized around the interval being closed.
- The user-facing shift reference is today's local date plus an incremental number for that date, such as `2026-07-06 #1`, `2026-07-06 #2`, and so on.
- Internal SQL identifiers may be arbitrary for performance and joins, but they must not imply that login creates accounting value.

If a calendar-day summary is ever shown, it must be clearly secondary and must not replace shift closing.

Future summarization should support partitioning by shift, user, calendar period, or other reporting groups, but the financial summary remains shift-closing to shift-closing.

## Reporting and Accounting Separation

Inventory numbers and sales numbers are different accounting concepts and must not be mixed.

Sales tracking priority:

- Accurate sales tracking is the primary accountability requirement.
- Bundle identity, ticket serial, sold/gap-fill sales, corrections, and cash impact matter more than perfect bin history.
- Bin tracking supports operator display, Rdisplay, and workflow convenience. It is important, but it is not the accounting source of truth.
- If bin placement is wrong or uncertain, the user can correct it later by moving bundles or resolving the state during closing.
- Do not let bin-display ambiguity rewrite, hide, or block valid sales tracking when the bundle identity and ticket serial are known.

Sales reporting answers:

- How much was sold?
- Which games produced sales?
- What cash/payment total should be reconciled for the shift?
- What corrections or voids affected the sales total?

Inventory reporting answers:

- What inventory was loaded?
- What inventory moved between bins?
- What ticket/book counts remain?
- What activation, receiving, or ending-count changes happened?

Closing may show both inventory overview and sales numbers on the same page, but they must remain visually and mathematically separate. Do not combine inventory counts with sales dollars into one total.

Cash summary rules:

- Cash summaries are shift-to-shift only.
- Cash summaries are based on sales/payment activity, not inventory counts.
- Inventory overview can support reconciliation, but it is not the cash summary.
- Any variance should identify whether it belongs to sales/cash or inventory/counts.

## Game, Bundle, and Ticket Rules

Inventory must let the user define the per-ticket price for each Game ID. The app derives the bundle total from that price and uses both values to determine how many tickets are in a bundle.

Default supported game prices should include:

- $1
- $2
- $5
- $10
- $20
- $25
- $50

The user may add another ticket price from Inventory game setup when it divides evenly into its automatically derived bundle total.

Game price rules:

- Each game type has a game ID.
- Each game type has a display name.
- Each game type has an image when available.
- Each game type has a price per ticket.
- Game ID, game name, game price, and related game metadata are stable once defined and should not change during normal operations. Game name may be established or corrected through explicit user edit or license-server sync rules.
- New game types require the user to set a positive price before the game can be used operationally.
- If a game is activated for the first time or added through inventory receiving for the first time, the user must enter or confirm the per-ticket game price before that workflow can finalize. Do not ask for a bundle price.
- Regular bundle activation follows the same missing-configuration rule as inventory receiving. If the scanned bundle's Game ID is new or has no valid positive ticket price, activation must pause and open a required game-setup dialog before assigning the bundle to the bin.
- If the scanned bundle's Game ID already has a saved valid ticket price, activation must reuse it and must not ask for pricing again; after the bin is selected/scanned, activation should continue. A missing display name may be corrected separately and must not cause a valid saved price to be requested again.
- The activation game-setup dialog should show the game ID, bundle ID, selected/scanned bin, ticket-price field, automatically derived bundle total, and any fetched/manual game name and image when available.
- The activation game-setup dialog must allow the operator to enter or scan the ticket price into the same game price field; do not add a separate price-scan text field.
- Regular bundle activation must keep input collection tight. Across the bundle activation process, the operator should only need fields for bin, ticket price, and game name; do not add a bundle-price field or separate barcode-focused text boxes for values already captured by the scanner workflow.
- Entering a missing ticket price during activation is an operational setup exception and may be completed by the active clerk or manager because activation cannot safely proceed without it.
- Inventory receiving may collect all scanned bundle barcodes first. When the user clicks close/finalize receiving, the app must check whether every scanned Game ID has a valid positive ticket price.
- If receiving includes any new or incompletely configured game IDs, the app must present a required game-setup dialog for each missing game before finalizing receiving.
- The receiving game-setup dialog should show the game ID, ticket-price field, automatically derived bundle total, and any fetched/manual game name and image when available.
- The app may attempt a best-effort price lookup using the same state/game setup source used for names and images, but an auto-found price is only a suggestion. The user must confirm or correct the price before saving.
- Price lookup failure must not block manual setup; the user must be able to enter the game price manually.
- Auto-fetched game names and images should reuse the already-wired state setup mechanism from `../WindowsPOS`.
- Auto-fetch failures must not block manual setup. The user must be able to enter or correct the lotto name and image manually.
- Initial import may collect bin and bundle/ticket placement pairs before asking for prices. When the operator continues to login, the app must validate every distinct imported Game ID and present the same required ticket-price setup for each unconfigured Game ID.
- Multiple initially imported bundles with the same Game ID share one saved game configuration. Initial import must prompt at most once for that Game ID and reuse the persisted prices for all of its bundles.
- Cancelling a required initial-import game setup, entering invalid prices, or failing to persist the game/setup state must keep the initial import open and must not continue to login.

Game image handling:

- Game image fetch should run when inventory receiving records a bundle and when regular activation places a bundle into a bin.
- Image lookup must be cache-first. If the image for that game is already cached locally, do not fetch or pull it again.
- If no cached image exists, the app should fetch and cache the image using the already-wired state/game setup mechanism from `../WindowsPOS`.
- Manual user-uploaded images must be supported and should satisfy the cached-image requirement for that game.
- A user-uploaded image should not be overwritten by automatic fetch unless the user explicitly replaces it.

Ticket barcode parsing:

- Ticket barcode parsing should follow the state-specific layout model used by `../WindowsPOS`.
- Barcode parsing must use the configured store/state layout strictly; do not guess another state layout after a configured layout fails.
- Parsed ticket identity must separate game code, bundle/pack number, and ticket serial.
- Physical bundle identity is `game code + bundle/pack number`; ticket serial identifies the current ticket within that physical bundle.
- Physical bundle identity is globally unique in current inventory. The same Game ID + Bundle ID must never appear more than once, even when multiple different bundles are allowed in the same bin.
- Duplicate bundle scans during initial import or receiving must speak `Duplicate` and must not add, move, sell, activate, audit, or otherwise change the bundle.
- Storage must enforce physical-bundle uniqueness in addition to UI scan checks. If legacy data contains duplicate active rows, migration must keep the most recently recorded placement, remove the older duplicate rows, and write a system audit entry without changing sales or activation history.

Bundle price rules:

- Bundle total is derived automatically from the per-ticket game price. It is not entered for each game or bundle.
- For now, a `$50` ticket price always derives a `$900` bundle total. Every other positive ticket price derives a `$500` bundle total.
- A future bundle-type creator may replace this hardcoded mapping, but do not expose per-game or per-bundle bundle-total entry in the current workflow.
- Ticket count per bundle is calculated as: `automatic bundle total / ticket price`.
- Bundle price must produce a whole ticket count. If it does not, the system must reject the value or require correction before saving.
- Bundle price is used for ticket range calculation, sold-out detection, and closing accountability.
- Price setup is complete only after the game configuration is successfully persisted. A SQLite save failure must leave activation or receiving blocked and must not be reported as a successful price setup.
- Missing or invalid ticket price, derived bundle total, ticket count, current ticket serial, or configured ticket range must block activation, normal sales, and closing-generated sales. Financial workflows must never substitute a one-ticket or `$0` fallback ledger row.

Ticket numbering rules:

- Inventory game setup must provide one global choice for whether every bundle starts at `000` or `001`. First-ticket mode is not stored or edited per game or per bundle.
- When upgrading a database that predates the global setting, initialize it from the existing per-game first-ticket value used by the majority of configured games; if there are no configured games, use `000`. The operator can then explicitly save a different global value.
- End ticket is calculated from game price, the automatically derived bundle total, and the global first-ticket mode.
- If first ticket is `000`, end ticket is `ticket_count - 1`.
- If first ticket is `001`, end ticket is `ticket_count`.
- Ticket range calculations must stay consistent across sales, inventory, Rdisplay, and closing.

Bundle completion rules:

- A bundle is complete when its sold ticket count/value reaches the automatically derived bundle total.
- When the final valid ticket is sold, keep the bundle assigned to its bin and mark it `Sold out`/grey in Bins and Rdisplay. Its displayed ticket remains the final valid ticket; it must never advance to a non-existent serial (for example, `$20`/`$300`, start `000`: `000`-`014`, never `015`).
- The sales ledger records only the ticket or inclusive ticket range actually sold. The bin/Rdisplay current-ticket state is operational inventory state and is stored separately.
- A ticket serial may be recorded only once for a placed bundle. Re-scanning a serial below the current ticket, including during a backfill or closing scan, must be rejected and must not change sales totals. SQLite must retain a unique per-bundle ticket claim so concurrent/repeated scan events cannot bypass this rule.
- A sale can be voided once only. The void is an auditable reversing ledger entry; a second void of the same sale and a void of that correction are rejected.
- A bundle can also be treated as sold out during closing if it is expected but not scanned when the user finalizes the shift; retain that bin placement as grey `Sold out` after recording the closing gap-fill range.
- Closing-generated sold-out handling must be recorded separately from normal scanned sales so reports can explain the difference.

## Required Main Menu

The application must have exactly five primary menu items:

1. Dashboard
2. Bins
3. Inventory
4. Closing
5. Settings

Do not add separate top-level menu items for reports, tools, admin, users, displays, help, sales, or setup. Those workflows should live inside one of the five sections if needed.

## Navigation Behavior

- Use a left-side collapsible menu.
- Expanded state shows icon plus label for each menu item.
- Collapsed state keeps the left rail visible and shows only icons.
- The selected item must remain visually clear in both expanded and collapsed states.
- Each icon must have a tooltip so the collapsed menu remains usable.
- The menu should feel like a Windows operational app, not a marketing page.

Suggested icon mapping:

- Dashboard: home/dashboard icon
- Bins: grid/squares icon
- Inventory: package/box icon
- Closing: checklist/receipt icon
- Settings: gear icon

## Page Responsibilities

### Dashboard

Dashboard is the home screen and first view when the app opens. It should provide quick access to the most common daily actions without replacing the deeper Bins, Inventory, Closing, or Settings pages.

Dashboard should include:

- Current open close interval status.
- Total active books.
- Total bins.
- Quick sale entry or jump-to-sale action.
- Quick access to active bins.
- A close-shift action.
- A cancel or undo-last-scan action.
- Inventory alerts or low/attention-needed inventory.
- Current close-interval sales summary.
- Closing readiness or next closing action.
- Display/device connection status if attention is required.

Dashboard must stay operational and compact. It should not become a separate reporting module or add new top-level workflows.

Dashboard current-shift metrics should focus on operational accountability: sales amount, tickets sold, active/open inventory context, and closing readiness. Do not show an average ticket price metric on Dashboard unless a future reporting workflow explicitly needs it; average ticket price is not useful in the live counter workflow.

Dashboard undo rules:

- Undo is available from Dashboard.
- Undo applies only to sales in the current open close interval.
- Undo applies only to the last sale scan/action that is eligible for undo.
- If a new close interval has just started and no sale scan has occurred, Undo does nothing or shows a clear no-op status.
- Undo must not edit a closed interval.
- Corrections for closed intervals must be recorded in a later/current open interval as corrective actions.

Dashboard quick summary overlay:

- Dashboard should provide one compact quick-summary command instead of dedicated buttons for every summary/action.
- Use a WinUI `Flyout` or `MenuFlyout` style overlay for this compact action picker.
- The command should be reachable from a clear Dashboard button such as "Summary" or "Quick actions".
- The overlay should show selectable actions for:
  - This month
  - This week
  - Today
  - Inactive
  - Inventory status
  - Update inventory
- Selecting a summary item should update the Dashboard summary area or open a lightweight detail overlay, not navigate away unless the action truly requires the Inventory page.
- `Update inventory` may route to Inventory when a full workflow is required.
- Keep the overlay keyboard accessible and usable by touch.
- The overlay should not hide urgent alerts, closing status, or current close-interval state.

Dashboard bin behavior:

- Empty bins are gray.
- Non-empty bin color is based on sales activity for the game number, not the bundle ID.
- Use the color scale to show highest-selling, medium-selling, and low-selling game activity.
- If multiple bundles are in the same bin, the bin tile should indicate that stacked/multiple-bundle state.
- Low, medium, and high single-bundle colors should use lighter base fills. Multiple-bundle/stacked variants should be visibly darker than the base state so the difference is noticeable at a glance.
- When the user opens a bin, do not show the game photo as the primary detail. Show a summary of each bundle in that bin.
- Bundle summary must include price, lotto name, game ID, bundle ID, and ticket range/status.
- Multiple bundles in one bin should use a slightly darker visual treatment than a normal single-bundle bin so the operator can distinguish it quickly.
- The current/latest bundle for a bin is whichever bundle was scanned most recently for that bin.
- Older bundles in the same bin become dormant for display purposes.

### Bins

Bins is the operator-first display page. It should show the working bin layout, active games, available ticket ranges, and current selling status. Operators should be able to understand what is loaded and where without opening multiple screens. Bin state supports display and workflow correction; it must not be treated as more important than preserving accurate sales activity.

Bins must support multiple bundles in the same bin. The latest bundle in the bin is the one shown on Rdisplay.

Bundle activation definition:

- Bundle activation means assigning a bundle that is not currently recorded in any bin into a bin.
- Activation is based on bin assignment state, not ticket number.
- A bundle does not have to start at ticket `000` to be activated.
- If a scanned bundle has no existing record in any bin during normal bin/inventory workflow, the system should treat that scan as a bundle activation candidate.
- If a bundle is scanned before a bin and is not active, the app should open a focused bin-entry dialog, speak `Enter bin number or scan bin`, and allow a typed bin number or scanned bin barcode to close the dialog and use that bin as the placement location.
- The exception is inventory receiving: bundle scans captured while adding/receiving inventory are receiving records only and must not be treated as activation until the user assigns the bundle to a bin.

Regular bundle activation:

- Regular bundle activation is separate from the closing reconciliation loop.
- Regular bundle activation is also separate from the first-time setup initial import. Initial import records opening inventory only and must not create activation gap-fill sales.
- During normal inventory/bin workflow, bins may contain multiple active bundles.
- A bundle can be activated into a bin by scanning bin then bundle, or bundle then bin.
- The bin scan and bundle/ticket scan that form one activation pair must happen within the configured scan-pair window. The default is 5 seconds.
- The scan-pair window must be configurable under Settings > Scanner and Display.
- When a clerk activates a bundle, the bin can be identified either by scanning the bin barcode or by entering/selecting the bin number on screen.
- Scanning the bin and entering/selecting the bin number are equivalent activation inputs; either one satisfies the bin-identification requirement.
- The Bins detail panel should provide an Add Bundle action for the selected bin so the clerk can activate a scanned bundle directly into that bin without scanning the bin barcode again.
- Selecting a specific bundle card in Bin Details must reveal a `Move Bundle` action. Do not show the move action when no bundle is selected.
- `Move Bundle` must open a focused dialog that identifies the selected bundle, requires a whole destination bin number within the configured bin range, and offers `OK` and `Cancel`.
- A successful manual move changes only the bundle's current bin assignment. Preserve its Game ID, Bundle ID, current ticket, sold-out state, sales, activation history, and close-interval history; refresh Bins, Inventory, Closing, and Rdisplay and audit the old and new bins.
- Entering the current bin as the destination must keep the dialog open and require a different bin.
- If the clerk scans or enters a bin number that does not exist, block activation and ask the clerk to scan the bin again or enter the correct bin number.
- For an invalid bin, the app should give audio feedback such as `Wrong bin`.
- Once both the bin and unassigned bundle are known, regular activation should complete immediately without an extra confirmation prompt.
- After successful activation, the app should give short audio feedback such as `Bundle activated in bin 12`.
- Activation should trigger cache-first game image lookup for the activated game.
- The scanned activation ticket is treated as sold during placement, along with any prior tickets in that bundle.
- After activation, the active bundle ticket is the next available ticket after the scanned activation ticket and should be shown as current on Rdisplay.
- During activation, tickets from the first ticket through the scanned activation ticket are recorded as sold/gap-fill sale activity for the current open close interval.
- This rule applies to any valid ticket serial in the scanned bundle. Ticket `001` and ticket `008` are examples only; the scanned ticket number determines the inclusive sold range and the next available ticket.
- Placement-created gap-fill sales must use the source label `activation_gap_fill` so reports can distinguish them from normal scanned sales.
- The gap-fill range follows the configured first-ticket mode: if first ticket is `000` and activation scans `004`, tickets `000-004` are sold and next ticket is `005`; if first ticket is `001`, tickets `001-004` are sold and next ticket is `005`.
- Regular activation only applies to bundles not currently placed in any bin. Moving an already placed bundle is a separate manual move workflow from the Bins UI.
- If a clerk scans a bin plus a ticket for a bundle already active in another bin, ignore the placement intent and do not move the bundle automatically.
- For an already active bundle placement scan, the app should give audio feedback such as `Bundle active in bin 7, move it manually`.
- Normal activation should not displace or force resolution of existing bundles in that bin because multiple bundles are supported.
- Closing-only displacement rules must not be applied to regular bundle activation.
- The most recently scanned bundle in a bin is the current active bundle for Dashboard/Bins/Rdisplay.
- Earlier bundles in that bin remain tracked but become dormant behind the latest scanned bundle.
- Rdisplay must show only the current active bundle for a bin, not dormant bundles.
- When the user opens a bin, the details view should show all active bundle records associated with that bin, including the current active bundle and dormant bundles.
- Dormant bundles may or may not still be physically in the bin. Their actual state is resolved during shift closing; if they are not scanned during closing, they are considered sold out according to closing rules.
- Dormant status is temporary within the current open close interval. After closing, dormant bundles should not persist as dormant; closing resolves each bin so only one current surviving bundle remains active and all other unscanned/dormant bundles are handled by closing rules.

### Inventory

Inventory is the stock management page. It should handle receiving books, assigning inventory, moving books between bins, reviewing active or pending inventory, and managing lotto game setup.

Inventory owns game setup because game identity, game price, automatic bundle-total rules, global ticket numbering, and game images are part of inventory/book setup. Do not duplicate these controls under Settings.

Inventory should organize stock and game setup into these tabs, in this order:

1. Receiving: scan unopened new bundles received into inventory. Receiving is the first tab because it is the recurring stock intake workflow.
2. Game Prices: shows game ID, image, lotto/game name, price per ticket, the automatic bundle-total rule, and one global ticket-numbering setting.
3. Open / Active Bundles: shows bundles currently open, active in bins, dormant behind another bundle, or otherwise inactive/pending resolution.

The active/open bundle tab should be last. Operators should be able to review active and inactive inventory from the Inventory menu without confusing it with unopened receiving.

Receiving contains unopened bundles that have not been activated into a bin. Once a received bundle is activated, it leaves receiving and becomes an open/active bin record. When all tickets in a bundle are sold, the bundle should no longer appear as an open/active inventory item; it remains as historical database/ledger activity for reporting and audit.

Inventory list behavior:

- The game catalog must be searchable by Game ID.
- Receiving, Open / Active Bundles, and Game Prices must support fast prefix search usable while typing. Typing `1`, then `12`, then `123` should narrow against records that start with that entered value.
- Open / Active Bundles search should include bin ID, game ID, bundle ID, and ticket where practical.
- Receiving search should include game ID and bundle ID. Receiving does not retain ticket serials.
- Receiving, Open / Active Bundles, and Game Prices should use paging instead of one long vertical scrolling list.
- Page size should be calculated from the visible list space so page counts grow or shrink with the window size; do not hard-code one fixed row count.
- Paging controls must show the current page and total pages when known.
- The Game Prices section must require a ticket price before a new game type can be used. It must show the hardcoded automatic bundle-total rule without asking the user to enter a bundle total.
- The Game Prices tab must include a control to view the selected game's currently cached image.
- From the cached-image view, the user must be able to remove the cached image when it is wrong or no longer wanted. Removing the image should not remove the game ID, game name, ticket price, automatic bundle-total rule, or global ticket-numbering setup.

Inventory receiving records bundles into stock without assigning them to bins. Receiving scans should create or update inventory records, but they should not create active bin assignments and should not be counted as bundle activation.

Receiving a bundle should trigger cache-first game image lookup for the received game.

Inventory receiving is the normal recurring stock intake workflow for new unopened bundles delivered to the store. It may happen daily, weekly, or whenever new lottery inventory arrives.

Inventory receiving is for unopened bundles. Because ticket `000` may be physically inaccessible, the dedicated receiving scan dialog may accept any valid ticket barcode from the bundle but must record only the parsed Game ID and Bundle ID. The ticket serial is ignored and must not create a sale, activation, or bin assignment.

The top bar must provide a `Scan New Inventory` action beside `Close Shift` only while the Inventory menu is selected. Its visibility must follow the Inventory content visibility directly, and the Receiving tab should repeat the same action as a clearly visible entry point. The action must be hidden on Dashboard, Bins, Closing, and Settings. Receiving runs as a focused modal scan session so scanner input cannot collide with sales, activation, or closing workflows. The receiving dialog lists scanned bundles on the left, shows the total bundles being added on the right, and provides an `Update Inventory` action that validates and finalizes the session.

If the same Game ID + Bundle ID is scanned more than once, is already in receiving inventory, or is already active in a bin, the app must speak `Duplicate`, take no action, and leave all receiving counts and records unchanged.

When a received bundle is later activated into a bin, it must be removed from unopened receiving inventory as part of the same successful activation transaction. Each successful activation must be recorded in an activation ledger so Closing can show how many bundles were activated in the current shift and include that count in shift history and generated closing reports.

Initial inventory import is separate from both receiving and regular activation. It is a one-time first-install workflow for capturing the store's current physical bin layout: which lotto bundle is already in each bin and which ticket is currently available for sale.

Initial import rules:

- Initial import happens only during first setup or an explicit re-initialization workflow.
- Initial import records active bin placement, not unopened received stock.
- Initial import requires bin position plus current available ticket for each already-open physical bundle.
- The scanned ticket becomes the starting current ticket for tracking, and tickets before it are an opening offset only.
- Initial import must not create sales, gap-fill revenue, or clerk cash accountability for those earlier tickets.
- Initial import records should be labeled separately from regular activation and inventory receiving in storage, UI, audit, and reports.
- Initial import may place multiple different bundles in one bin, but it must reject a Game ID + Bundle ID that already exists in any bin.

Do not overload one inventory record type to mean both unopened receiving and active bin placement. Unopened receiving, initial active-bin import, and later bundle activation must remain separate workflow sources even if a future UI shows them together.

Inventory correction rules:

- Receiving and Open / Active Bundles must allow the operator to select a bundle and use an explicit `Remove Selected Bundle` action.
- Removal must require a confirmation dialog that identifies the Game ID, Bundle ID, and bin when applicable.
- Removing a current inventory record corrects inventory state only. It must not delete or rewrite recorded sales, activation events, or closed-shift history.
- Every completed removal must be audited.
- Closing gap-fill and sold-out calculations must de-duplicate by physical Game ID + Bundle ID defensively, even though storage also enforces uniqueness.

### Closing

Closing is the balancing page. It should summarize sales, inventory movement, expected counts, corrections, and end-of-day totals. Closing should guide the operator through a clean final review before committing the day.

Closing must be shift-based. It should show cash summaries, inventory overview, and sales numbers as separate sections. Sales totals and inventory totals must not be mixed for reporting or accounting.

Closing scan rules:

- Clicking Shift Closing should open a dedicated scan dialog.
- The scan dialog should accept barcode scans and show a live count of scanned barcodes.
- The scan dialog should show enough live feedback for the user to know scans are being captured, but it should not finalize reconciliation while the user is still scanning.
- The scan dialog should allow the user to finish/close the scanning session.
- Closing the scan dialog must preserve all collected valid evidence. Reopening `Start Closing Scan` resumes that same in-progress evidence instead of resetting it.
- Only the explicit `Cancel Closing Scan` > `Discard` confirmation, a successful finalized close, or an explicit selected-error discard may remove in-progress closing evidence.
- A correct rescan for the same physical bundle must replace its earlier rejected scan state without deleting valid scans for other bundles.
- The scan dialog must allow the operator to select and discard a rejected/error row. Accepted scan rows are not individually discardable, and discarding an error must not clear accepted evidence.
- During the scan dialog, the user scans ticket barcodes only.
- During the scan dialog, the user does not scan bin barcodes.
- During the scan dialog, the user does not click/select a bin to tell the system where the ticket belongs.
- When the scan dialog closes, the system analyzes collected ticket scans against the existing active bundle/bin state.
- Closing scan evidence belongs to the shift being closed.
- Closing scan evidence represents the current real-world state of all physical bins.
- During closing, scanned bundles/tickets are inventory evidence, not normal live sales.
- During closing, the user scans the current available ticket from each physical bin/bundle.
- Closing scan evidence is treated as the truth of the physical bin state.
- If closing scan evidence does not match the system record, the system record must be reconciled to match the closing scan evidence.
- The system should associate each scanned ticket to an existing bin by finding the active bundle that owns that ticket.
- If a scanned ticket's bundle is active in an existing bin, that scan defines the current active bundle/ticket state for that bin.
- If a bin has multiple bundles, the scanned bundle becomes the active/current closing state for that bin and unscanned bundles in that bin are sold out during closing.
- If a scanned ticket's bundle is not active in any existing bin, the system must start the manual reconciliation loop to ask where that bundle belongs.
- Closing scan collection is not an assignment workflow. Assignment/placement decisions happen only during the post-scan reconciliation loop when the system cannot match scanned evidence to existing active bins.
- During closing reconciliation, each resolved bin should have a single current bundle relationship for the closing state.
- If the scanned current ticket is forward from the system's expected current ticket, the gap is recorded as sold.
- If the scanned current ticket is behind the system's expected current ticket, the system must reconcile the difference in the current open close interval. Do not edit closed intervals; any fix for previously closed activity must be recorded as a corrective action in the current/later open interval.
- Closing should not be treated as a free placement/activation workflow.
- Closing reconciliation should compare scanned bundle/ticket evidence against the system's existing bin assignments and surface differences before final submit.
- Any tickets/bundles that are part of the system's active bin state but are not scanned during closing are closed out when the user finalizes closing.
- Any active bundle expected during closing but not scanned is considered sold and should be recorded as gap-fill sold.
- Unscanned active bundles do not require per-bundle manual input before final submit.
- If closing scan evidence includes a bundle that is not activated yet, the user must reconcile it manually before final submit.
- Manual reconciliation for an unactivated bundle must require the user to choose which bin it belongs to.
- If the selected bin is not empty, the system must ask what happens to the existing bundle before the new/unactivated bundle can be accepted for that bin.
- The occupied-bin question must show the existing bundle's game name, game ID, Bundle ID, and ticket ID/current ticket.
- The occupied-bin question must allow the user to identify whether the existing bundle moved to another bin or was taken out of inventory.
- If the existing bundle is not sold out, it must either have moved or have been taken out of inventory; there is no third unresolved state.
- If the user says the existing bundle moved, they must choose the destination bin.
- If that destination bin has another existing bundle, the same occupied-bin question repeats for that displaced bundle.
- If the user says the existing bundle was taken out of inventory, the system should close/remove it from active inventory with an auditable reason.
- Taken-out-of-inventory reason options are: No sale, Returned, Correction.
- Taken-out-of-inventory bundles should go back to open inventory in the future workflow.
- This displacement/reconciliation loop applies only to shift closing.
- Reconciliation is a closing-only loop and must continue until every displaced, unactivated, missing, or conflicting bundle is resolved.
- Reconciliation should iterate until all unactivated bundles, conflicts, missing expected bundles, and bin-state differences are resolved or explicitly confirmed.
- The user must confirm that reconciliation is clear before finalizing the shift closing.
- In reconciliation context, "game bundle" means Bundle ID.
- Every reconciliation interaction must show at minimum: game name, Bundle ID, and game ID.
- Reconciliation interactions should also show bundle ID, current/expected bin, scanned ticket/current ticket, price, and status when available.
- During closing reconciliation, dormant bundles in a bin that are not represented in the scanned physical state should be considered sold as appropriate for close-interval accountability.
- The sold-out fill created by closing must be distinguishable from ordinary scan sales in audit/reporting.
- If a bundle reaches its automatically derived bundle total before closing, it is considered complete/sold out.
- Closing should make unscanned-bundle sold-out consequences clear before final submit.
- Closing scan prompts should use real-time text-to-speech so the operator hears the next action immediately.

Closing page bin status:

- Closing page should show all bins, active and inactive.
- Unscanned bins should be gray.
- Scanned bins should be green.
- Closing bin tiles should stay visually compact: show bin number and color state only, not repeated status words like Empty or Needs scan on every tile.
- If the user missed a bin, it is the user's responsibility to scan/fix it before final submit.
- Clicking a bin during closing should show expected game ID, Bundle ID, and ticket ID/current ticket.
- If the user scans while the closing scan dialog is open, the system matches that ticket to the related active bin/bundle after the dialog closes.
- `Close scanning` stops collection and keeps the temporary evidence available for review and finalization. `Cancel Closing Scan` requires discard confirmation when evidence exists, clears only the temporary closing state, writes an audit event, and must not change persisted sales, inventory, or shift data.
- Any other dormant bundle in that bin should be automatically considered sold during closing reconciliation.

### Settings

Settings owns configuration and system management. This includes store setup, display setup, scanner/display connectivity, Manager/Clerk PIN management, backup options, and any technical diagnostics.

Settings must include state setup and technical setup:

- Follow the same state setup pattern as `../WindowsPOS`.
- Store settings should show store address/details and license registration as two noticeable cards. The registration card should include a Check License action and clear last-check/status text.
- Displays: handles Rdisplay registration, health, config, and diagnostics.
- Scanner: handles scanner pairing, health, config, and diagnostics.
- Settings > Scanner and Display should present scanner controls and display registration as two noticeable cards, with registered displays listed below as individual display cards.
- Settings > Users/PIN lets the active Manager or Clerk change their own PIN. This requires the active user's current PIN plus a different, matching four-digit replacement.
- Only a Manager sees the Clerk reset controls. A Manager may create or reset the optional Clerk login by saving a Clerk name and matching four-digit PIN; the Clerk's previous PIN is not required.
- PIN changes take effect for the next login without ending the current close interval or forcing the active user to log out.
- Backup and email settings belong here when available.
- Game setup does not live under Settings; it belongs under Inventory.

Application upgrade rules:

- Manual upgrade check belongs under Manager Settings > Store / Version Information.
- Automatic app upgrade checks should be startup-only and non-blocking.
- Automatic checks should run only on the scheduled local dates: first-week Monday, second-week Tuesday, third-week Wednesday, and fourth-week Thursday.
- Automatic checks should record the local date they ran and should not repeat for the same date.
- Do not add hourly polling or recurring background wakeups for app upgrade checks.
- Manual Check for Upgrade remains available even when automatic scheduled checks exist.
- Main-branch pushes publish the latest Windows update manifest automatically. A manually dispatched branch build publishes it only when the operator explicitly enables the publish-update-manifest input.

## License Management and Game Name Sync

SimpleLotto should use the same license management mechanism as `../windowsPOS`.

License management rules:

- Use the same device registration / call-home model as `../windowsPOS`.
- Use the same signed license response verification model as `../windowsPOS`.
- Use the same local license state/cache behavior and grace-period approach as `../WindowsPOS`.
- Use the same license-info update mechanism where the server provides a short-lived update token after authorized call-home.
- License check failures should not corrupt local state; preserve the existing valid/grace/expired behavior.
- License management belongs under Manager Settings unless a visible license warning/banner is needed in the shell.

SimpleLotto license expiry lifecycle:

- The signed license response `expires_at` date is the subscription expiration date.
- If a license expires on the 5th day of a month, renewal warning should begin on the 29th day of the prior month when that is 7 days before expiration.
- During the 7 days before `expires_at`, the shell should show a renew-soon banner but the app remains fully usable.
- On the `expires_at` date, the app should show an expired/grace banner and start a 7-day grace period.
- During the 7-day grace period after `expires_at`, SimpleLotto remains usable but should call home automatically in recovery-friendly places so renewal can unlock without requiring a manual Settings action.
- After `expires_at + 7 days`, operational software should lock until a successful authorized call-home renews the cached state. License recovery/settings must remain accessible.
- License refresh should be best-effort and non-destructive; failed refresh attempts must not corrupt a previously valid cached state.
- Automatic license refresh should run non-blocking at startup and at appropriate recovery points such as login or license-sensitive actions when the license is near expiry, in grace, or locked.

State game-name sync rules:

- The license server must manage lotto game names by state.
- Game-name sync is scoped by state and game ID.
- Games are state-wide; the same game ID in different states must be treated as different state/game records.
- Every game-name sync request must include the selected store/state code.
- Before syncing down or uploading a name, check the server entry for that exact state and game ID.
- Never sync a game name across states.
- Local software is the SimpleLotto app/database.
- Local user-defined game names have the highest priority on the local system.
- If the local user has edited a game name, do not overwrite that local name from the server.
- Server game-name entries are authoritative only when the local name is still default/generated or otherwise not user-defined.
- If the license server already has a game-name entry for the selected state and game ID, sync that name from server to local only when local does not have a user-defined name.
- If the license server does not have a game-name entry for the selected state and game ID, and local has a user-edited non-default name, sync the local name to the server.
- When a user edits a game name locally, mark that game name as user-edited/dirty for sync.
- Actual upload should occur through the authorized license-info/game-name sync path, not through an unauthenticated direct call.
- Sync should run after authorized license call-home and may also be triggered from Settings by a Manager action.
- Do not sync default generated names to the server.
- Example: a local default name like `Game 1822` is generic/system-defined and must not be synced.
- Only sync names changed by the user to something other than the system default/generated name.
- Example: if the user changes `Game 1822` to `20X Luck`, that user-defined name can sync to the server only if the server does not already have that state/game entry.
- Do not use local sync to overwrite an existing server game-name entry.
- Game price, the automatic bundle-total rule, and global ticket numbering are local operational configuration and must not be overwritten by this game-name sync unless a future scoped requirement explicitly adds that.
- Game-name sync should be best-effort and must not block sales, closing, or local game setup.
- Sync results and failures should be logged for support.

License server update requirements:

- Add a license-server surface to store lotto game names by state and game ID.
- The server must support reading existing state/game names for client sync.
- The server must support accepting a local user-edited game name only when no state/game entry exists yet.
- The server must reject or ignore default/generated game names such as `Game {game_id}`.
- Server writes should be authenticated through the same license-info update-token style mechanism used by `../windowsPOS`.
- Server responses should let the client distinguish: synced from server, uploaded local name, ignored default name, rejected because server already has entry, and sync failed.

## Rdisplay-Style Mechanism

SimpleLotto should use the same mechanism as `../windowsPOS` Rdisplay where applicable:

- Windows app is the primary controller.
- External displays/devices register with the Windows app.
- The Windows app exposes local endpoints or a local service surface for display/device communication.
- Display clients receive a simple rendered state from the Windows app instead of owning business logic.
- State pushed to displays must come from the same sales/inventory source of truth used by the Windows UI.
- Connectivity, registration, and diagnostics belong under Settings.
- The existing Rdisplay client from `../windowsPOS` should work with SimpleLotto.
- SimpleLotto must keep the Rdisplay wire contract compatible with `../windowsPOS` unless a deliberate migration is documented.
- Rdisplay should receive the latest bundle for a bin when multiple bundles are assigned to that bin.
- Rdisplay tile state should use the calculated ticket range and tickets remaining from the same game/bundle pricing rules used by the Windows UI.
- Rdisplay image/name data should use the same auto-fetched or manually corrected game setup data from Inventory.
- The existing `../windowsPOS` Rdisplay client currently understands `license_status` only; it does not render the desired license expiry/grace banner by itself.
- Any Rdisplay expiry/grace banner needs a deliberate wire-contract migration with explicit banner fields such as expiry date, grace days remaining, banner text, and opacity, plus renderer changes on the display client.
- Until that migration is implemented, SimpleLotto can only send coarse license status to Rdisplay.

The display mechanism should remain simple: display clients show current operational state, while the Windows app owns sales, inventory, closing, settings, and validation.

## Scanner and Runtime Behavior

When a scanner is paired, SimpleLotto must monitor scanner input globally while the application is running.

Scanner rules:

- SimpleLotto should reuse the proven HID/Raw Input capture mechanism from `../windowsPOS`, but must keep its own simpler product and routing behavior. Do not copy WindowsPOS placement, pending-state, or sale business logic.
- Global scanner capture is the default during normal operation.
- Focused/on-demand scan capture is used only inside explicit workflows that ask the user to scan, such as add bundle, inventory receiving, closing scan, setup/import, and correction dialogs.
- Paired scanner input should be monitored regardless of which page is currently visible.
- Scanner input should continue to be monitored when the main window is minimized to the tray.
- If a background scan requires an operator dialog, such as selecting an activation bin or completing missing ticket-price setup, SimpleLotto must restore and foreground the main window before showing that dialog. Configured-game sales that require no operator input should remain background-capable without restoring the window.
- Scanner routing must respect the current workflow state: global normal sale/activation, focused add-bundle capture, focused inventory receiving, focused closing scan, setup/import, or correction.
- Scan events should be captured with timestamp, active user, current close interval/shift reference, raw barcode, parsed meaning, page/workflow state, and result.
- Scanner monitoring should not depend on keyboard focus inside a specific text field.
- If scanner monitoring is unavailable or disconnected, show clear status on Dashboard and Settings.
- Scanner status and pairing diagnostics belong under Settings.
- Settings > Scanner and Display must include barcode scanner pairing and unpairing controls. Pairing should reuse the WindowsPOS HID keyboard-class scanner model: list candidate HID devices, store VID/PID/serial when available, and use that pairing as the prerequisite for background scanner capture.

Capture, classification, and routing contract:

- There is one scanner input layer. It captures raw barcode characters first, identifies the complete scan, classifies it, and only then routes the classified value to the active workflow. Receiving, Closing, startup import, activation, and normal sales must not implement separate scanner stacks.
- A paired scanner uses a background Raw Input message window filtered to the selected HID device identity (VID/PID/serial). It remains active when the window is unfocused or minimized to the tray and must not capture ordinary keyboard text from another device.
- A keyboard-class scanner scan is grouped by its configured terminator, not by inter-character timing. Accumulate the complete key sequence and dispatch it only when the scanner sends Enter/Tab (the usual CR/LF-style keyboard-wedge suffix). Do not split or emit barcodes using 50 ms/400 ms burst or idle heuristics. A paired partial sequence with no terminator may be discarded after five seconds, but it must never be emitted as a barcode fragment.
- The five-second incomplete-raw-buffer cleanup is independent from the configurable bin/bundle activation scan-pair window. The default activation scan-pair window remains five seconds and groups valid bin, ticket/bundle, and price inputs for one placement workflow.
- Unpaired fallback has no device identity. It may handle a scanner while the app is focused, but it must not be relied on for simultaneous/interleaved use of two scanners; pair the operating scanner for background and device-isolated capture.
- The supported non-ticket command labels are `BIN-<1-4 digits>` and `PRICE-<1-5 digits>`. A price label payload is cents. Ticket scans must pass the configured state barcode parser. Any other character sequence is not a SimpleLotto barcode and must never be interpreted by stripping characters or extracting a numeric suffix.
- Normal text fields, password fields, search fields, rich-text fields, and ordinary number entry are excluded from app-wide unpaired fallback capture. The activation and receiving game-price fields are the explicit exception: they may observe a fast valid `PRICE-...` label and place the resulting value in that same price field while preserving normal manual entry.
- Workflow routing is determined after classification: receiving and closing accept ticket barcodes only; startup import accepts ticket and bin labels; normal sales/activation can use ticket, bin, and price labels according to the current placement state.
- Every rejected captured scan must show a concise scan error, write an audit entry with the raw value and reason, and speak the short prompt `Scan again.` A workflow expecting a ticket may instead speak the equally short `Ticket only.` Duplicate bundle scans remain an audio-only `Duplicate` no-op.

Audit rules:

- Audit is part of the operational accountability surface, not optional diagnostics.
- Record scanner activity, rejected/unrecognized scans, sale records, bundle activations, bin placements, opening/initial placements, corrections, settings changes, login/logout, display registration changes, license checks, and closing finalization.
- Audit entries should include enough detail to reconstruct what happened: active user or system actor, timestamp, workflow/source, game ID, bundle ID, bin, ticket/range, quantity, amount, next ticket where relevant, and failure reason when rejected.
- Focused receiving and closing sessions must audit session start, accepted scans, rejected scans, cancellation/close outcome, finalization, and reconciliation decisions. Receiving duplicates remain the explicit exception because they are audio-only no-op input.
- Game price/name/image setup changes and inventory removals must be audited after successful persistence. Audit detail must state that inventory removal preserves prior sales and activation history.
- Audit write failures must not block clerk workflow, but they must be logged to the application log so the failure itself can be diagnosed.

Application lifetime rules:

- While SimpleLotto is running, including when minimized to the tray, Windows idle sleep must be blocked so scanner, display, and shift workflows continue uninterrupted. Release the sleep-prevention request when the application actually exits.
- When a barcode scanner is paired, closing the main window must not exit the application by default.
- When a barcode scanner is paired, window close should minimize SimpleLotto to the Windows system tray so scanner/display/speech services continue running.
- When no barcode scanner is paired, normal window close may exit the application unless another active background service requirement is added later.
- The tray icon should allow the user to restore the window.
- The tray icon should provide an explicit Exit command.
- If an unclosed interval has activity, Exit should require confirmation and make the consequence clear.
- Background scanner/display services should continue while the app is minimized to tray.

## Backend and Storage Direction

SimpleLotto is single-computer software. Use SQLite as the primary local backend unless a later requirement proves it insufficient.

Storage rules:

- Use one primary SQLite database for active records.
- The accounting boundary is an explicit close-interval record created by the previous successful close and closed by the current successful close. Timestamps describe when events occurred; they must not be used to reconstruct interval membership.
- Every interval has a persistent ID and status. SQLite must enforce that exactly one interval is open during normal operation.
- A stored `shift_id`, `shift_number`, or close interval ID must represent that close interval and must not be derived from login.
- Sales, activation events, closing history, report outbox jobs, and other financial/reportable rows must reference their interval ID directly. A Windows clock rollback must not move a committed row into or out of the current interval.
- Manager, Clerk, System, and legacy-migration actors have stable IDs independent of display names. Financial rows retain both the stable actor ID and the recorded display name where a human-readable snapshot is useful.
- The user-facing shift reference should use local closing date plus an incremental number for that date.
- Do not partition the primary database by month because close intervals can cross calendar boundaries.
- Calendar month/year filters are reporting views, not storage boundaries.
- Keep up to eight years of records.
- If archival is needed later, archive closed intervals into yearly SQLite archive files, not monthly operational files.
- Keep sales ledger rows and inventory ledger rows separate even when they share the same close interval.
- Every financial/reportable row should record close interval/shift reference when known, active user, timestamp, and source.
- Every inventory row should record close interval/shift reference when known, bundle, bin, movement type, timestamp, and source.

Close-interval storage rule:

- Closing creates the immutable financial summary for the explicitly open interval.
- The closing transaction records its closing history/outbox rows, closes that interval, and creates the next open interval atomically.
- Once a close interval is closed, it is closed and should not be edited.
- Fixes after close happen only in a later/current open interval as corrective actions.
- Every sale has a persistent sale ID, interval ID, actor ID, actor-name snapshot, timestamp, and source. Sale rows are append-only; they are never deleted or updated to correct accounting.
- Undo/void creates one negative correction row in the current open interval that references the original sale ID. The original sale remains unchanged, and each original sale can have at most one correction.
- A physical ticket claim references the persistent sale ID that first claimed it. Voiding a sale does not release or delete that claim, so the same ticket cannot later be sold again.
- Cash summary data comes from sales/payment activity.
- Each closing record must persist the user-facing shift label and the report folder path created for that close.
- The shift label sequence resets by local closing date, for example `2026-07-08 #1`, `2026-07-08 #2`, not by lifetime application closing count.
- Inventory overview data comes from inventory/count activity.
- Reports may display both, but database summaries must keep them separate.

## WindowsPOS Replacement and Migration

SimpleLotto is intended to replace an existing `../windowsPOS` installation for users who install it in place.

Replacement rules:

- SimpleLotto should be installable as the forward replacement for WindowsPOS.
- SimpleLotto should use the same Rdisplay mechanism so existing display clients can continue working.
- SimpleLotto should keep Reporting and Email behavior familiar to WindowsPOS users.
- SimpleLotto should not write new operational data into the old WindowsPOS database.
- SimpleLotto must create and use its own new database file for this version.
- SimpleLotto does not need to expose legacy WindowsPOS history.
- Legacy WindowsPOS records are not part of the v1 scope.
- Current SimpleLotto operations, close intervals/shifts, sales, inventory, closing, settings, and reports must use the new SimpleLotto database.

Migration/setup rules:

- Detection should not block first-install Manager setup.
- The user should be informed that SimpleLotto will create a new active database.
- Legacy data access should not be built for v1.
- Do not silently merge old WindowsPOS records into the active SimpleLotto ledger.
- Do not spend implementation effort on reading or displaying old WindowsPOS records unless a future scoped requirement reintroduces it.

## Delivery Plan

The first implementation deliverable should focus on the operational core:

1. Rdisplay compatibility
2. Bins
3. Closing
4. Settings
5. Reporting

Dashboard remains the home screen in the final navigation, but the first deliverable should prioritize the underlying workflows that make Dashboard useful.

Do not add Reporting as a sixth top-level menu item. Reporting should be reachable from Closing only while preserving the required five-item navigation: Dashboard, Bins, Inventory, Closing, Settings.

## First Deliverable Architecture Rules

Use a modular monolith architecture for the first deliverable. SimpleLotto is a single-computer app with local storage, scanner input, and local display/device integration; microservices would add operational complexity without value.

Recommended module boundaries:

- Shell/UI: navigation, page hosting, dialogs, tray behavior, role-aware page visibility.
- Auth/Closing Intervals: first-run setup, Manager/Clerk login, current close interval state, close interval numbering, and closed-interval immutability.
- Scanner: scanner pairing, global scan capture, barcode parsing, scan audit, workflow routing.
- Bins: bin state, multi-bundle bin model, current/latest bundle selection, dormant bundle tracking.
- Inventory: game setup, bundle setup, activation, open inventory, taken-out-of-inventory handling.
- Closing: scan capture dialog, post-scan reconciliation loop, gap-fill sold, corrective actions, finalization.
- Reporting/Email: last 7 closings UI, report generation, export files, email send/status.
- Rdisplay: Windows-side API/snapshot/event contract compatible with `../windowsPOS` Rdisplay.
- License/Game Sync: license call-home, signed response validation, game-name sync by state, license-server update-token posts.
- Persistence: SQLite schema, repositories, migrations, backup/restore, audit trail.
- Settings: state setup, scanner/display, email, TTS. Game prices, automatic bundle totals, and global ticket numbering remain under Inventory.

Dependency rules:

- UI code should call application services, not write directly to SQLite.
- Domain/application services own business rules for closing, close intervals, bins, and inventory.
- Repositories own database access and transactions.
- Rdisplay, scanner, email, image fetch, license sync, and text-to-speech should be adapters around domain services.
- Modules should communicate through public service interfaces, not by reaching into each other's internal state.

Transaction and consistency rules:

- Closing finalization must be transactional.
- Closing finalization should either complete all sale gap-fill, inventory state changes, report records, and audit rows, or fail without partial finalization.
- Report/email sending must not be part of the critical transaction. If email fails, the close interval remains closed and the email failure is recorded.
- The closing transaction must persist a pending report/outbox job containing an immutable snapshot of the report inputs. Report files are generated only after that transaction commits; generation failure must leave the shift closed and the job retryable after restart.
- Scan capture can be append-only during the dialog. Reconciliation applies business changes after scan collection.
- Closed intervals are immutable; later fixes are corrective actions in a later/current open interval.

SQLite/schema requirements for first deliverable:

- Schema migrations must be versioned.
- A migration that assigns ledger identities to an existing database must first create an online SQLite backup beside the active database in a migration-backup folder.
- Historical interval inference may use stored timestamps only during migration and must verify each inferred group against its saved closing row count, ticket count, and sales cents. Exact matches become verified closed-interval history; mismatches are preserved in a `legacy_unresolved` interval and written to both a structured migration-conflict table and Audit.
- Historical ticket claims must be rebuilt from persistent sale rows. Duplicate, malformed, quantity-mismatched, missing-bundle, orphan-claim, and ambiguous-void history must be preserved and reported as migration conflicts rather than silently rewritten or discarded.
- Use explicit tables for users, close intervals/shifts, games, bundle-type rules when introduced, bundles, bins, bin bundle state, scan events, sales ledger, inventory ledger, closing records, closing reconciliation issues, reports, settings, display registrations, and audit log.
- Use cents/integer money values, not floating point, for all prices and totals.
- Store timestamps in UTC and display in local time.
- Add indexes for `shift_id`/`close_interval_id`, `game_id`, `bundle_id`, `bin_id`, `closed_at`, and scan timestamp.
- Keep sales ledger and inventory ledger separate.
- Enforce append-only sales, ticket claims, activation events, closing history, and closed intervals at the SQLite boundary, not only in UI code.
- Record source for generated rows, such as normal sale, undo, closing gap-fill sold, closing correction, and inventory removal.
- Store unopened receiving records separately from active bin placement records, or use explicit movement/source types that cannot be confused. Initial import, regular activation, receiving, movement, and closing reconciliation must remain queryable as distinct inventory sources.

Contract/versioning rules:

- Rdisplay wire contract must remain compatible with `../windowsPOS` unless a deliberate migration is documented.
- Settings and database schema should carry version numbers.
- Barcode parser behavior should be centralized and covered by tests.
- Report file names and email attachment behavior should follow `../windowsPOS` compatibility rules.

Reliability and recovery rules:

- On startup, recover cleanly from an app crash while minimized, during scanner monitoring, or after a failed email.
- If the app crashes during a closing scan dialog before final submit, captured scan evidence may be discarded or recovered, but it must not partially close the interval.
- If the app crashes after final closing transaction commits but before reports/email finish, the close interval remains closed and reports/email can be retried.
- Database backups must use SQLite's online backup API against the live connection. Do not rely on a passive WAL checkpoint followed by copying only the main database file, because committed WAL frames must be included in the backup snapshot.
- Backups should be created at close or immediately after a successful close.
- Backup/restore behavior should be part of Settings or a manager-only maintenance surface.

Security rules:

- Manager/Clerk PINs must be stored as versioned, salted PBKDF2-HMAC-SHA256 hashes, never plaintext. New hashes store their format version and work factor so they can be upgraded later.
- Existing salted SHA-256 login hashes remain readable only for backward-compatible verification during the PIN migration release. After any successful legacy login, require a different four-digit PIN and replace only the selected account's stored hash with the current PBKDF2 format before completing login.
- Do not silently reuse an existing four-digit legacy password as the new PIN. The required migration must collect and confirm a different PIN so the password change is explicit.
- Keep the existing `manager_password_hash` and `clerk_password_hash` setting keys for database compatibility even though the product terminology is PIN.
- Email SMTP passwords and display tokens must be stored securely.
- Scanner/display diagnostics visible to Clerk must not expose secrets.
- Audit privileged actions: setup, login, closing, corrections, settings changes, display pairing, and inventory removal.

First deliverable verification targets:

- Closing scan dialog records scans immediately and shows count without perceptible lag.
- Text-to-speech prompt starts within 500 ms of workflow state change under normal local conditions.
- Dashboard undo is no-op when no current-interval sale exists.
- Closing finalization for a normal interval completes within 3 seconds for a small-store data set.
- Rdisplay receives the latest bundle state for active bins after bin/inventory changes.
- Report generation for one closed interval completes within 5 seconds for a small-store data set.
- Email failure does not prevent a close interval from being closed.
- Last 7 closings are visible from Closing; older closings are not exposed in current UI scope.

## Microsoft / WinUI Project Rules

Use the Microsoft WinUI guidance for this project:

- Build as a WinUI 3 Windows app.
- Use WinUI platform controls before custom controls.
- Use `NavigationView` or an equivalent WinUI left-navigation pattern for the collapsible left menu.
- Keep collapsed navigation icons visible with tooltips.
- Use `ListView` with grid-based item templates for tabular data.
- Use `NumberBox`, `ComboBox`, `TextBox`, `ToggleSwitch`, tabs, dialogs, and InfoBars where they fit.
- Use theme resources for colors and support Light, Dark, and High Contrast.
- Build and package validation runs through `.github/workflows/build-windows.yml` on GitHub Actions; local development does not require Windows or PowerShell.
- Treat the GitHub Actions Windows build as the compile/package authority for this repository.

Before implementing major XAML screens, use the WinUI/Microsoft skill guidance to map each requirement to platform controls.

## Reporting and Email Reuse

SimpleLotto should keep the Reporting structure and Email behavior from `../windowsPOS` where compatible with the simplified product scope.

Reuse targets from `../windowsPOS`:

- Closing report generation pattern.
- Closing report folder/export structure.
- CSV report exports.
- PDF closing report export.
- Reports repository/query structure.
- SMTP/email settings structure.
- Closing-report email send flow.
- Email status tracking on closings.

Expected closing/report artifacts should follow the `../windowsPOS` pattern unless a SimpleLotto-specific reason changes them:

- `shift_summary.csv`
- `inventory.csv`
- `sales_detail.csv`
- `corrections.csv`
- `anomalies.csv`
- `placement_events.csv`
- `bin_assignments.csv`
- `initialization.csv`
- `closing_audit.csv`
- `closing_report.pdf`

Closing report folder rules:

- Closing finalization must create an on-disk report folder for that specific close interval.
- Report folders must be named by local closing date plus that day's shift sequence, for example `2026-07-08_shift-001`, `2026-07-08_shift-002`.
- The daily shift sequence is scoped to the local closing date. It is not a lifetime counter from first install.
- The Closing history row for each close must provide an `Open Reports` action that opens that closing's stored report folder.
- Existing closes that predate report-folder persistence may appear without an available report folder action.
- If PDF generation is not yet available in the scaffold, a text closing report may be generated temporarily, but `closing_report.pdf` remains the target artifact.

Manual closing totals:

- Before finalizing a close, the clerk must enter `Online Sale`, `Online Cashout`, and `Instant Cashout`.
- All three values are dollar amounts and may be zero.
- Expected cash is calculated as `instant_ticket_sales + online_sale - instant_cashout - online_cashout`.
- Manual totals and expected cash must be persisted on the closing record and included in the closing report summary.

SimpleLotto may omit or de-emphasize day-level reporting where it conflicts with shift-to-shift accounting. If a day summary is kept for compatibility, it must be clearly secondary to shift closing and must not become the financial boundary.

Email rules:

- Email settings live under Settings.
- Email report recipients are configured once and reused by shift closing.
- Closing email can send CSV reports and the PDF closing report when available.
- The user must be able to choose which closing artifacts are included or excluded from the closing email as a global Settings > Email preference, not as a per-closing choice.
- Global closing email content choices should include each standard artifact separately: shift summary, inventory, sales detail, corrections, anomalies, placement events, bin assignments, initialization, closing audit, and PDF closing report.
- Email failure must not invalidate a completed close.
- Email status should be visible in Reporting/Closing history.
- Manual test email should be available from Settings.

Reporting rules:

- Reporting is accessed from Closing, not Settings and not a separate top-level menu item.
- The user-facing Reporting/Closing history UI should show only the last 7 closings.
- Older closing reports may remain stored/exported, but access to older reports is outside the current scope.
- Reporting must preserve the separation between sales numbers and inventory numbers.
- Reporting must support shift-to-shift financial summaries first.
- Future partition summaries may group by shift, user, calendar period, game, or bin.
- Partition summaries must not redefine the financial boundary; they are reporting views over closed-shift records.

## Audio and Text-to-Speech

SimpleLotto must include text-to-speech for scanner-driven workflows, especially closing.

Text-to-speech rules:

- Prompts must be real-time or near-real-time.
- Closing prompts such as "start scanning" and "scan next ticket" must play immediately when the workflow state changes.
- Do not repeat the delayed audio behavior seen in `../windowsPOS`, where closing audio can lag by several seconds.
- Prefer pre-initialized Windows speech resources or a queued low-latency audio service so the first prompt is not delayed.
- Default speech rate should be faster than normal conversational speech so prompts finish quickly.
- Target roughly 170-190 words per minute for standard prompts, assuming healthy adult operators who can understand average human tempo.
- Do not make prompts so fast that numbers, bin IDs, game IDs, or correction warnings become ambiguous.
- Audio prompts should be short and actionable.
- Text-to-speech failure must not block scanning or closing.
- Provide a Settings control to enable/disable speech prompts and adjust volume if practical.

## Design Direction

- Prioritize dense, clear operational screens over decorative layout.
- Use WinUI platform controls and theme resources.
- Keep pages scan-friendly for repeated counter use.
- Avoid extra top-level navigation.
- Avoid implementing POS complexity from `../windowsPOS` unless it directly supports sales, inventory, closing, or settings.

## Current Build Note

The existing scaffold is only a starting point. Future implementation should reshape it around the five required menu items and the sales/inventory/closing/settings workflow described here.
